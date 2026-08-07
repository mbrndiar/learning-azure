# 9. Consume, checkpoint, and recover

Module 8 put events into a log and read them back. Nothing in it survived a
restart, a second instance, or a failure halfway through a batch. This module is
about the other half: a consumer that can be killed at any instant, restarted
anywhere, and scaled out — and that still produces the right answer.

There is exactly one hard idea here, and everything else follows from it:

> **The service records nothing about your progress. You do, in a different
> service, and the gap between "handled" and "recorded" is measured in
> duplicates.**

## Objectives

By the end of this module you can:

- explain why a checkpoint is a promise about the past rather than a position in
  the present, and why it may only ever move forward;
- compute the exact number of duplicate deliveries a given checkpoint cadence
  will produce on a crash, before it happens;
- write a projection that is unaffected by redelivery, and say why the payload
  cannot be the deduplication key;
- read a consumer's lag correctly, including the partition it has never touched;
- diagnose an ownership problem from a snapshot, and avoid the misdiagnosis that
  scales a rebalancing cluster *down*;
- stop a consumer without producing a burst of duplicates.

## The question this module answers

A field station publishes readings. A projection consumes them and writes
totals. The container running the projection is redeployed twice a day, crashes
occasionally, and runs two replicas for availability.

How many times is each reading counted?

The answer is not "once", and no configuration setting makes it "once". The
answer is "at least once, and you decide how much more than once".

## A checkpoint is a promise, not a position

A checkpoint says: *everything in this partition up to and including sequence
number N has been handled; do not send it again.*

Three consequences, all of which the exercise pins:

**It is written by you, to your storage account.** Event Hubs holds nothing
about consumer progress. The `BlobCheckpointStore` in the companion writes one
blob per partition per consumer group, and the position lives in the blob's
*metadata* — the blob itself is zero bytes. An operator who lists the container
and looks at the sizes concludes, reasonably and wrongly, that it is empty.

**It may only ever move forward.** With a concurrent handler, completions arrive
out of order. Recording a lower position than the one already stored does not
"correct" anything: it silently re-delivers everything in between on the next
restart. `CheckpointLedger.Record` therefore refuses a rewind and says so, which
is a decision the storage SDK will not make for you.

**It is a promise about effects, not about reads.** If the handler applied the
change and the process died before the checkpoint, the effect happened and the
record of it did not. That asymmetry is the entire source of duplicates, and it
cannot be closed — only bounded.

### Resume means after

The checkpoint at sequence number 75 means 75 is *done*. A processor that
resumes *at* 75 replays it on every single restart. That is the most common way
a consumer produces a permanent low-grade stream of duplicates that nobody
notices until the totals are audited against the source.

`EventPosition.FromSequenceNumber(75, isInclusive: false)` is the correct call,
and `CheckpointLedger.ResumeFrom` in the exercise returns exactly that shape.

The other half is subtler: **a partition with no checkpoint has no position at
all**, and zero is a real sequence number, so it cannot double as "nothing
recorded". `ResumeFrom` returns `IsFromStart` and `-1`, forcing the caller to
choose a default deliberately. The processor's `PartitionInitializingAsync`
handler is where that choice is made, and getting it wrong is how a restarted
consumer quietly reprocesses a week of history.

### The two bounds

Checkpointing costs a blob write per partition. Checkpointing every event is a
throughput ceiling and a bill; checkpointing rarely is a duplicate count. So the
policy has two bounds, and needs both:

| bound | protects against | what it costs |
| --- | --- | --- |
| every N events | a busy partition building an unbounded replay | one blob write per N events |
| every T seconds | a quiet partition sitting uncheckpointed indefinitely | one blob write per T, even when idle |
| on partition close | a rebalance or shutdown replaying the tail | one blob write per release |

A policy with only the event bound abandons a partition that receives one event
an hour. A policy that checkpoints on close *unconditionally* writes a blob to
say nothing happened — and on a cluster that is rebalancing, that is a storm.
`CheckpointPolicy.Evaluate` handles all three cases and the "nothing to record"
case, in that order.

## At-least-once is a number

Here is the arithmetic, and it is worth memorising because it is the whole
design:

> **worst-case duplicates per partition = the checkpoint interval**

Checkpoint every 25 events, crash at the worst moment, replay up to 25. That is
not a bug to be fixed; it is a dial to be set. The companion below sets it to 25,
kills the processor after 90 events, and counts **15** duplicates — exactly
`90 − (3 × 25)`.

Which means the handler has to be idempotent. And the deduplication key is
**(partition id, sequence number)** and nothing else:

- **Not the payload.** Two identical readings a second apart are two events.
- **Not a message id.** A redelivery and a genuine resend are indistinguishable
  by it unless the *producer* guarantees otherwise, which the producer in module
  8 does not.
- **Not a timestamp.** Same problem, worse resolution.

Position within a partition is the only identity Event Hubs actually guarantees.
`IdempotentProjection` keys on it, and keeps a high-water mark per partition —
noting that sequence numbers are *increasing*, not *contiguous*. A projection
that assumes the next one is `previous + 1` stalls forever the first time the
service leaves a gap.

## Ownership is a lease

Within one consumer group, a partition has exactly one reader. The processors
agree on who reads what by writing *ownership* blobs next to the checkpoints,
each with an expiry. Nothing coordinates them; there is no leader.

That produces four situations, and only one of them is fine:

| situation | what it means | what to do |
| --- | --- | --- |
| every partition owned, no spare processors | balanced | nothing |
| a partition with no owner | its events are not being read at all | find out why the owner stopped |
| ownership moving many times a minute | the processors spend their time rebalancing | lengthen the ownership interval, or stop restarting instances |
| more processors than partitions | the extras own nothing and cost money | scale down, or add partitions — which module 8 showed you cannot |

`OwnershipDoctor.Diagnose` returns these in that order of severity, with one
deliberate exception: **thrashing is checked before "unowned" is believed.** A
rebalancing cluster looks under-owned in every single snapshot. Diagnosing that
as an unowned partition, or worse as idle processors, is how a cluster that is
struggling to stabilise gets scaled *down* into a backlog.

The lease also explains why a restart is never instant. A processor that is
*killed* rather than stopped leaves its ownership blobs behind, and nothing may
touch those partitions until the lease expires. The companion waits for exactly
that, and prints the wait.

## Lag is measured against the checkpoint

Lag per partition is:

```text
lag = lastEnqueuedSequenceNumber − checkpointedSequenceNumber
```

Two things about this are routinely got wrong.

**It is measured against the checkpoint, not against what the handler has seen.**
A consumer that handles everything and never checkpoints reports zero backlog and
has unbounded lag. On restart it proves it.

**A partition with no checkpoint is maximally behind, not caught up.** This is
the failure mode that makes a dashboard green while an entire partition goes
unread: the consumer group has never recorded a position there, so *everything*
the partition holds is outstanding. `LagCalculator.Measure` treats a missing
checkpoint as position −1 and counts those partitions separately, so the report
can distinguish "behind" from "not looking".

It also clamps at zero. A checkpoint ahead of the last enqueued sequence number
is not an error to propagate — it just means the partition snapshot is a moment
older than the ledger — and a monitoring signal that goes negative is a
monitoring signal that gets silently dropped by an alert rule.

Note what this computation requires: a number from Event Hubs and a number from
*your* storage account. **Lag is a join between two services that only your code
can perform.** No Event Hubs API knows about your checkpoints.

## Stopping is part of the contract

A consumer is stopped constantly: every deployment, every scale event, every
node drain. So shutdown is not an edge case, it is the common path.

Two rules, both pinned by the evaluator:

**Cancellation is checked every iteration, and it does not throw.** A pump that
throws out of its loop on shutdown loses the chance to record what it already
did, and the next instance replays it. A pump that checks the token once before
the loop honours nothing at all — and passes every test that cancels before it
starts, which is why the evaluator cancels mid-stream.

**The last act of a stopping consumer is a checkpoint.** Whatever was handled
since the last one gets recorded, per partition, under the closing reason.
Skipping it turns a routine deployment into a burst of duplicates on every
release.

And one rule that only shows up when the pieces are wired together: **a
recognised duplicate still counts as progress.** Its effect is already in the
projection, so the position past it is safe to record. A pump that advances only
on newly applied events pins its checkpoint behind a run of duplicates and
replays them again on the next restart, forever.

## Run the companion

```bash
ACCEPT_EULA=Y docker compose up -d azurite eventhubs
dotnet run --project lessons/09-event-hubs-processing/CheckpointYard
```

Both services are required. The checkpoint store is Azurite; the processor
cannot claim a partition without writing to it.

Real output, captured from a run against `eventhubs-emulator:2.2.1` and
`azurite:3.36.0`. The checkpoint container name carries a per-run stamp, the
client identifier is a fresh GUID, and offsets and sequence numbers continue
from whatever the hub already holds — so which partitions have data before the
run, and how far they have advanced, depends on what you ran earlier. This is
one representative occurrence; the reproducible claims are that every partition
is claimed exactly once, that a checkpoint moves the resume point, and that lag
returns to zero.

```text
Checkpoint container: checkpoints-115427494

0. Publish: 200 readings from one station
-----------------------------------------
   Published                 : 200 events, all keyed 'station-01'
   Sequence numbers before   : p0=-1, p1=599, p2=-1, p3=-1
   Everything below counts only events published by THIS run.

1. Processor A: Checkpoint every 25, then die after 90
------------------------------------------------------
   Partitions claimed        : 1, 2, 3
   Events handled            : 90
   Checkpoints written       : 3
   Handler errors            : 0
   Stopped by                : reaching the target

   15 events were handled AFTER the last checkpoint. Their effects
   happened. The record that they happened did not.

2. The checkpoint store: Two kinds of blob, and neither is a lock
-----------------------------------------------------------------
   checkpoint 1  offset=96032 sequencenumber=674 clientidentifier=ae0591b2-5e4b-4988-9fab-8f06b376540e
   ownership  1  ownerid=
   ownership  2  ownerid=
   ownership  3  ownerid=

   The checkpoint blob is EMPTY: the position lives entirely in the
   metadata. The ownership blob is how two processors agree who reads
   what, and it expires — which is why a killed processor's partitions
   are picked up by the next one rather than stranded.

   Waiting 8s for A's partition leases to expire...

3. Processor B: A fresh process, resuming from whatever A left behind
---------------------------------------------------------------------
   Partitions claimed        : 0, 1, 2, 3
   Events handled            : 125
   Checkpoints written       : 5
   Handler errors            : 0
   Stopped by                : 5s of silence

4. Duplicates: At-least-once is a number, and here it is
--------------------------------------------------------
   Handled by A              : 90
   Handled by B              : 125
   Handled by BOTH           : 15
   Handled by NEITHER        : 0

   15 events were processed twice. Not because anything failed:
   A handled them, A did not get to checkpoint them, and B correctly
   resumed from the last position that WAS recorded.
   This is the contract, not a defect. A handler that is not
   idempotent is a handler that is wrong.

5. Lag: The only honest measure of whether a consumer is keeping up
-------------------------------------------------------------------
   partition 0 : last enqueued    -1   checkpointed    -1   lag     0
   partition 1 : last enqueued   799   checkpointed   799   lag     0
   partition 2 : last enqueued    -1   checkpointed    -1   lag     0
   partition 3 : last enqueued    -1   checkpointed    -1   lag     0

   Partitions with no checkpoint report lag against -1: the group has
   never recorded a position there, so every event ever written to them
   is outstanding. 'No checkpoint' and 'caught up' are not the same.

   Lag is measured against the CHECKPOINT, not against what the
   handler has seen. A processor that handles everything and never
   checkpoints has zero backlog and unbounded lag, and on restart it
   will prove it.

6. A second consumer group: Same events, separate cursor, separate egress
-------------------------------------------------------------------------
   Events read by '$Default'   : 200
   Its blobs in this container : 0 (a bare consumer client has no store)

   The 'field-journal' group processed, checkpointed, crashed, and
   recovered. None of that was visible to this group, which read every
   event from the beginning. Consumer groups share the log and share
   nothing else — including the egress budget.

Deleted checkpoint container checkpoints-115427494.
```

Five things in that output are worth more than the rest.

**Fifteen duplicates, and you could have predicted the number.** `90 − (3 × 25)`.
Nothing failed. No retry fired. The processor handled 90 events, recorded 75, and
its replacement correctly resumed at 76.

**Processor A claimed partitions 1, 2 and 3 — not 0.** Load balancing had not
finished when A hit its target and stopped. That is normal, and it is why a
consumer's partition set is not a thing to assert on in production code.

**The checkpoint blob's size is not shown because it is zero.** Everything is in
`sequencenumber=674` in the metadata. `offset` is there too; the SDK uses it as
the actual seek position and it is not a count of anything.

**The ownership blobs have an empty `ownerid`.** That is what `StopProcessingAsync`
does: it releases cleanly, so the successor does not have to wait. The companion
waits anyway, because a *killed* processor does not get to do this.

**Section 6 read all 200 events, and the checkpoint container knows nothing about
it.** A bare `EventHubConsumerClient` has no store at all — its cursor lives in
memory and dies with the process. That is the right tool for a one-off
inspection and exactly the wrong one for a projection.

### What the emulator will not tell you

Four divergences, in increasing order of how much they matter.

**Checkpoint writes are free here.** Azurite is a local process. In Azure each
checkpoint is a billed blob transaction with real latency, which is why the
cadence is a cost decision rather than a preference.

**Consumer groups cannot be created.** They are declared in
`infra/local/eventhubs/config.json` and read once at container start. The
emulator auto-adds `$default` alongside them and refuses everything else. The
cost of a consumer group — a second full read of the stream, charged against the
same throughput units — is therefore invisible locally.

**There are no metrics.** `IncomingMessages`, `OutgoingMessages`, `ThrottledRequests`,
and the consumer-lag signals do not exist. Everything section 5 computes, it
computes by hand.

**There is no authorization.** The emulator takes a fixed connection string.
Azure needs *two* roles for a processor — one on Event Hubs, one on the blob
container — and a processor holding only the first starts cleanly, claims
nothing, and reports errors from a service nobody was watching.

The last two are why this module has a **required live checkpoint**.

## The management labs

```bash
bash infra/azure-cli/event-hubs-processing.sh
```

```bash
pwsh -File infra/powershell/event-hubs-processing.ps1
```

Both are **live**, both prompt for confirmation showing the subscription they are
about to bill, and both delete their resource group at the end. Same nine steps,
same names, same order.

Step 3 creates the storage account the checkpoints live in, which is the point
of the lab: a consumer is a client of two services with two bills, two
availability records, and two sets of permissions.

Step 5 assigns *Azure Event Hubs Data Receiver* on the namespace and *Storage
Blob Data Contributor* on the checkpoint container only. Grant the first without
the second and the processor starts, logs a storage failure through
`ProcessErrorAsync`, claims no partitions, and reads nothing — a symptom that
looks like an Event Hubs problem and is not one.

Step 6 reads `lastEnqueuedSequenceNumber` per partition and then asks the
platform for `IncomingMessages` and `OutgoingMessages`. Neither number exists
locally.

## A bounded experiment

Fifteen minutes, two runs, one constant changed each time. `CheckpointEvery` is
on line 39 of `CheckpointYard/Program.cs`. Section 4 is the only part of the
output you need.

**1. Checkpoint ten times more often.** Set `CheckpointEvery = 5`.

Observed:

```text
1. Processor A: Checkpoint every 5, then die after 90
-----------------------------------------------------
   Events handled            : 90
   Checkpoints written       : 18

   0 events were handled AFTER the last checkpoint.

4. Duplicates
   Handled by BOTH           : 0
```

**2. Never checkpoint at all.** Set `CheckpointEvery = 100` — larger than the 90
events processor A handles, so no checkpoint is ever written.

Observed:

```text
1. Processor A: Checkpoint every 100, then die after 90
-------------------------------------------------------
   Events handled            : 90
   Checkpoints written       : 0

   90 events were handled AFTER the last checkpoint.

3. Processor B
   Events handled            : 200

4. Duplicates
   Handled by A              : 90
   Handled by B              : 200
   Handled by BOTH           : 90
```

Then set it back to 25.

Three things to extract.

**The duplicate count is a linear function of the interval, and you chose it.**
5 → 0 duplicates (90 is a multiple of 5, so the crash landed on a boundary; the
worst case is 4). 25 → 15. 100 → 90. This is a dial, and the cost on the other
side of it is 18 blob writes instead of 3 for the same 90 events.

**Run 2's processor B did strictly more work than the whole stream.** It handled
200 events of which 90 had already been handled. At production scale that is not
a curiosity: a consumer that never checkpoints has an unbounded restart cost, and
the restart is triggered by exactly the conditions — load, deployment,
instability — under which you can least afford it.

**Nothing in run 2 reported an error.** No exception, no warning, no failed
health check. The only evidence that 90 events were counted twice is a
comparison the *application* performed. That is the case for idempotency: the
platform will never tell you.

## Common mistakes and how to diagnose them

| symptom | likely cause | how to confirm |
| --- | --- | --- |
| every restart reprocesses one event per partition | resuming inclusively from the checkpoint | log the resume position and compare with the checkpointed sequence number |
| a restart reprocesses a week | a partition with no checkpoint defaulted to `Earliest` | check `PartitionInitializingAsync` for a per-partition default |
| totals are consistently 2–5 % high | the handler is not idempotent and deployments are frequent | count distinct `(partition, sequence)` against applied events |
| dedup by payload silently drops real events | two identical readings are two events | assert that two events with the same body and different sequence numbers both apply |
| the dashboard is green while a partition is unread | lag treats "no checkpoint" as caught up | count partitions with no checkpoint separately |
| lag alerts never fire, then fire at 10⁶ | lag measured against handler progress, not the checkpoint | compare the checkpoint blob metadata with `lastEnqueuedSequenceNumber` |
| the processor starts, logs nothing useful, reads nothing | missing *Storage Blob Data Contributor* on the checkpoint container | look at `ProcessErrorAsync` output for storage operations |
| scaling out made throughput worse | more processors than partitions, plus rebalance churn | count ownership changes per minute before concluding anything |
| a deployment produces a burst of duplicates | no checkpoint on partition close | check that the shutdown path checkpoints |
| a consumer will not start after an outage | the checkpoint points inside an expired retention window | compare the checkpoint with `beginSequenceNumber` |
| a second replica reads nothing for 30 seconds after the first dies | ownership leases have not expired yet | that is the design; shorten the interval only if you accept more rebalancing |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/09-event-hubs-processing/tests -p:Implementation=starter

```

The starter has fourteen numbered gaps, in dependency order: the ledger's
forward-only rule and its two resume cases (GAPs 1–3); the checkpoint policy's
closing case and two bounds (GAPs 4–5); idempotent application and the
high-water mark (GAPs 6–8); lag including the unclaimed partition (GAPs 9–10);
the ownership diagnosis order (GAP 11); and the pump that wires them together —
cancellation, duplicates-as-progress, and the closing checkpoint (GAPs 12–14).
Each throws a `NotImplementedException` naming the section of this page that
derives it.

Every check is deterministic and offline: the clock is injected, the stream is a
list, and nothing touches an emulator or Azure.

**Untouched-starter baseline: fails.** 61 of 72 checks fail, the first with:

```text
System.NotImplementedException : GAP 1: implement CheckpointLedger.Record.
See lessons/09-event-hubs-processing/README.md#a-checkpoint-is-a-promise-not-a-position.
```

The 11 that pass are the argument-guard checks — `APartitionIdIsRequired`,
`ANegativeSequenceNumberIsRefused`, `BothBoundsMustBePositive`,
`EveryCollaboratorIsRequired`, and so on. The guards are already written, because
validating an input is not the skill this module is testing.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| `ResumeFrom` returns `IsInclusive: true` | 2 failures: `ResumeStartsAfterTheCheckpointedEvent` — *Assert.False() Failure, Expected: False* — and `ARestartReplaysOnlyWhatWasNotRecorded` |
| `Record` accepts a rewind | 3 failures: `ACheckpointNeverMovesBackwards` and `RerecordingTheSamePositionIsNotProgress` — *Expected: False, Actual: True* — plus `ARewindIsNeverRecordedEvenOnTheWayOut` — *Expected: 0, Actual: 1* |
| a partition with no checkpoint is reported as caught up | 3 failures, including `APartitionWithNoCheckpointIsMaximallyBehind` — *Expected: 60, Actual: 0* — and `TheTotalIsTheSumOfThePartitions` — *Expected: 480, Actual: 0* |
| cancellation checked once before the loop instead of every iteration | 2 failures: `CancellationStopsThePumpWithoutThrowing` — *Expected: True, Actual: False* — and `ACancelledRunStillRecordsWhatItDid` — *Expected: 39, Actual: 499* |
| the closing checkpoint is skipped | 3 failures, including `WhatWasHandledAfterTheLastBoundIsRecordedOnTheWayOut` — *Expected: 4, Actual: 3* |
| a recognised duplicate does not advance progress | 1 failure: `DuplicatesCountAsProgress` — *Expected: [EventCount, EventCount], Actual: [EventCount, PartitionClosing]* |

The last two are the ones to notice. Both produce code that handles every event
correctly, applies every effect exactly once within a single run, and passes
every check that is not specifically looking for them. Neither has a symptom
until a restart — and the restart happens on the deployment, not in the test
run.

The fourth is worth a second look too. Checking cancellation once before the
loop is honoured perfectly by any caller who cancels *before* starting, which is
what a naive test would do. The evaluator cancels after 40 of 500 events, which
is what a deployment does.

## Environments

- **Emulator.** `ACCEPT_EULA=Y docker compose up -d azurite eventhubs` for the
  companion — both, because the checkpoint store is Azurite and the processor
  cannot claim a partition without it. The exercise evaluator needs nothing
  running.
- **Live checkpoint: required.** Run one of the two management labs end to end.
  Two things this module teaches are absent locally and cannot be simulated:
  the split role model — a processor needs a role on Event Hubs *and* a role on
  the blob container, and the failure mode when it has only one is silent — and
  the platform metrics that a real consumer is monitored with. The emulator also
  cannot create a consumer group, so the cost of adding one is invisible until
  step 4 of the lab. Budget under USD 0.02 and roughly ten minutes; step 8
  deletes the resource group.

## Review questions

1. A processor checkpoints every 500 events. It handles 1,200 and is killed.
   How many events does its replacement redeliver, and what is the range of
   possible answers?
2. A colleague proposes deduplicating on the event body's SHA-256 "because the
   readings are unique anyway". Give the concrete scenario in which this drops
   real data, and say why it will not show up in testing.
3. A consumer group has four partitions. Three report zero lag; the fourth has no
   checkpoint at all. Your dashboard shows total lag 0. What is wrong with the
   dashboard, and what should it show instead?
4. Explain why `EventPosition.FromSequenceNumber(n, isInclusive: true)` is wrong
   after a checkpoint, and describe the exact production symptom.
5. Ownership changes 40 times a minute across six processors on four partitions.
   Give the two candidate diagnoses, say which one to act on first, and state the
   remedy that would make things worse.
6. Module 6's queue deleted a message after processing; this module's stream does
   not. State the one guarantee the queue gives that the stream cannot, and the
   one the stream gives that the queue cannot.
7. A processor is granted *Azure Event Hubs Data Receiver* and nothing else. It
   starts without error. Describe what happens next and where the evidence is.
8. Your handler applies effects to Cosmos DB. Sketch the cheapest way to make it
   idempotent, and say what it costs in request units per event.
9. During a rolling deployment, replicas are stopped one at a time. Explain why
   the shutdown checkpoint matters more here than during a crash, and what the
   duplicate count looks like with and without it.
10. A consumer has been offline for three days against a hub with one day of
    retention. Its checkpoint is still stored. Describe what happens on restart
    and how you would detect it before it happens.

## What you can now assume

You can now consume a partitioned stream reliably: resume in the right place,
bound the duplicate count deliberately, apply effects idempotently, measure lag
honestly, and shut down without a burst. The events are being read correctly and
they are still nowhere useful — the projection lives in memory and dies with the
process.

Module 10 is where they land: a database whose partition key is chosen with the
same care as module 8's, and whose costs are measured in request units rather
than guessed at.
