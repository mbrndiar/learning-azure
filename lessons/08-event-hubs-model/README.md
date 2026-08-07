# 8. Stream expedition telemetry

> **Read** this page, **run** the companion in
> [`TelemetryStream/`](TelemetryStream/) against the Event Hubs emulator,
> **practise** in
> [`exercises/08-event-hubs-model/`](../../exercises/08-event-hubs-model/), then
> work the paired [CLI](../../infra/azure-cli/event-hubs-model.sh) and
> [PowerShell](../../infra/powershell/event-hubs-model.ps1) labs.
> Prerequisites: the [Field Station project](../../projects/field-station/) and
> Docker. **A live checkpoint is required** for this module — see
> [Environments](#environments).

## Objectives

By the end of this module you can:

- **produce** partitioned telemetry batches with an explicit partition-key
  strategy and bounded batch sizes;
- **compare** an event stream with a work queue and justify the retention,
  replay, and ordering tradeoffs for sensor telemetry; and
- **explain** how namespace, hub, partition count, and throughput limits bound
  ingest, and say exactly what cannot be changed after creation.

## The question this module answers

Module 6 built a queue, and the queue was right: an artifact arrives, one worker
processes it, the message is deleted, the work is done. The expedition's sensor
telemetry breaks every assumption in that sentence.

Twelve stations report a temperature every few seconds. The live dashboard needs
it now. The Cosmos projection (module 11) needs the same events, independently.
Next month somebody will want to re-run the anomaly detector over last week —
over data that, in a queue, was deleted the moment it was first handled.

> **A queue moves work. A stream keeps a record.** The queue's central
> mechanic — delete on success — is precisely what the telemetry workload cannot
> tolerate.

That single difference propagates into everything else: how the data is
addressed, what ordering you can get, what a consumer's position means, and what
you pay for.

## A stream is not a queue

| | Queue Storage (module 6) | Event Hubs |
| --- | --- | --- |
| after a message is handled | deleted | still there, until retention expires |
| a second reader | competes for the same message | reads the whole stream independently |
| reading again | impossible | the normal case |
| ordering | none; redelivery actively reorders | total, within one partition |
| consumer position | server-side (visibility timeout) | client-side (a cursor you store) |
| unit of retry | one message | none — you re-read a position |
| what limits throughput | account transaction rate | partitions and throughput units |
| cost driver | operations | provisioned capacity, per hour |

The row that matters most is the last-but-one. A queue tracks, per message,
whether you finished. **A stream tracks nothing.** It hands you a cursor and
lets you decide what to remember — which is why module 9 exists and why it is
about checkpoints rather than about consuming.

The exercise turns this into a decision procedure. Given a workload, exactly one
characteristic is usually structural and the rest are preferences:

- **replay required** → stream. A queue cannot be configured into keeping what
  it deleted.
- **two readers that each need everything** → stream. Queue consumers compete
  for one copy; two full readers means two queues and a fan-out you maintain.
- **per-key ordering required** → stream. A queue promises no order at all.
- **per-item acknowledgement required** → queue. A stream has a per-partition
  cursor and nothing finer.
- **item cost varies from milliseconds to minutes** → queue. A stream partition
  has exactly one owner processing in order, so one slow event blocks everything
  behind it.

The expedition uses both, for exactly these reasons: artifacts stay on the
queue, telemetry moves to the stream.

## Namespace, hub, partition

Three nested things, and they are not three names for one thing.

| | what it is | what it owns |
| --- | --- | --- |
| **namespace** | the billed, addressable endpoint | throughput units, TLS and network settings, authorization rules |
| **event hub** | one stream inside it | partition count, retention, consumer groups |
| **partition** | one append-only log | ordering, sequence numbers, offsets, one owner per consumer group |

The namespace owns capacity, and this is the first surprise: **throughput units
are bought per namespace and shared by every hub in it.** A noisy hub starves a
quiet neighbour, and no per-hub setting prevents it.

The hub owns the partition count. On Basic and Standard that number is fixed
when the hub is created and there is no operation that changes it — a fact
step 6 of the management labs makes you watch, because it is the single most
consequential line in this module.

A partition is a commit log. Events are appended, each gets a **sequence
number** and an **offset**, and both restart from zero in every partition. There
is no global ordering, no global position, and no query. The only thing you can
do with a partition is start somewhere and read forward.

## The partition key buys one thing

A producer has exactly one lever over placement, and it is not "which
partition":

```csharp
var options = new CreateBatchOptions { PartitionKey = station };
using var batch = await producer.CreateBatchAsync(options);
```

The service hashes the key and the hash decides the partition. That buys you
**co-location and send order for everything sharing the key**, and nothing else.

The mistakes are all variations on choosing the wrong granularity:

| key | what happens | why it is wrong |
| --- | --- | --- |
| the reading's timestamp or a fresh GUID | perfect spread | one station's readings scatter over every partition; the ordering guarantee is gone and nothing reports it |
| a constant, e.g. `"telemetry"` | total ordering | one partition, one owner, one throughput ceiling — for the entire workload |
| `station` | a station is ordered and co-located | the expedition's actual requirement |
| `station + day` | ordering breaks at midnight | tempting by analogy with module 7's table key, and wrong: a table partition is a scan boundary, a hub partition is an ordering boundary |

That last row is worth dwelling on. Module 7's key was `StationId + day`, and it
was right there for the same reason it is wrong here. **A table partition bounds
a scan; a stream partition bounds an order.** Bounding growth is a table problem
that a stream does not have — retention drops old events for you.

A key is a string whose UTF-8 representation is **at most 128 bytes**, and
unlike a table key there is no forbidden-character list. Counting .NET
characters is insufficient: non-ASCII station names can consume multiple bytes
per character.

### The mapping is stable and it is not yours

The key-to-partition hash is stable — the same key always lands on the same
partition — and Microsoft does not publish the function. The exercise
reimplements the mapping with **FNV-1a**, which reproduces the *properties* that
matter rather than the service's actual placements: deterministic, uniform-ish
over many keys, and lumpy over few.

One property is worth stating on its own, because getting it wrong produces a
bug with no symptom:

> `string.GetHashCode()` is randomized per process in .NET. A partition computed
> from it changes on every restart.

Nothing throws. Nothing logs. The co-location guarantee simply stops being true
after a deployment, and the first person to notice is whoever is debugging
out-of-order readings six weeks later. The evaluator pins the mapping against
values recorded from a different process, which is the only way an in-process
test can catch it.

The second property is the one the companion measures: **a small number of keys
does not spread evenly.** Five stations over four partitions cannot be balanced,
whatever the hash is.

## A batch is a size budget with one key

`EventDataBatch` is not a list. It is a size budget that refuses:

```csharp
if (!batch.TryAdd(new EventData(body)))
{
    await producer.SendAsync(batch);
    // open a new batch and re-add the event that did not fit
}
```

Three rules, and each has a corresponding way to lose data:

1. **`TryAdd` returns `false`; it does not throw.** An unchecked call drops the
   event with no exception anywhere in the system.
2. **A batch carries one partition key for every event in it.** A single
   "current batch" reused across stations either mixes keys or silently
   abandons the guarantee the key was chosen for.
3. **An event that does not fit an *empty* batch never will.** Retrying is an
   infinite loop; skipping is data loss. It has to be surfaced.

The Standard-tier publication limit is 1 MB, and the companion measures what
that actually holds.

## Capacity is two limits, not one

A Standard-tier throughput unit admits, per second:

- **1 MB of ingress, or 1,000 events** — whichever you exhaust first; and
- **2 MB of egress**.

Sizing on megabytes alone is the classic error. A telemetry workload at 5,000
events per second and 200 bytes an event is 1 MB/s — one unit by bytes and
**five by event count**. Provision one and you are throttled at a fifth of the
planned rate, with a `ServerBusy` that looks like a network problem.

Egress has its own trap: **every consumer group reads the whole stream.** Adding
the Cosmos projection alongside the dashboard does not add a little egress, it
adds all of it again.

Partitions are bounded from below by two separate things too — the ingest rate
(roughly 1 MB/s per partition) and the number of processor instances that must
each own work. A partition has exactly one owner per consumer group, so **a hub
with fewer partitions than processors leaves processors permanently idle**, no
matter how the workload is spread.

### What you cannot change afterwards

| change | Basic | Standard | Premium |
| --- | --- | --- | --- |
| throughput / processing units | yes | yes | yes |
| retention | fixed at 1 day | 1–7 days | up to 90 days |
| add a consumer group | no (exactly 1) | yes, up to 20 | yes, up to 100 |
| **increase partitions** | **no** | **no** | yes, and it remaps keys |
| decrease partitions | no | no | no |

Even the "yes" in that table has a cost. Increasing the partition count on
Premium **remaps keys to partitions**, so events for one key move and relative
order across the change is not preserved. There is no tier on which the
partition count is a free variable.

## Run the companion

```bash
ACCEPT_EULA=Y docker compose up -d eventhubs
dotnet run --project lessons/08-event-hubs-model/TelemetryStream
```

The emulator seeds one hub, `telemetry`, with 4 partitions. Real output,
captured from a run against `eventhubs-emulator:2.2.1`. The run tag, the
creation timestamp, and the send duration are per run; so is the *distribution*
of the keyless events, because the service assigns those round-robin from an
arbitrary starting partition. Sequence numbers continue from whatever the hub
already holds, so they grow across repeated runs. This is one representative
occurrence: the reproducible claims are that a partition key always lands on one
partition and that keyless events spread across all four.

```text
Run tag: 111846641

0. The hub: The namespace, the hub, and the partition count
-----------------------------------------------------------
   Fully qualified namespace : localhost
   Event hub                 : telemetry
   Created                   : 2026-08-07 11:15:32Z
   Partition ids             : 0, 1, 2, 3 (4 partitions)
   The partition count is fixed at creation. Everything below is a
   consequence of that one number.

1. No partition key: Throughput, and no ordering guarantee at all
-----------------------------------------------------------------
   Sent                      : 20 events, no partition key
   Landed on                 : ONE partition, chosen by the service
   Ordering between batches  : none that you may rely on
   The unit of placement is the BATCH, not the event. A keyless send
   spreads load across many sends; it does not fan one send out. If
   you expected 5 events on each of 4 partitions, section 4 will
   disagree with you.

2. A partition key: One station, one partition, in order, forever
-----------------------------------------------------------------
   Sent                      : 100 events in 5 batches, one partition key each (30 ms)
   Guarantee bought          : all of a station's readings are on one
                               partition, in send order
   Guarantee NOT bought      : which partition. The key is hashed; the
                               mapping is stable but not yours to pick
   Section 4 reads the placement back and shows it.

3. The batch: TryAdd returns false; it does not throw and it does not send
--------------------------------------------------------------------------
   Maximum size              : 1,048,576 bytes
   Size when empty           : 0 bytes
   1 KiB events accepted     : 1,008
   Size when full            : 1,048,320 bytes
   Overhead per event        : ~16.0 bytes above the 1,024-byte body
   The batch was NOT sent. TryAdd returning false is the signal to
   send what you have and start a new batch — an unchecked TryAdd is
   how events get dropped without an exception anywhere.

4. Placement: Where the keyed events actually landed
----------------------------------------------------
   partition 0 :  60 events (20 keyless)   keys: station-04, station-05
   partition 1 :  40 events ( 0 keyless)   keys: station-01, station-02
   partition 2 :   0 events ( 0 keyless)   keys: (none)
   partition 3 :  20 events ( 0 keyless)   keys: station-03

   Every station appears under exactly one partition. Five keys did
   not produce five partitions, and no amount of retrying moves one.
   The keyless batch from section 1 sits whole on a single partition.

5. Replay: The same read, again, from the beginning
---------------------------------------------------
   First pass                : 120 events
   Second pass               : 120 events
   Identical                 : True
   Reading did not consume anything. There is no acknowledgement, no
   lock, and no delete: a reader is a cursor over a log that the
   retention window — not the reader — decides when to drop.
   That is the whole difference from module 6's queue.

6. Sequence numbers and offsets: Per partition, and never global
----------------------------------------------------------------
   partition 0 : beginning      0   last     99   empty False
   partition 1 : beginning      0   last     79   empty False
   partition 2 : beginning     -1   last     -1   empty True
   partition 3 : beginning      0   last     59   empty False

   Sequence numbers restart per partition, so 'event 41' is not a
   position in the stream — it is a position in ONE partition. A
   checkpoint is therefore per partition too, which is module 9.
```

Four things in that output are worth more than the rest.

**Section 1 disagrees with the obvious model.** Twenty keyless events did not
land five-per-partition; they landed *together*, on one. The unit of placement
is the batch. A producer that "spreads load by not setting a key" spreads it
across sends, not within them.

**Partition 2 is empty.** Five keys, four partitions, one idle. That is not the
emulator being lazy: it is what hashing a small key set does, and it is why
partition count is sized from throughput and processor count rather than from
"number of stations".

**Sections 6's numbers grew between runs.** Beginning sequence 0, last 99 — this
was the third run against the same emulator, and nothing was consumed by the
first two. Retention, not reading, is what removes events.

**The replay in section 5 is not a feature that had to be enabled.** It is what
reading *is*. There was no acknowledgement to skip and no lock to release.

### What the emulator will not tell you

Three divergences, and one of them is visible in the output above.

**The namespace name is not yours to choose.** In Azure the namespace name is
the DNS host you connect to. The emulator supports exactly one namespace and one
name for it, so [`infra/local/eventhubs/config.json`](../../infra/local/eventhubs/config.json)
declares the required `emulatorNs1`. Any other value is a warning rather than an
error — the emulator logs

```text
warn: HostHelper[0]
      Recoverable validation failed on user config:Expected string to be "emulatorns1"
      with a length of 11 because NamespaceName is non-modifiable.Only supported
      emulatorns1, but "expedition" has a length of 10, differs near "xpe" (index 1).
```

and then starts anyway with `emulatorns1`, which is the failure mode worth
noticing: a configuration file that the service silently overrides is one you
have stopped reading. Hub names, partition counts, and consumer groups in that
file *are* honoured; only the namespace is fixed. Either way the SDK reports the
fully qualified namespace as `localhost`, which is why section 0 prints that
instead of a `*.servicebus.windows.net` host.

**There is no control plane.** You cannot create a hub, change a partition
count, add a throughput unit, or set retention against the emulator. The
configuration file is read once at container start and that is the entire
management surface. Everything in [Capacity is two limits, not
one](#capacity-is-two-limits-not-one) is therefore untestable locally.

**Retention is not enforced.** The emulator keeps what it is given for as long
as the container lives, so a replay that would fail against a 1-day window in
Azure succeeds here forever.

The first is cosmetic. The second and third are why this module has a **required
live checkpoint**: a capacity model you have never watched Azure refuse is a
capacity model you do not have.

## The management labs

```bash
bash infra/azure-cli/event-hubs-model.sh
```

```bash
pwsh -File infra/powershell/event-hubs-model.ps1
```

Both are **live**, both prompt for confirmation showing the subscription they
are about to bill, and both delete their resource group at the end. Same eight
steps, same names, same order.

Step 5 changes retention, throughput units, and consumer groups on a running
hub. Step 6 attempts to change the partition count from 4 to 8 and then — this
is the part worth copying into your own habits — **reads the value back rather
than trusting the response**. A control-plane call that returns success without
changing anything is a real failure mode, and the only defence is to verify the
state rather than the status code.

Step 7 assigns *Azure Event Hubs Data Sender* and *Data Receiver* separately.
Being Owner of the namespace does not let you publish one event to it.

## A bounded experiment

Fifteen minutes, three runs, one number changed each time. Section 4 is the only
part of the output you need.

**1. More keys than partitions.** In `TelemetryStream/Program.cs` (lines 31-32)
replace the `Stations` array with twelve stations:

```csharp
private static readonly string[] Stations =
    [.. Enumerable.Range(1, 12).Select(index => $"station-{index:D2}")];
```

Observed result (the twenty keyless events land wherever the service's
round-robin happens to start, so which partition carries them moves between
runs; the key-to-partition mapping is a hash and does not):

```text
   partition 0 : 140 events (20 keyless)   keys: station-04, station-05, station-07, station-10, station-11, station-12
   partition 1 :  40 events ( 0 keyless)   keys: station-01, station-02
   partition 2 :  40 events ( 0 keyless)   keys: station-06, station-08
   partition 3 :  40 events ( 0 keyless)   keys: station-03, station-09
```

Every partition is now busy — and partition 0 carries **six** of the twelve
stations while partition 2 carries two. Tripling the key count removed the idle
partition and did *not* remove the imbalance.

**2. One key for everything.** Now set `Stations` to a single entry and raise
`ReadingsPerStation` (line 35) to 100:

```csharp
private static readonly string[] Stations = ["expedition"];
private const int ReadingsPerStation = 100;
```

Observed result:

```text
   partition 0 :   0 events ( 0 keyless)   keys: (none)
   partition 1 : 120 events (20 keyless)   keys: expedition
   partition 2 :   0 events ( 0 keyless)   keys: (none)
   partition 3 :   0 events ( 0 keyless)   keys: (none)
```

The 100 keyed events always land together on the one partition `expedition`
hashes to; the 20 keyless events landed there too in this run and may land
elsewhere in yours.

Then revert both edits.

Three things to extract.

**The partition count never changed.** It could not: it is fixed at hub
creation. Every difference above came from the *keys*, which are a producer-side
decision made in one line of application code.

**Perfect ordering and zero scalability are the same configuration.** Run 2 has
a total order over all 100 keyed events — genuinely useful, occasionally
required — and it achieves that by using one quarter of a hub that costs the
same either way. Three partitions sat idle. The choice between "ordered" and "parallel" is
made at the key, not at the tier.

**Balance is a property of key *cardinality*, not of key correctness.** All
three runs used a perfectly reasonable key. Only the number of distinct values
changed, and it moved the workload from one-partition-idle, to
one-partition-carrying-more-than-half, to three-partitions-empty. When you pick a key, ask
how many distinct values it will have in production — not whether it identifies
the right thing.

## Common mistakes and how to diagnose them

| symptom | likely cause | how to confirm |
| --- | --- | --- |
| a station's readings arrive out of order after a deployment | the partition is computed from `string.GetHashCode()`, which is randomized per process | log the computed partition for a fixed key across two process starts |
| events vanish under load with no exception logged | `TryAdd`'s return value is ignored | assert that every reading appears in some batch, as `NothingIsLostWhenABatchFillsUp` does |
| the producer hangs and memory grows | an oversized event is being retried forever against fresh batches | bound the batch count per event; a second refusal is fatal, not transient |
| `ServerBusy` at a fraction of the planned megabytes | the event-count limit bound before the byte limit | divide events/s by 1,000 and compare with MB/s |
| adding a dashboard made ingest start throttling | a new consumer group multiplied egress | count consumer groups; each one reads the whole stream |
| half the processor instances are idle | fewer partitions than processors | a partition has exactly one owner per consumer group |
| one partition is hot and the others are not | too few distinct partition keys | run `PartitionKeyPlanner.Spread` over the production key set before choosing |
| "we will add partitions later" is in the design | it is not possible on Basic or Standard | see step 6 of the management labs |
| a key is rejected at send time | its UTF-8 representation exceeds 128 bytes | measure with `Encoding.UTF8.GetByteCount`; concatenation and non-ASCII text are common causes |
| the local replay test passes and production loses old events | the emulator does not enforce retention | check `retentionDescription.retentionTimeInHours` on the real hub |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/08-event-hubs-model/tests -p:Implementation=starter

```

The starter has thirteen numbered gaps, in dependency order: partition-key
choice, legality, the stable hash, and skew (GAPs 1–4); bounded batching with
cancellation, one batch per key, refusal handling, and the un-batchable event
(GAPs 5–8); the stream-versus-queue decision procedure (GAP 9); and capacity
sizing and immutability (GAPs 10–13). Each throws a `NotImplementedException`
naming the section of this page that derives it.

**Untouched-starter baseline: fails.** 77 of 79 checks fail, the first with:

```text
System.NotImplementedException : GAP 10-12: implement CapacityPlanner.Size.
See lessons/08-event-hubs-model/README.md#capacity-is-two-limits-not-one.
```

That failure is your next action, not a repository defect. (The two passing
checks are `ReadingsAreRequired` and `ABatchFactoryIsRequired`, which the
starter's argument guards already satisfy.)

The evaluator is deterministic and offline: no emulator, no socket, no wall
clock. Batching runs against a `BudgetedBatch` with an exact byte budget and a
`TryAdd` that refuses rather than throws, exactly as `EventDataBatch` does, and
it counts its own refusals so a test can prove the budget was actually reached.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| `PartitionFor` uses `string.GetHashCode()` instead of FNV-1a | 1 failure every run: `ThePartitionMappingSurvivesARestart` — *Expected: 1, Actual: 2*. Roughly a third of runs add a second, `FiveKeysOverFourPartitionsDoNotSpreadEvenly`, when that process's random seed happens to pile three keys onto one partition. Every other check still passes, because within one process a randomized hash is perfectly stable |
| the refused `TryAdd` is skipped instead of retried on a new batch | 4 failures, including `NothingIsLostWhenABatchFillsUp` — *Expected: 200, Actual: 20* — and `AFullBatchIsClosedAndAnotherIsOpened` — *Expected: 3, Actual: 1* |
| one batch is opened for all keys instead of one per key | 1 failure: `NoBatchMixesPartitionKeys` — *Expected: 5, Actual: 1* |
| `Size` takes the maximum of bytes and egress but drops the event-count limit | 1 failure: `EventCountCanBindBeforeBytesDo` — *Expected: 5, Actual: 1* |
| cancellation is checked once before the loop instead of on each iteration | 1 failure: `CancellationIsHonouredMidPack` — *Assert.Throws() Failure: No exception was thrown* |

The first and last are the ones to notice. Both faults produce code that works
perfectly in every test that is not specifically looking for them: a randomized
hash is stable inside one process, and a token checked once is honoured by every
caller who cancels early. Neither has a symptom until production.

Writing the first mutation also settled a design question in the evaluator. The
original `FiveKeysOverFourPartitionsDoNotSpreadEvenly` asserted `EmptyPartitions
== 1`, copied from the companion's live output — and the exercise's FNV-1a stand
-in leaves *no* partition idle and one carrying double instead. Asserting the
service's placements against a model that is explicitly not the service's hash
would have been a test that pins the wrong thing. It now asserts the property
that both share: five keys over four partitions cannot balance.

## Environments

- **Emulator.** `ACCEPT_EULA=Y docker compose up -d eventhubs` for the
  companion. The Event Hubs emulator depends on Azurite for its metadata and
  blob storage, so Compose starts both. The exercise evaluator needs nothing
  running.
- **Live checkpoint: required.** Run one of the two management labs end to end.
  The emulator has no control plane at all — no namespace, hub, partition,
  retention, or throughput operation exists locally — so every capacity claim in
  this module is unverifiable without it. The specific thing you are there to
  see is step 6: Azure declining to change a partition count, and the read-back
  that proves it. Budget under USD 0.01 and roughly ten minutes; step 8 deletes
  the resource group.

## Review questions

1. The telemetry workload needs ordering per station, replay for a week, and two
   independent readers. Which of those three, on its own, makes a queue
   impossible — and why can the other two be worked around?
2. Module 7 keyed a table partition on `StationId + day` and this module keys a
   stream partition on `StationId` alone. Both are "the right key". Explain what
   a partition bounds in each service, and why the day belongs in one and not
   the other.
3. A colleague sets the partition key to `Guid.NewGuid()` "for better balance".
   Balance improves. State precisely what was lost, and why no exception is
   raised.
4. `PartitionFor` is implemented with `string.GetHashCode()`. Every test passes.
   Describe the production symptom, when it appears, and the one test that could
   have caught it.
5. Your producer sends 5,000 events per second at 200 bytes each. How many
   throughput units, and which limit bound? Now the events grow to 20 KB — what
   changes?
6. You add a second consumer group so a dashboard can read alongside the Cosmos
   projection. Ingest starts throttling and the producer did not change. Explain.
7. Twenty keyless events were sent in one batch. How many partitions received
   events, and what does that say about "keyless means spread"?
8. The hub was created with 4 partitions and the workload now needs 16. Give the
   options on Standard, and say what the Premium option costs even when it
   succeeds.
9. `TryAdd` returns `false` for an event that is 2 MB. Describe the two wrong
   responses and what each one costs.

## What you can now assume

You can now put events into a partitioned, replayable log with an ordering
guarantee you chose deliberately, size the hub from a measured workload, and say
which of those decisions you are stuck with. What you cannot yet do is get the
events back out reliably: nothing so far has survived a restart, a second
processor instance, or a duplicate delivery. That is module 9, and it is
entirely about the cursor this module kept mentioning.
