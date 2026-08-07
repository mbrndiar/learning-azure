# 📨 6. Dispatch processing work

> **Read** this page, **run** the companion in
> [`DispatchYard/`](DispatchYard/) against Azurite, **practise** in
> [`exercises/06-queue-storage/`](../../exercises/06-queue-storage/), then work
> the paired [CLI](../../infra/azure-cli/queue-storage.sh) and
> [PowerShell](../../infra/powershell/queue-storage.ps1) labs.
> Prerequisites: [module 3](../03-storage-account/README.md) and Docker. No
> Azure subscription is needed for any part of this module.

## Objectives

By the end of this module you can:

- **implement** a consumer that stays correct under **at-least-once delivery**,
  where the same work order is handed out two or three times;
- **configure** the **visibility timeout** and a **dequeue count** budget so
  slow work is not duplicated and stuck work is quarantined as a
  **poison message** rather than replayed forever; and
- **compare** competing-consumer dispatch with a partitioned event stream and
  justify, from stated requirements, which one a workload needs.

## The question this module answers

Modules 3 to 5 stored artifacts. Something still has to *process* them, and the
processing outlives the HTTP request that triggered it: thumbnails, checksums,
ingest, indexing. The obvious design is to do the work inline and the obvious
design does not survive a restart.

> **A field laptop uploads 400 observations. The ingest worker is restarted
> mid-batch. How many observations are ingested — and how do you know it is not
> 512?**

The answer turns on a fact people meet in production rather than in
documentation: **the queue guarantees delivery, not uniqueness.** A message may
be delivered more than once, this is normal, and there is no setting that turns
it off. Making the *effect* happen once is the consumer's job.

## A message is a pointer, not a payload

A Storage queue is deliberately unambitious. It has no schema, no partitions, no
consumer groups, no ordering guarantee, and no subscriptions. It has one flat
backlog of small messages and four verbs: send, receive, delete, peek.

| property | value | why it matters |
| --- | --- | --- |
| maximum message size | 64 KiB message body | large payloads belong elsewhere |
| default time to live | 7 days | a backlog nobody drains expires silently |
| maximum visibility timeout | 7 days | the ceiling on "I am still working on this" |
| ordering | none guaranteed | competing consumers reorder by construction |
| delivery | at least once | duplicates are a normal outcome, not a fault |

The size limit is the design constraint that shapes everything else. A message
is a **pointer to work**, not the work: the observation goes in a blob and the
message carries its name. Module 4 built exactly that name.

### Base64 is this course's explicit codec policy

`Azure.Storage.Queues` v12 defaults `QueueClientOptions.MessageEncoding` to
`None`; it does **not** Base64-encode by default. This course deliberately uses
Base64 so producers and consumers share one unambiguous wire format. Base64
emits four bytes for every three it consumes, so under that application policy
the usable raw payload is smaller than the service's 64 KiB message-body limit:

```text
 49152 raw bytes ->  65536 encoded bytes -> fits
 50176 raw bytes ->  66904 encoded bytes -> REJECTED
 61440 raw bytes ->  81920 encoded bytes -> REJECTED
```

That table is printed by the companion, not recited from documentation. A
validation that checks raw JSON against 64 KiB can accept a 60 KiB payload even
though this codec turns it into an 80 KiB body. A client using the SDK's `None`
default would not pay that expansion, but should still keep work in Blob
Storage and put only its durable name on the queue.

## The message lifecycle, as a state table

Every duplicate-delivery bug in this module is a path through this table:

| state | how it is entered | how it is left | visible to consumers? |
| --- | --- | --- | --- |
| **Queued** | `SendMessage` | a consumer receives it | yes |
| **Invisible** | `ReceiveMessage` starts the visibility timeout | delete, or the timeout expires | no |
| **Requeued** | the visibility timeout expired before a delete | received again, `DequeueCount` + 1 | yes |
| **Deleted** | `DeleteMessage` with a matching pop receipt | terminal | no |
| **Quarantined** | the consumer moved it aside after N deliveries | terminal, by your code | no |
| **Expired** | time to live elapsed | terminal, silently | no |

Two of those six transitions are yours. The service will never quarantine a
message for you — a Storage queue has no dead-letter queue, unlike Service Bus —
and it will never tell you the effect already happened.

### Receiving is not removing

`ReceiveMessages` hides a message; it does not remove it. Deleting is a separate
call, and that separation is the entire fault-tolerance story: a consumer that
crashes after receiving and before deleting loses nothing, because the message
becomes visible again by itself.

The proof, from the companion, against Azurite. Message ids, pop receipts, and
the UTC timestamps are minted per run, so yours will differ; this is one
representative occurrence.

```text
   Received id          : a2f0012d-6b63-4302-a258-821f3fb0a714
   Dequeue count        : 1
   Invisible until (UTC): 2026-08-07 10:45:05Z
   ApproximateMessagesCount while it is hidden: 1
   The message still counts toward the depth. It is invisible, not gone.
   ApproximateMessagesCount after delete     : 0
```

The depth does not drop when the message is received. It drops when it is
deleted. A dashboard that treats queue depth as "work remaining" is right; one
that treats it as "work not started" is wrong.

### The pop receipt is proof of *this* receive

`DeleteMessage` needs a message id **and** a pop receipt. The receipt changes on
every receive, so a consumer holding a stale receipt cannot delete the message
another consumer is now working on. That is the protection you want, and it is
also why the delete in a slow handler fails with `MessageNotFound` rather than
silently destroying someone else's work.

## The visibility timeout is a bet

When you receive a message you tell the service how long you expect to need. It
is a bet, and losing it has a specific, observable cost:

- **too short** — the message reappears while the first handler is still
  running, and two consumers do the same work;
- **too long** — a consumer that crashes parks the work for that long before
  anybody else may touch it.

There is no correct value, only a value derived from how long the handler
actually takes. Setting the timeout to the *expected* duration means half of all
runs exceed it, so the exercise's planner multiplies by a safety factor and
caps at the service maximum of seven days.

### At-least-once, observed

This is the companion losing that bet deliberately: a 1-second visibility
timeout and a handler that takes 1.5 seconds. The message id is minted per run;
the `DequeueCount` progression is the point.

```text
   Visibility timeout   : 1.0s
   Handler duration     : 1.5s (deliberately longer)

   Attempt 1: id 055885ca... DequeueCount=1 (same message: first delivery)
   Attempt 2: id 055885ca... DequeueCount=2 (same message: REDELIVERED)
   Attempt 3: id 055885ca... DequeueCount=3 (same message: REDELIVERED)
```

Nothing failed. Nothing threw. No retry policy ran. The message came back three
times purely because work was still in flight when the window expired — and if
the handler had a side effect, that side effect happened three times.

`DequeueCount` is the service telling you how many consumers have already been
handed this work. It is the single most useful number in a queue-based system,
and it is the input to every poison-message decision below.

## At-least-once is your problem

Since the service will deliver twice, the consumer must make the *effect* happen
once. There are only three honest strategies:

1. **Make the operation naturally idempotent.** Writing a thumbnail to a
   deterministic blob name is idempotent; appending a line to a log is not.
2. **Claim the work before doing it.** An atomic first-writer-wins record keyed
   by the work identity — module 5's `If-None-Match: *` write is exactly this,
   and so is a table entity insert.
3. **Make the effect conditional on state you already own.** "Set status to
   ingested if it is pending" is safe to repeat; "increment the counter" is not.

The exercise implements the second, because it is the one that composes with
effects you do not control.

### Deduplicate on the work id, never the message id

The message id identifies the **queue entry**. Re-sending the same payload
produces a different id, so a message-id cache catches redelivery of one queue
entry and nothing else. A producer that retried its own send — after a timeout,
say — creates a genuinely new entry carrying work that has already been done,
and it walks straight through.

The **work order id** is chosen by the producer and travels with the work
through every retry, every redelivery, and every re-enqueue. That is the key to
claim against.

### Poison messages: fail fast on the deterministic ones

A message that fails identically every time will fail forever, and the queue
never drains behind it. Two rules cover it:

- **A message that cannot be decoded is poison on the first delivery.** No
  amount of retrying will make invalid Base64 valid. Quarantine it immediately.
- **A message whose handler keeps failing is poison after N deliveries.** Check
  `DequeueCount` *before* doing any work, so an over-budget message costs one
  cheap check rather than one more full failure.

Storage queues have no dead-letter queue, so "quarantine" means what your code
makes it mean: a separate `…-poison` queue, a blob, or a table row. What matters
is that it leaves the backlog and that a human can find it.

## A queue is not a stream

The last outcome is a judgement, and it is the one most often made backwards.

| | Storage queue | partitioned event stream |
| --- | --- | --- |
| a message is consumed by | exactly one consumer | every consumer group |
| after processing | deleted, gone | retained until the retention window ends |
| replay | impossible | seek to an earlier offset |
| ordering | none | per partition |
| scaling unit | more competing consumers | partitions |
| the question it answers | "who will do this work?" | "what happened, in order?" |

Neither can be configured into the other. A queue message is destroyed when it
is handled, so no amount of tuning gives you replay; a stream keeps every event,
so no amount of tuning gives you a shrinking backlog of outstanding work.

The rules the exercise encodes, in order: **replay required → stream**;
**per-key order required → stream**; **more than one independent consumer per
item → stream**; **otherwise → queue**. Throughput is not on that list.
"High volume" justifies neither, and it is the reason most often given.

## Run the companion

```bash
docker compose up -d azurite
dotnet run --project lessons/06-queue-storage/DispatchYard
```

Real output, captured from a run against Azurite 3.36.0. Message ids, pop
receipts, and UTC timestamps are minted per run, so this is one representative
occurrence; every count, code, and `DequeueCount` in it is reproducible.

```text
1. What a queue message is
--------------------------
   Sent message id      : a2f0012d-6b63-4302-a258-821f3fb0a714
   Pop receipt          : MDdBdWcyMDI2MTA6NDQ6MzUwNjhk
   Inserted (UTC)       : 2026-08-07 10:44:35Z
   Expires  (UTC)       : 2026-08-14 10:44:35Z
   Default lifetime     : 7.00:00:00
   The message id identifies the QUEUE ENTRY, not the work. Re-sending the
   same payload produces a different id, which is why deduplication keys off
   the producer-chosen work order id instead.

2. Receive hides; delete removes
--------------------------------
   Received id          : a2f0012d-6b63-4302-a258-821f3fb0a714
   Dequeue count        : 1
   Invisible until (UTC): 2026-08-07 10:45:05Z
   ApproximateMessagesCount while it is hidden: 1
   The message still counts toward the depth. It is invisible, not gone.
   ApproximateMessagesCount after delete     : 0

3. At-least-once, observed
--------------------------
   Visibility timeout   : 1.0s
   Handler duration     : 1.5s (deliberately longer)

   Attempt 1: id 055885ca... DequeueCount=1 (same message: first delivery)
   Attempt 2: id 055885ca... DequeueCount=2 (same message: REDELIVERED)
   Attempt 3: id 055885ca... DequeueCount=3 (same message: REDELIVERED)

   The DequeueCount is the service telling you how many consumers have
   already been handed this work. Nothing here was retried on error: every
   redelivery is purely the visibility timeout expiring while work was in
   flight.
   Deleting with the newest pop receipt succeeded.

4. Peeking does not claim
-------------------------
   Peeked 4e81b38c... DequeueCount=0 body={"workOrderId":"wo-3001"}
   Peeked a96755ab... DequeueCount=0 body={"workOrderId":"wo-3002"}
   Peeked da01fc3f... DequeueCount=0 body={"workOrderId":"wo-3003"}
   Peek returns no pop receipt, so a peeked message cannot be deleted, and
   its DequeueCount does not advance. Peek is for dashboards, not consumers.
   ApproximateMessagesCount: 3 (named 'Approximate' because it is a snapshot, not a lock)

5. The course Base64 policy reduces the raw-payload ceiling
-----------------------------------------------------------
    49152 raw bytes ->  65536 encoded bytes -> fits
    50176 raw bytes ->  66904 encoded bytes -> REJECTED
    61440 raw bytes ->  81920 encoded bytes -> REJECTED
   Under this explicit codec policy, usable raw payload is about 48 KiB.
   The SDK default is None. Keep large work in a blob and queue only its name.
```

The companion deletes its queue on the way out, so it can be run repeatedly.

## The management labs

Same ten steps, twice, so the shape survives whichever tool your team uses:

```bash
docker compose up -d azurite
bash infra/azure-cli/queue-storage.sh
```

```bash
docker compose up -d azurite
pwsh -File infra/powershell/queue-storage.ps1
```

Step 5 waits out a 5-second visibility timeout and receives the same message
again — the redelivery from section 3, at the command line. Step 6 then tries
to delete it with the *first* pop receipt and is rejected, which is the
protection that makes a stale consumer harmless.

Both scripts print the endpoint they are about to write to before writing
anything, and both delete their queue at the end.

## A bounded experiment

Fifteen minutes, two observable answers.

1. In `DispatchYard/Program.cs`, change the visibility timeout in
   `ShowRedeliveryAsync` from `TimeSpan.FromSeconds(1)` to
   `TimeSpan.FromSeconds(5)`, leaving the 1.5-second handler alone.
2. Re-run `dotnet run --project lessons/06-queue-storage/DispatchYard`.

Observed result (the message id is minted per run):

```text
   Visibility timeout   : 5.0s
   Handler duration     : 1.5s (deliberately longer)

   Attempt 1: id bd414c12... DequeueCount=1 (same message: first delivery)
   Attempt 2: nothing visible yet.
   Attempt 3: id bd414c12... DequeueCount=2 (same message: REDELIVERED)
```

Widening the window did not eliminate redelivery — it *halved* it. The handler
now finishes inside its window (attempt 2 sees nothing), but the loop never
deletes the message, so it comes back once the five seconds are up. That is the
honest result: a bigger timeout buys time, not correctness. The only thing that
ends a delivery is a delete.

3. Now try to make the window smaller than the service allows: set it to
   `TimeSpan.FromMilliseconds(500)`.

Observed result — a real rejection from Azurite, before a single message moves:

```text
The service rejected a request: OutOfRangeQueryParameterValue (HTTP 400).
QueryParameterName: visibilitytimeout
QueryParameterValue: 0
MinimumAllowed: 1
MaximumAllowed: 604800
```

The SDK sends the timeout as whole seconds, so 500 ms becomes `0` on the wire
and the service refuses it. The permitted range is one second to seven days, and
it is stated in the error rather than left to documentation.

Then revert both edits.

The lesson is uncomfortable: **duplicate delivery is a tuning outcome, not an
error condition.** You can make it rarer, and the service will not even let you
tune below one second. You cannot make it impossible, which is why the consumer
must be correct when it happens anyway.

## Common mistakes and how to diagnose them

| symptom | likely cause | how to confirm |
| --- | --- | --- |
| `RequestBodyTooLarge` under this course's Base64 codec, but raw JSON is 60 KiB | Base64 expanded the body past the 64 KiB service limit | compute `4 * ceil(bytes / 3)` and compare it with 65536; do not mistake Base64 for the SDK default |
| the same work is done two or three times | the handler outlives its visibility timeout | log `DequeueCount` on every receive; anything above 1 is a redelivery |
| duplicates persist even with a dedupe cache | the cache is keyed on `MessageId`, which changes per enqueue | log both ids; a re-sent work order shows a new message id and the same work order id |
| `MessageNotFound` on delete | the pop receipt is stale — the message was redelivered while you worked | compare `NextVisibleOn` from the receive against the wall clock at the delete |
| the queue never drains and depth is flat | one poison message failing forever ahead of the backlog | peek the head; check its `DequeueCount` |
| depth stays high while workers look idle | messages are invisible, not absent; something receives and never deletes | `ApproximateMessagesCount` counts invisible messages, so compare it with a peek |
| messages vanish overnight | the 7-day time to live expired | check `ExpirationTime` on send; it is not unbounded |
| ordering assumptions fail under load | competing consumers reorder by construction | you need a stream, not a queue — see [A queue is not a stream](#a-queue-is-not-a-stream) |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/06-queue-storage/tests -p:Implementation=starter

```

The starter has ten numbered gaps, in dependency order: message encoding and the
encoded size limit (GAPs 1–2), the visibility-timeout planner (GAPs 3–4), the
idempotent dispatcher with its poison rules (GAPs 5–8), and the queue-versus-
stream judgement (GAPs 9–10). Each throws a `NotImplementedException` naming the
section of this page that derives it.

**Untouched-starter baseline: fails.** 62 of 67 checks fail, the first with:

```text
System.NotImplementedException : GAP 1: implement WorkOrderCodec.Encode.
See lessons/06-queue-storage/README.md#a-message-is-a-pointer-not-a-payload.
```

That failure is your next action, not a repository defect. (The five passing
checks read published constants and reject invalid constructor arguments, both
of which the starter already provides.)

The evaluator is deterministic and offline. Delivery semantics are modelled by
handing the dispatcher the same work order under different message ids and
dequeue counts — which is exactly what the service does — so nothing here needs
Azurite, and nothing here is timing-dependent.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| the claim is made against `message.MessageId` instead of `order.WorkOrderId` | 2 failures: `TheClaimIsMadeAgainstTheWorkOrderIdNotTheMessageId` — *Expected: "wo-1", Actual: "msg-zzz"*; `TheSameWorkReEnqueuedUnderANewMessageIdIsStillDeduplicated` — *Expected: ["wo-1"], Actual: ["wo-1", "wo-1"]* |
| an undecodable message is retried until the delivery budget is spent | 2 failures: `AnUndecodableMessageIsQuarantinedOnTheFirstDelivery` and `AnUndecodableMessageIsNeverRetried`, both *Expected Quarantine, Actual Retry* |
| `Encode` checks the JSON length against the limit instead of the Base64 length | 2 failures: `AnOrderThatOnlyFitsBeforeEncodingIsRejected` and `TheRejectionSaysWhatToDoInstead`, both *Assert.Throws() Failure: No exception was thrown* |

The first fault is the one to notice: it still deduplicates, it still passes the
obvious "a redelivered message is not processed twice" check, and it fails only
for work that was re-enqueued rather than redelivered — the case that actually
loses money in production.

## Environments

- **Emulator.** `docker compose up -d azurite` for the companion and for both
  management labs. The exercise evaluator needs nothing running.
- **Live checkpoint: not required.** Azurite implements the queue behaviour this
  module teaches — visibility timeouts, dequeue counts, pop receipts, and
  redelivery — faithfully enough that a live account would show you nothing
  new. Contrast [module 5](../05-blob-lifecycle/README.md#environments), where
  the emulator genuinely cannot answer the question.

## Review questions

1. A message body of 60 KiB of JSON is rejected on send. State the exact size
   the service saw and the arithmetic that produced it.
2. Your handler takes 40 seconds at the 99th percentile and 8 seconds typically.
   Choose a visibility timeout, and say what goes wrong at 8 seconds and what
   goes wrong at 7 days.
3. A message is processed twice. Nothing threw, no retry policy ran, and the
   logs show `DequeueCount=1` then `DequeueCount=2`. What happened?
4. Why does deduplicating on `MessageId` pass a redelivery test and still lose
   money in production?
5. `DeleteMessage` fails with `MessageNotFound` for a message you are certain
   exists. Explain, and say why the failure is a protection rather than a bug.
6. A malformed message has `DequeueCount=1`. Give the disposition and the
   justification in one sentence each.
7. Queue depth is 4,000 and flat while eight workers report themselves idle.
   Name two distinct causes and the single command that distinguishes them.
8. An expedition needs every observation processed once by an ingest worker, and
   the same observations replayed next month by an audit job. Which dispatch
   model, and what do you do about the other requirement?

## What you can now assume

The rest of the course takes for granted that you can move work out of a request
without losing it, keep a consumer correct when the same work arrives three
times, quarantine what will never succeed, and tell the difference between work
that must be *done* and events that must be *remembered*.
[Module 7](../07-table-storage/README.md) takes the last piece of the expedition
that is still implicit — knowing which stations reported and which did not — and
gives it an index whose cost you choose deliberately.
