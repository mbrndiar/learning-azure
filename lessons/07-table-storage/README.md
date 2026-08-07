# 🗂️ 7. Index station observations

> **Read** this page, **run** the companion in
> [`ObservationIndex/`](ObservationIndex/) against Azurite, **practise** in
> [`exercises/07-table-storage/`](../../exercises/07-table-storage/), then work
> the paired [CLI](../../infra/azure-cli/table-storage.sh) and
> [PowerShell](../../infra/powershell/table-storage.ps1) labs.
> Prerequisites: [module 3](../03-storage-account/README.md) and Docker. No
> Azure subscription is needed for any part of this module.

## Objectives

By the end of this module you can:

- **design** `PartitionKey` and `RowKey` values that turn the expedition's
  dominant lookups into **point reads** rather than scans;
- **implement** optimistic concurrency with **entity ETags** and group related
  writes into a **transactional batch**; and
- **measure** the request cost of a point read, a partition scan, and a table
  scan on the same data set, and say which one a given filter will produce.

## The question this module answers

Blobs answer "give me these bytes, by name". They cannot answer "which stations
have not reported since noon?" — the name is the only index a container has, and
module 4 showed that a prefix scan is a string comparison and nothing more.

Table storage is the cheapest way to answer the second kind of question, and it
comes with one condition attached:

> **You get exactly two indexed columns, you choose them before you have any
> data, and you can never add a third.**

There are no secondary indexes to bolt on later, no query planner to outsmart,
and no `CREATE INDEX`. If a lookup cannot be expressed with those two strings,
the service reads every row and charges you for all of them. **Key design is the
only performance decision in this service**, and it is made on day one.

## The entity: two keys and no schema

Every entity has four system properties, and everything else is a column that
exists only on the rows that set it.

| property | who sets it | what it does |
| --- | --- | --- |
| `PartitionKey` | you | the unit of scale, of transaction, and of scan |
| `RowKey` | you | identity within the partition; sorted ascending as a string |
| `Timestamp` | the service | last write time; read-only |
| `ETag` | the service | the version, for optimistic concurrency |

Two rows in the same table may carry entirely different properties. That
flexibility is real, and it is also why the service cannot index anything for
you: it does not know what the rows contain.

The exercise's entity, in full:

```csharp
public sealed class ObservationEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string StationId { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public double TemperatureC { get; set; }
    public string Status { get; set; } = "pending";
}
```

Note that `StationId` and `ObservedAt` are *also* in the keys. That duplication
is deliberate and it is the single most important thing on this page: querying
`StationId` and querying `PartitionKey` return the same rows and cost
dramatically different amounts.

## The partition key is the only decision that matters

A partition is the unit of three separate things at once:

- **scale** — one partition is served by one partition server, so a hot
  partition is a throughput ceiling no amount of provisioning removes;
- **transaction** — a batch may not leave one partition, ever;
- **scan** — a filtered query costs the partition it is confined to.

Those pull in opposite directions. Fewer, larger partitions make more writes
atomic and more scans expensive; more, smaller partitions make scans cheap and
atomicity rare. There is no setting that resolves it, which is why the choice is
a design exercise rather than a configuration one.

| candidate | atomicity | scan cost | failure mode |
| --- | --- | --- | --- |
| one constant, e.g. `"observations"` | everything is batchable | scan = whole table | one partition server for the entire workload |
| `StationId` | a station's writes are atomic | grows without bound | a station reporting per minute hits a million rows in two years |
| `StationId` + day | a station-day is atomic | one day of one station | a query spanning two days spans two partitions |
| a GUID per row | nothing is batchable | point reads only | no range query is possible at all |

The exercise chooses `StationId + day`, joined with `|`, because the
expedition's dominant lookup is "what did this station report today". State your
dominant lookup first, then derive the key. Deriving it the other way round is
how tables end up scanned.

### Keys are strings with forbidden characters

`/`, `\`, `#`, `?`, and control characters are rejected outright, and a key may
not be empty or exceed 1,024 characters. This matters because the obvious thing
to do — reuse module 4's blob name as a key — is illegal:

```text
observations/station-bravo/2026/07/06/frame-0001.jpg   ← contains '/', not a key
```

The failure arrives at write time, on whichever station id first contains a
slash, in production.

## Row keys sort as strings

There is exactly one storage order — `RowKey` ascending, ordinal, as a string —
and it cannot be changed. Two consequences follow, and both are silent when they
bite.

**Pad everything.** A row key of `9:05` sorts *after* `10:05`, because `'9'` is
greater than `'1'`. A range query over unpadded keys returns a plausible,
smaller, wrong answer. The exercise uses a fixed-width UTC format,
`yyyy-MM-ddTHH:mm:ss.fffffffZ`, so every key is the same length and chronological
order is string order.

**To read newest-first, invert the key.** There is no descending index to ask
for. The technique is arithmetic:

```csharp
var inverted = DateTime.MaxValue.Ticks - observedAt.ToUniversalTime().UtcTicks;
return inverted.ToString("D19", CultureInfo.InvariantCulture);
```

A later instant produces a smaller number, so ascending storage order is
newest-first. `D19` matters as much as the subtraction: without fixed width the
padding problem returns immediately.

## Three query shapes, one syntax

Every query is one of three things, decided entirely by which keys the filter
pins:

| shape | filter pins | entities read | cost as the table grows |
| --- | --- | --- | --- |
| **point read** | `PartitionKey` and `RowKey` | 1 | constant |
| **partition scan** | `PartitionKey` only | the partition | grows with the partition |
| **table scan** | neither | the table | grows with everything |

The syntax is identical in all three cases, which is the whole problem. The SDK
will run a table scan without a warning, and it will be fast on the 400 rows in
your development table.

The most common accident is filtering the *duplicated* column:

```text
StationId    eq 'station-03'   -> table scan,     1000 rows returned, 37.7 ms
PartitionKey eq 'station-03|…' -> partition scan, 1000 rows returned,  3-8 ms
```

Same rows. Same result. One reads a partition and one reads everything. And a
filter on a non-key property does not narrow the key range the service must
consider; it only reduces what is returned. The companion reports 58 returned
rows, the 1,000-row candidate partition, requests/pages, and elapsed time.
Table Storage does not expose an exact server-side scanned-row counter.

## The ETag is back, with a different API

Module 5's blob ETag returns here unchanged in meaning: read a version, write
betting on it, and be told when you lose. Only the surface differs — a table
update takes the ETag as an *argument* rather than a header you set, and the
failure is `UpdateConditionNotSatisfied` rather than `ConditionNotMet`.

The one dangerous convenience is `ETag.All`, which means "overwrite whatever is
there". It is the last-write-wins default module 5 spent an entire module
removing, and it is one identifier away from the safe call.

Two rules carry over verbatim:

1. **Bet on the ETag from *this* read**, never a cached or hard-coded one.
2. **Re-read inside the retry loop.** Retrying with the same stale ETag fails
   forever; retrying with a fresh ETag and stale *data* silently reintroduces
   the lost update. Both look like a working retry from outside.

## A transaction that cannot leave its partition

A table transaction is real — every operation succeeds or none does — with three
limits:

- at most **100 operations**;
- **exactly one partition key**, with no exceptions and no workaround;
- **no duplicate row key** within the batch.

The operation limit is a splitting problem: chunk at 100 and submit repeatedly.
The partition limit is a **design** problem. There is no cross-partition
transaction in this service, so "these two entities must land together" is a
requirement that they *share a partition key* — and that is decided before any
data exists.

Splitting a cross-partition batch by partition is the honest fallback, and it is
worth saying plainly what it costs: **the atomicity is gone.** Each resulting
batch succeeds or fails alone, and the caller must tolerate a partial outcome.

## Run the companion

```bash
docker compose up -d azurite
dotnet run --project lessons/07-table-storage/ObservationIndex
```

The companion writes 5,000 entities across 5 partitions, then asks the same
question three ways. Real output, captured from a run against Azurite 3.36.0.
ETags carry the write timestamp and the elapsed milliseconds depend on your
machine, so this is one representative occurrence; the entity counts and the
*relative* cost of the three queries are the reproducible part.

```text
0. Seeding
----------
   5000 entities in 50 transactional batches (792 ms)
   5 partitions of 1000 rows each

1. Point read: both keys known
------------------------------
   PartitionKey        : station-03|2026-07-06
   RowKey              : 2026-07-06T08:20:00.0000000Z
   Entities returned   : 1
   Entities read by the service: 1 (a keyed GET reads one row, by construction)
   ETag                : W/"datetime'2026-08-07T10%3A57%3A20.7463059Z'"
   Elapsed             : 8.5 ms

2. Partition scan: partition key only
-------------------------------------
   Filter              : PartitionKey eq '…' and RowKey eq '…'
   Entities returned   : 1
   Elapsed             : 28.8 ms

   Now filter the same partition on a NON-KEY property:
   Filter              : PartitionKey eq '…' and TemperatureC lt -18.0
   Entities returned   : 58
   Elapsed             : 3.4 ms
   The partition holds 1000 candidate rows. The service returned 58.
   Table Storage does not expose an exact server-side scanned-row count.

3. Table scan: no key predicate
-------------------------------
   Filter              : RowKey eq '…'   (no PartitionKey!)
   Entities returned   : 5
   Elapsed             : 2.1 ms
   Same row key in every partition, so it returned 5 rows from 5000.

   And the query that LOOKS identical to a partition scan:
   Filter              : StationId eq 'station-03'
   Entities returned   : 1000
   Elapsed             : 37.7 ms
   Same rows as a partition scan, same syntax, and the service had to read
   the whole table to find them: StationId is a duplicated column, not a key.

4. The entity ETag
------------------
   alice read ETag     : W/"datetime'2026-08-07T10%3A57%3A20.7463059Z'"
   bob   read ETag     : W/"datetime'2026-08-07T10%3A57%3A20.7463059Z'"  (identical: same version)
   alice write         : HTTP 204, new ETag W/"datetime'2026-08-07T10%3A57%3A21.2458034Z'"
   bob   write         : REJECTED UpdateConditionNotSatisfied (HTTP 412)
   Bob was told. That is the whole difference between a lost update and a
   retry: one is silent and one is a status code.
   stored Status       : ingested

5. Transactional batches
------------------------
   two rows, one partition   : accepted, 2 sub-responses
   two rows, two partitions  : ACCEPTED — the emulator does not enforce this rule
   101 rows, one partition   : the emulator returned a response the SDK could not parse (Expected an HTTP status line, not changesetresponse_dfad7b29-b182-4729-a28d-1e08cf5bb13b)
```

Note the elapsed times in sections 2 and 3. On 5,000 rows they are noise —
2 ms against 38 ms, occasionally inverted by connection warm-up. That is
precisely the point: **the cost difference is invisible at development scale and
linear at production scale.** Do not tune from a stopwatch; reason from the
query shape.

### What the emulator will not tell you

Section 5 is the interesting failure. Azure rejects both an oversized batch and
a cross-partition batch with `InvalidInput`. Azurite:

- **accepted the cross-partition batch outright**, and
- answered the 101-operation batch with a multipart response the SDK could not
  parse (`Expected an HTTP status line, not changesetresponse_…`).

Neither is a bug you can work around; both are reasons the exercise validates
these rules in your own code. A design that relies on a cross-partition
transaction passes every emulator test you will ever write and fails on its
first real deployment.

This module still needs no live checkpoint, because the divergence is *known,
named, and compensated for in code*. Contrast
[module 5](../05-blob-lifecycle/README.md#environments), where the emulator
cannot answer the question at all.

## The management labs

Same ten steps, twice, so the shape survives whichever tool your team uses:

```bash
docker compose up -d azurite
bash infra/azure-cli/table-storage.sh
```

```bash
docker compose up -d azurite
pwsh -File infra/powershell/table-storage.ps1
```

Step 5 runs the `StationId eq …` table scan next to the `PartitionKey eq …`
partition scan so the identical syntax is visible side by side. Step 7 reuses
one ETag twice and is rejected the second time with HTTP 412.

Both scripts print the endpoint they are about to write to before writing
anything, and both delete their table at the end.

## A bounded experiment

Fifteen minutes, one uncomfortable answer. The table holds 5,000 rows in every
run below; only the partition layout changes.

1. In `ObservationIndex/Program.cs`, change `Stations` from `5` to `50` and
   `ReadingsPerStation` from `1000` to `100`.
2. Re-run the companion and read section 3.

Observed result — 50 partitions of 100 rows (the elapsed time is per run; the
entity count is the point):

```text
   Filter              : StationId eq 'station-03'
   Entities returned   : 100
   Elapsed             : 3.4 ms
```

3. Now set `Stations` to `1` and `ReadingsPerStation` to `5000` — the
   constant-partition-key design from the table above. Only one station exists in
   that layout, so replace every `station-03` in `Program.cs` (there are three,
   on lines 114, 183, and 188) with `station-01`.

Observed result — one partition of 5,000 rows:

```text
   Filter              : StationId eq 'station-01'
   Entities returned   : 5000
   Elapsed             : 155.0 ms
```

Then revert the edits.

Three things are worth extracting from those numbers.

**The point read never moved.** Between 8 and 10 ms in all three layouts, with
the run-to-run spread larger than the difference between them. That is the
property the entire key design exists to buy, measured rather than asserted.

**The scan got 40× slower** as partitions were merged — and nothing about the
data, the filter, or the code changed. Only the key did.

**The observable cost tracks candidate range, returned entities, pages, and
latency.** The client cannot report how many rows the server examined
internally. The 152 ms run returned 5,000 entities; the narrower runs returned
fewer. Treat those as observations, not a fabricated scan count. Key design is
the lever that narrows the candidate range, and no partition layout makes a
table scan cheap.

## Common mistakes and how to diagnose them

| symptom | likely cause | how to confirm |
| --- | --- | --- |
| queries are fast in development and time out in production | a table scan that was cheap on 400 rows | check whether the filter names `PartitionKey`; if not, it is a scan |
| a filter on an indexed-looking column is slow | it filters the *duplicated* property, not the key | compare `StationId eq …` with `PartitionKey eq …` |
| a range query misses rows | row keys are not fixed width, so `9:05` sorts after `10:05` | print two adjacent keys and compare them with `string.CompareOrdinal` |
| newest-first paging is expensive | you are reading the whole partition to sort it | invert the row key instead; there is no descending index |
| writes to one station throttle while others are idle | a hot partition; one partition is one partition server | check whether the partition key varies across the hot workload |
| `InvalidInput` on a batch that works locally | the batch spans partitions; Azurite does not enforce this | see [What the emulator will not tell you](#what-the-emulator-will-not-tell-you) |
| an entity update silently overwrote a colleague's change | the write passed `ETag.All` | log the ETag argument; anything printing `*` is last-write-wins |
| `UpdateConditionNotSatisfied` never stops | the retry does not re-read, so it resends the same stale ETag | assert that no two attempts bet on the same ETag |
| a key is rejected at write time | it contains `/`, `\`, `#`, `?`, or a control character | blob names are the usual source |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/07-table-storage/tests -p:Implementation=starter

```

The starter has ten numbered gaps, in dependency order: key construction and key
legality (GAPs 1–4), query classification, filter construction, and cost
measurement (GAPs 5–7), optimistic concurrency and its retry loop (GAPs 8–9),
and batch validation and splitting (GAP 10). Each throws a
`NotImplementedException` naming the section of this page that derives it.

**Untouched-starter baseline: fails.** 93 of 95 checks fail, the first with:

```text
System.NotImplementedException : GAP 1: implement ObservationKeys.PartitionKeyFor.
See lessons/07-table-storage/README.md#the-partition-key-is-the-only-decision-that-matters.
```

That failure is your next action, not a repository defect. (The two passing
checks read a published constant and reject a null constructor argument, both of
which the starter already provides.)

The evaluator is deterministic and offline. Concurrency is modelled by a
`RacingTable` that enforces ETag preconditions exactly as the service does and
lets a scripted competitor land *between* your read and your write, so a hoisted
read is detectable rather than merely improbable.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| the conditional write passes `ETag.All` instead of the ETag that was read | 7 failures, including `ALostUpdateIsNotSilentlyOverwritten` — *Expected: -9, Actual: -3* — and `TheWriteBetsOnTheEtagThatTheReadReturned` — *Expected: "W/"0x1"", Actual: "*"* |
| `RowKeyFor` drops zero padding (`yyyy-M-dTH:mm:ss`) | 3 failures: `TheNineOClockTrapDoesNotBite` — *'2026-7-6T9:00:00' must sort before '2026-7-6T10:00:00' or every range query is wrong*; `RowKeysSortChronologicallyAsStrings`; `EveryRowKeyIsTheSameWidth` — *the collection contained 2 items: [17, 18]* |
| a range filter uses `ObservedAt ge …` instead of `RowKey ge …` | 1 failure: `ARangeFilterUsesARowKeyInequalityNotAPropertyOne` — *Not found: "RowKey ge"* |

The first fault is the one to notice: every uncontended test still passes, the
data is still written, and the only thing that changed is what happens when
somebody else is writing too.

Running these mutations also found a **real defect in the evaluator itself**: the
padding test originally built its instants with `DateTimeOffset.Date`, which is
machine-local, so on a UTC+2 machine 9 a.m. and 10 a.m. both became single-digit
UTC hours and the trap never sprang. It now uses explicit `TimeSpan.Zero`
offsets. A test that cannot fail is not a test.

## Environments

- **Emulator.** `docker compose up -d azurite` for the companion and for both
  management labs. The exercise evaluator needs nothing running.
- **Live checkpoint: not required.** The two known emulator divergences are
  named in [What the emulator will not tell you](#what-the-emulator-will-not-tell-you)
  and compensated for by `BatchValidator`, which is why they are taught rather
  than merely encountered.

## Review questions

1. You may index exactly two columns and you choose them before you have data.
   State the expedition's dominant lookup, then derive both keys from it.
2. `StationId eq 'station-03'` and `PartitionKey eq 'station-03|2026-07-06'`
   return overlapping rows. Which is the accidental table scan, and why does it
   look identical in code?
3. A range query over row keys returns 40 rows where you expected 55, and the
   missing ones are all from before 10 a.m. Diagnose it in one sentence.
4. There is no descending index. Describe, in code, how you page newest-first,
   and say why the width of the formatted number matters as much as the
   subtraction.
5. Two entities must be written atomically. What does that requirement force
   about their partition keys, and what do you lose if you split them instead?
6. A filter reads `PartitionKey eq '…' and TemperatureC lt -18.0` and returns 58
   of 1,000 rows. How many did the service read, and what did you pay for?
7. Your batch test passes against Azurite and fails on first deployment with
   `InvalidInput`. Name the two rules Azurite does not enforce.
8. A retry loop keeps failing with `UpdateConditionNotSatisfied` forever. Give
   the bug, then give the *different* bug that a naive fix introduces.

## What you can now assume

You can now store bytes, control their versions and lifetime, dispatch work
about them without losing it, and index them so the expedition's questions are
answered by point reads rather than by scans. That is the whole Storage account
— the four services, their consistency models, their cost models, and their
failure modes — with each claim in these seven modules measured rather than
asserted.

Next, the **Field Station** project removes the chapter-by-chapter scaffolding:
you will combine Blob, Queue, and Table behind application-owned ports before
the course introduces a new service.
