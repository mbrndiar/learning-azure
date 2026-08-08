# 🌌 10. Design the global journal

Module 9 left a projection in memory. This module gives it somewhere to live —
and introduces the one decision in Azure that you cannot change afterwards
without moving every document you own.

There is exactly one hard idea here, and everything else is arithmetic on top of
it:

> **The partition key is not a schema detail. It is the shape of every query you
> will ever be able to afford, and it is fixed at container creation.**

## Objectives

By the end of this module you can:

- explain why `(partition key, id)` is the primary key, and predict the exact
  failure a point read with the wrong key produces;
- measure a candidate partition key's cardinality and skew, and say which of the
  two failures is unfixable;
- project a logical partition against the 20 GB ceiling before it is reached;
- build a composite or bucketed key when no property in the document is usable
  on its own, and state what the bucketing costs on reads;
- estimate a query's request-unit charge from documents examined and partitions
  touched, and price a whole workload against a provisioned RU/s figure;
- explain why a container provisioned for 10,000 RU/s throttles while consuming
  900, and why adding throughput does not always help;
- decide between manual and autoscale throughput with the floor included;
- price an indexing policy and know which direction the trade runs.

## The question this module answers

Eight field stations report temperature readings. The same 200 documents are
written to two containers, identical in every byte, differing in one property
name: one is partitioned by `/stationId`, the other by `/day`.

Both answer "everything station-05 reported today" correctly. One of them costs
eight times as much to do it, and the multiplier is not eight — it is *the
number of stations*.

That is the whole module. Everything below is the machinery for seeing that
number before you have written any documents at all.

## The partition key is half the address

A document in Cosmos DB is addressed by two values, not one: the partition key
and the id. Cosmos hashes the partition key to decide which physical partition
holds the document, and the id is unique only *within* that logical partition.

This has a consequence people meet in production rather than in a tutorial: a
point read with the correct id and the wrong partition key returns **404 Not
Found**. Not an error, not a redirect, not a slower answer. The document exists.
The read looked for it somewhere it never was, and the absence is indistinguish-
able from an absence.

The companion demonstrates it directly, and the two lines are worth staring at:

```text
   Read station-05-0007 with /stationId = station-05
     status                  : 200 OK

   Read station-05-0007 with /stationId = station-01
     status                  : 404 NotFound
```

The practical rule that follows: an id is only useful if the partition key
travels with it. A URL, a message payload, or a foreign key that carries only
the id has thrown away half the address, and the code that consumes it must
either reconstruct the key or fall back to a cross-partition query — which is
the most expensive operation in the system.

### Cardinality is not distribution

Two different things go wrong with a partition key, and they are usually
confused:

**Cardinality** is how many distinct values the key takes. `/day` on a single
day's data has cardinality 1. Cosmos cannot split one logical partition across
physical partitions, so a low-cardinality key puts a hard ceiling on the system
that no amount of provisioned throughput removes.

**Skew** is how evenly the documents spread across those values. A key can have
ten thousand distinct values and still be useless if one tenant holds ninety
percent of the data.

`PartitionKeyAdvisor.Measure` reports skew as *the largest partition divided by
the average partition*, and the choice of denominator is the point. Measuring
against the **total** is the natural-looking alternative and it is wrong: that
number falls automatically as cardinality rises, so a key with ten thousand
values and one enormous partition scores well on it and takes the system down
anyway. Against the average, a perfectly flat key scores exactly 1.0 at any
cardinality, and a key with one partition holding ten times its share scores 10
whether there are four values or four million.

The order in which the two are checked matters as well. Low cardinality is
reported first because it is the failure that cannot be repaired: a skewed key
can be spread with a synthetic suffix, but a key with three possible values has
three partitions forever.

### A logical partition is a ceiling

One logical partition — that is, one distinct partition key value — holds at
most **20 GB**. This is not a soft limit or a performance guideline. When it is
reached, writes to that key fail with `403` sub-status `1014`, and there is no
setting that raises it. The repair is a new partition key and a migration of
every affected document.

The important word is *logical*. The container may hold terabytes. It is a
single key value that is capped, which is exactly why `/day`, `/tenantId` for a
system with one large tenant, or a constant like `/type` are the keys that fail
in year two rather than in the first week.

`PartitionKeyAdvisor.WillOutgrowLogicalPartition` does the only arithmetic that
matters: daily documents in the worst partition, times document size, times the
retention window, against 20 GB. It answers one question — *does this key have
an end date?* — and it is worth asking before the container exists, because it
cannot be asked usefully afterwards.

### When no natural key works, make one

Sometimes no property in the document is a good key. There are two standard
repairs, and they are the two `SyntheticKeyBuilder` implements.

**Compose several properties.** `tenantId|deviceId` has the product of both
cardinalities and stays queryable, because a query that knows both values knows
the key. Three rules make it safe: every part must be present, the separator must
never occur inside a part, and the order is permanent. Omitting an absent part
rather than refusing it is the subtle one — it silently places the same entity in
a *different* logical partition from every complete document, and no query finds
both halves.

**Add a bucket to a hot key.** When the key must be `/day` because that is what
the queries filter on, append a bucket: `2026-08-07-003`. The suffix must be
derived from the document — hash the id — and not randomly generated. A random
suffix spreads writes just as well and makes the document *unreadable*, because
a point read needs the partition key and nothing in the document says which
bucket it went to.

The bucketing is not free, and the cost is explicit: `FanOutKeys` returns the
list of keys a query for the whole day must now be issued against. One write
partition became eight; so did every read that wants the whole key. That trade
is the decision, and it should be made with the list in front of you.

## Read amplification is the number to watch

Request units are a charge. A point read of a 1 KiB item by id and partition key
costs **1 RU under Eventual or Session consistency**. Strong and Bounded
Staleness reads cost twice as many RUs, and larger items cost more. Query charge
also includes index lookup, item loading and return, and compilation; use
response charges and query metrics from a real account rather than treating a
local formula as Azure's meter.

So the useful local measurement is not the charge — it is the ratio:

```
read amplification = documents examined / documents returned
```

A keyed query that reads exactly what it returns scores 1.00. The companion's
section 4 asks both containers the same question:

```text
                              returned   partition   amplification
   /stationId                      25          25   1.00x
   /day                            25         200   8.00x
```

Eight stations, eight times the work. `QueryCostModel.ReadAmplification` also
answers the case everyone forgets: a query that returns *nothing* still did all
the work. Reporting 1.0 there, or dividing by zero and letting infinity out,
hides the single most expensive query an application can run — the scan that
finds no match.

### Fan-out scales with partitions, not results

A query that cannot name a partition key is not a slower query. It is a
different query: the SDK dispatches it to **every physical partition**, each one
evaluates it, and the client merges the results.

That is why `QueryCostModel.Estimate` multiplies the per-partition *overhead* by
the partition count and leaves the document term alone. The documents exist
once, wherever they live; the overhead is paid once per partition asked. A
cross-partition query that returns a single document out of a hundred-partition
container still pays for a hundred partitions, which is how the cheapest-looking
query in a codebase becomes its largest line item.

And the cost grows on its own. A container that splits from two physical
partitions to four does not change one line of application code, and every
cross-partition query in it just became twice as expensive.

`RequestUnitsPerSecond` closes the loop by pricing the workload rather than the
query. An expensive query is not automatically the one to fix: a 500 RU report
that runs once an hour costs 0.14 RU/s, and a 3 RU read that runs 200 times a
second costs 600. Provisioned throughput is a rate, so the workload has to be
expressed as one before the two numbers can be compared.

## Throughput is divided before it is spent

This is the fact that produces the most confused support cases in Cosmos DB.

Provisioned RU/s is a total for the container, and Cosmos divides it **evenly
across physical partitions**. A partition cannot borrow from an idle neighbour.
400 RU/s over four physical partitions is 100 RU/s each, permanently, no matter
what the other three are doing.

So there are two ceilings, and `ThroughputPlanner.WillThrottle` checks both:

- the container total, and
- the busiest logical partition against its physical partition's share.

Checking only the total produces the classic ticket: *"we are provisioned for
10,000 RU/s, the chart says we are consuming 900, and we are being throttled."*
Both statements are true. The 900 is concentrated on one partition entitled to
1,000, and it is peaking above it.

It also explains the counter-intuitive remedy. Adding throughput causes a
container to split, and every split halves each partition's share — so a hot
partition that was throttling at 10,000 RU/s over ten partitions is throttling
just as hard at 20,000 over twenty. The fix for a hot partition is a different
partition key, not a bigger number.

The choice between manual and autoscale is arithmetic too, and it has a trap.
Classic autoscale bills the **highest RU/s reached in each hour**, never below
10% of the configured maximum for that hour. Averaging raw requests over a day
erases the peaks the meter charges for. In a single-write-region account, the
autoscale meter is 1.5x the manual rate; multiple-write-region pricing differs.
`RelativeAutoscaleCost` therefore accepts one peak per billed hour and applies
the floor before summing.

Dynamic autoscale changes where scaling happens: each physical partition and
region can scale independently rather than every partition scaling to the
hottest partition's level. It does not turn billing into request averaging.
Flat load generally favors manual throughput; spiky load can favor autoscale,
but calculate it from hourly maxima for the account's region configuration.

### Consistency is part of the read budget

Session consistency is the usual application default because a client carrying
its session token gets read-your-writes without paying the global coordination
cost of Strong consistency. If work moves to another process, propagate the
session token when that guarantee matters. Request-level overrides may weaken
the account default, but cannot strengthen it. Strong and Bounded Staleness
double read RU cost, so consistency belongs in both the correctness design and
the throughput estimate.

## Indexing is a write tax you choose

Cosmos DB indexes **every path of every document** by default. That default is
right often enough that most people never look at it, and it is a real cost with
a clear direction:

- A read pays for an index **once**, at the moment it uses it.
- A write pays for **every** index the document touches, whether or not anything
  ever queries them.

`IndexingPlanner.WriteCost` prices the asymmetry, and `SavingsPerSecond`
multiplies the difference by the write rate — and deliberately allows a negative
result. Adding a composite index to make one query cheaper makes *every write*
more expensive, and the sign of that number is how the trade gets settled.
Returning an absolute difference, or clamping at zero, turns the only question
worth asking into one that always answers yes.

The one hard requirement is composite indexes. A single-property `ORDER BY` is
served by the range index every indexed path already has. A **multi-property**
`ORDER BY` is not served at all unless a composite index lists exactly those
properties in exactly that order — Cosmos returns an error rather than doing the
sort. That makes it a deployment-time failure rather than a slow query, and it
makes `RequiresMissingCompositeIndex` a check worth having in a test suite:
`(day, celsius)` does not serve `ORDER BY celsius, day`, and a prefix does not
serve a longer query.

## ▶️ Run the companion

```bash
docker compose up -d cosmos
curl -sf http://127.0.0.1:8080/ready && echo ready
dotnet run --project lessons/10-cosmos-modeling/RequestUnits
```

Real output, captured from a run against `azure-cosmos-emulator:vnext-EN20260706`.
The `/day` partition key is today's date, so that value tracks the day you run
it; RU charges and elapsed times vary slightly per run. This is one
representative occurrence — every document count, partition count, and
amplification ratio in it is reproducible.

```text

0. One decision, two containers: The documents are identical. The partition key is not.
---------------------------------------------------------------------------------------
   readings-by-station    partition key /stationId   400 RU/s
   readings-by-day        partition key /day         400 RU/s
   station-serials        partition key /region      400 RU/s

   The first two hold exactly the same documents. Everything that
   follows is a consequence of that one property name.

1. The documents: Eight stations, twenty-five readings each, all on one day.
----------------------------------------------------------------------------
   Documents written         : 200 to each container
   Stations                  : 8
   Readings per station      : 25

   Not one byte differs between the two containers. What differs is
   where each document landed, and that is decided entirely by the
   partition key path declared when the container was created.

2. The point read: The partition key is not metadata. It is half the address.
-----------------------------------------------------------------------------
   Read station-05-0007 with /stationId = station-05
     status                  : 200 OK
     celsius                 : -16.5

   Read station-05-0007 with /stationId = station-01
     status                  : 404 NotFound

   The document exists. The read still failed, and it failed with 404
   rather than an error, because the id was looked for in a partition
   that never held it. An id is unique WITHIN a logical partition, not
   within a container: (partition key, id) is the primary key.

3. A query inside one logical partition: Everything examined is something returned.
-----------------------------------------------------------------------------------
   Documents returned        : 25
   Documents in the partition: 25
   Read amplification        : 1.00x

   The filter is the partition key, so the partition it selects holds
   nothing else. Read amplification of 1.00x is the target every
   partition key design is aiming at, and the number that tells you
   whether it hit.

4. The same question, two models: 'Everything station-05 reported on 2026-08-07.'
---------------------------------------------------------------------------------
                              returned   partition   amplification
   /stationId                      25          25   1.00x
   /day                            25         200   8.00x

   Same question, same answer, same documents. The /day container had
   to sit on a partition holding all 8 stations and discard 7
   readings out of every 8. That ratio is not a constant: it IS the
   number of stations. Add stations and the /stationId model does
   not move, while /day gets linearly worse.

5. Skew: A partition key is a promise about distribution, and it is checkable.
------------------------------------------------------------------------------
   /stationId   partitions   8   largest   25 docs (12.5% of all documents)   key of largest: station-01
   /day         partitions   1   largest  200 docs (100.0% of all documents)   key of largest: 2026-08-07

   A logical partition is capped at 20 GB and served by one physical
   partition's throughput. /day does not just read badly: on the day
   it is current it absorbs EVERY write in the system, and no amount
   of provisioned RU/s can be spent on a partition that does not exist.

6. The query that cannot name a key: Fan-out is not a slower query. It is a different query.
--------------------------------------------------------------------------------------------
   Documents returned        : 112
   Documents in container    : 200
   Read amplification        : 1.79x
   Logical partitions        : 8
   Physical partitions       : 1

   The predicate touches no partition key, so the query is dispatched
   to every physical partition and the results are merged by the
   client SDK. The cost scales with the number of physical partitions,
   not with the number of rows that come back — which is why a
   cross-partition query that returns one document can still be the
   most expensive operation in an application.

7. Unique keys: The only uniqueness Cosmos enforces beyond the id, and it is per partition.
-------------------------------------------------------------------------------------------
   Created north-01   region=arctic   serial=SN-4417
   Rejected north-02  region=arctic   serial=SN-4417  -> 409 Conflict
   Created south-01   region=antarctic serial=SN-4417

   The same serial was refused inside one region and accepted in
   another. A unique key policy is scoped to the logical partition, so
   global uniqueness is only available when the partition key is
   global — and the policy is fixed for the life of the container.

8. Throughput: Provisioned RU/s is a rate, and it is divided before it is spent.
--------------------------------------------------------------------------------
   Manual throughput         : 400 RU/s
   Autoscale maximum         : 1000 RU/s
   Autoscale floor (10%)     : 100 RU/s

   Provisioned throughput is split evenly across PHYSICAL partitions,
   and each logical partition is capped at its physical partition's
   share. 400 RU/s over four physical partitions is 100 RU/s each, no
   matter how idle the other three are. This is why a hot partition
   throttles while the account-level chart shows plenty of headroom.

9. What this run could not measure: The emulator answers questions about shape, not about price.
------------------------------------------------------------------------------------------------
   Every response above carried a request charge of 1 RU, including
   the 200-document cross-partition query. That is not a discovery
   about Cosmos; it is a limitation of the emulator, which does not
   run the metering that produces a real charge. The same is true of
   the query metrics header: retrievedDocumentCount comes back as 0.

   So this lesson measures the thing the emulator DOES model — how
   many documents a question has to be asked of — and treats request
   units as what they are: a charge proportional to that number. The
   proportion itself has to be read off a real account, which is what
   the live checkpoint in this module is for.

Deleted database expedition.
```

### What the emulator will not tell you

Section 9 of the companion says it out loud, because it is unusually severe here
and pretending otherwise would teach the wrong lesson.

**Request charges are fake.** Every response the emulator returns carries
`x-ms-request-charge: 1`, including a cross-partition query over 200 documents.
There is no metering behind it.

**Query metrics are empty.** The `x-ms-documentdb-query-metrics` header is
present and populated with zeros: `retrievedDocumentCount=0`,
`outputDocumentCount=0`, `indexUtilizationRatio=1.00` regardless of what the
query did. So "how much did the engine look at" has to be measured a different
way locally — which is why the companion counts logical partition sizes with
`SELECT VALUE COUNT(1)` and derives amplification from that.

**There is exactly one physical partition, forever.** `GetFeedRangesAsync`
returns a single range no matter how much data is written. Partition splits, the
event that halves every partition's throughput share, cannot be observed at all.

**There is no rate limiter.** 429 and `x-ms-retry-after-ms` never appear. A load
test against the emulator proves nothing whatsoever about capacity.

**An excluded path is not enforced on reads.** Real Cosmos refuses an `ORDER BY`
on an excluded path; the emulator answers it anyway. A policy that works locally
can fail on deployment.

What the emulator *does* model faithfully is everything about *shape*: partition
key placement, the 404 on the wrong key, unique key conflicts scoped per
partition, `GROUP BY` distribution, throughput and autoscale settings on the
control plane. That is what this module measures locally, and it is why the live
checkpoint is **required** rather than optional.

## 🛠️ The management labs

```bash
bash infra/azure-cli/cosmos-modeling.sh
```

```bash
pwsh -File infra/powershell/cosmos-modeling.ps1
```

Both are **live**, both prompt for confirmation showing the subscription they are
about to bill, and both delete their resource group at the end. Same nine steps,
same names, same order.

Step 3 creates the two containers and shows the partition key the service
recorded. Note what is *absent* from both tools: there is no command that changes
a partition key path. It is not an oversight — changing it means moving every
document into a different logical partition, which is a migration rather than an
edit.

Step 4 reads the throughput, migrates the container to autoscale, and reads it
back. Watch `minimumThroughput`: it is the floor the service will not let you go
below, it rises with stored data and with the number of physical partitions the
container has *ever* had, and it never comes back down.

Step 5 prints the two exports that point the companion at the live account. Run
it there and record the request charges for the point read, the single-partition
query and the cross-partition query. Locally all three are 1.00 RU. In Azure
they are not, and the ratio between them is the lesson this module exists for.

Step 7 asks the platform for `TotalRequestUnits` and `TotalRequests` — the
consumption and throttling meters that have no local equivalent.

Step 8 narrows the indexing policy to two paths. It applies asynchronously via a
background reindex that leaves the container queryable throughout, which is why
the saving does not appear the instant the command returns.

## 🔬 A bounded experiment

Ten minutes, one run, one constant changed. `Stations` is on line 36 of
`RequestUnits/Program.cs`. Sections 4 and 5 are the only parts of the output you
need.

**Quadruple the number of stations.** Set `Stations = 32`.

Observed:

```text
4. The same question, two models: 'Everything station-05 reported on 2026-08-07.'
---------------------------------------------------------------------------------
                              returned   partition   amplification
   /stationId                      25          25   1.00x
   /day                            25         800   32.00x

   Same question, same answer, same documents. The /day container had
   to sit on a partition holding all 32 stations and discard 31
   readings out of every 32. That ratio is not a constant: it IS the
   number of stations. Add stations and the /stationId model does
   not move, while /day gets linearly worse.

5. Skew: A partition key is a promise about distribution, and it is checkable.
------------------------------------------------------------------------------
   /stationId   partitions  32   largest   25 docs (3.1% of all documents)   key of largest: station-01
   /day         partitions   1   largest  800 docs (100.0% of all documents)   key of largest: 2026-08-07

   A logical partition is capped at 20 GB and served by one physical
   partition's throughput. /day does not just read badly: on the day
   it is current it absorbs EVERY write in the system, and no amount
   of provisioned RU/s can be spent on a partition that does not exist.
```

Compare with the baseline run above, where `Stations = 8`:

| | `Stations = 8` | `Stations = 32` |
| --- | --- | --- |
| `/stationId` logical partitions | 8 | 32 |
| `/stationId` largest partition | 25 docs (12.5%) | 25 docs (3.1%) |
| `/stationId` amplification | 1.00x | **1.00x** |
| `/day` logical partitions | 1 | 1 |
| `/day` largest partition | 200 docs (100%) | 800 docs (100%) |
| `/day` amplification | 8.00x | **32.00x** |

Three things to take from it.

**The good key did not move.** Not "improved slightly" — it is identical, and it
will be identical at 32,000 stations. That is what a partition key that matches
the access pattern buys: a cost per query that is independent of the size of the
system.

**The bad key degraded exactly linearly.** 8 -> 32 stations, 8.00x -> 32.00x.
The multiplier was never a constant; it was always the station count wearing a
disguise. A load test at eight stations would have found `/day` perfectly
acceptable.

**Skew as a share of the total went the wrong way.** `/stationId`'s largest
partition fell from 12.5% to 3.1% while `/day` stayed at 100%. If you were
scoring candidates by share-of-total, the *good* key's score improved for a
reason that has nothing to do with the key — which is precisely why
`PartitionKeyAdvisor.Measure` scores against the average instead.

## ⚠️ Common mistakes and how to diagnose them

| symptom | likely cause | how to confirm |
| --- | --- | --- |
| a document that exists reads as 404 | point read issued with the wrong partition key | re-run the read as a cross-partition query on `c.id`; if it comes back, the key was wrong |
| a query is fast in test and ruinous in production | it is cross-partition and test had one physical partition | check `x-ms-documentdb-query-metrics` for partitions touched, not elapsed time |
| costs rise faster than data | cross-partition queries plus a container that split | compare RU/s per request before and after the split |
| throttled at 900 RU/s on a 10,000 RU/s container | one hot logical partition against its share | Azure Monitor: *Normalized RU Consumption* by partition key range |
| adding throughput did not stop the throttling | the split halved each partition's share | count physical partition key ranges before and after |
| writes to one key start failing with 403/1014 | the logical partition reached 20 GB | `SELECT VALUE COUNT(1)` per key and multiply by document size |
| a synthetic key made documents unfindable | the bucket suffix is random, not derived from the document | check whether a reader can recompute the key from the id alone |
| the same entity appears in two partitions | a composite key part was absent and got skipped | count distinct keys with fewer separators than expected |
| a globally unique value repeated | the unique key policy is scoped per logical partition | look at the partition key of both documents |
| an `ORDER BY` works locally and fails on deploy | a multi-property sort with no matching composite index | the emulator answers it; real Cosmos returns an error |
| autoscale cost 15% of manual on an idle container | the 10% floor, times the 1.5x rate | compare billed RU/s with the autoscale maximum, not with usage |
| writes got more expensive after a "small" index change | every write pays for every index | count indexed paths before and after and multiply by write rate |

## 🧩 Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/10-cosmos-modeling/tests -p:Implementation=starter

```

The starter has fourteen numbered gaps, in dependency order: the distribution
measurement and the two rejections it feeds (GAPs 1-3); the composite key and the
bucketed key (GAPs 4-5); read amplification, the fan-out estimate and the
workload total (GAPs 6-8); throughput division, the two throttling ceilings and
the autoscale floor (GAPs 9-11); and the indexing write tax, its sign, and the
composite index requirement (GAPs 12-14). Each throws a
`NotImplementedException` naming the section of this page that derives it.

Every check is deterministic and offline: a partition key decision is arithmetic
over a distribution, and nothing here touches an emulator or Azure.

**Untouched-starter baseline: fails.** 84 of 117 checks fail, the first with:

```text
System.NotImplementedException : GAP 1: implement PartitionKeyAdvisor.Measure.
See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution.
```

The 33 that pass are the argument-guard checks and the published constants —
`ASkewCeilingOfOneOrLessIsMeaningless`, `TheLogicalPartitionLimitIsTwentyGigabytes`,
`ZeroBucketsIsNotASpread`, `AnAutoscaleRequestUnitCostsHalfAsMuchAgain` and so
on. The guards are already written, because validating an input is not the skill
this module is testing.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| skew measured against the total instead of the average | 4 failures: `AFlatKeyHasSkewOfExactlyOne`, `SkewIsMeasuredAgainstTheAverageNotTheTotal`, `SkewDoesNotImproveJustBecauseCardinalityRose`, `ASkewedKeyWithPlentyOfValuesIsStillRejected` |
| skew checked before cardinality | 1 failure: `CardinalityIsCheckedBeforeSkew` |
| fan-out multiplies the documents as well as the overhead | 2 failures: `FanOutMultipliesTheOverheadOnly` — *Expected: 17.5, Actual: 30* — and `FanOutDoesNotMultiplyTheDocuments` |
| a query returning nothing reports amplification 1.0 | 2 failures: `AQueryThatReturnsNothingStillReportsTheWorkItDid` — *Expected: 10000, Actual: 1* — and `AQueryThatExaminedNothingAndReturnedNothingIsFree` |
| throttling checks only the container total | 3 failures: `AHotPartitionIsThrottledWhileTheContainerLooksIdle`, `AddingThroughputDoesNotHelpAHotPartitionOnceItSplits`, `ExactlyAtAPartitionsShareIsNotYetThrottled` — all *Expected: True, Actual: False* |
| the autoscale 10% floor is ignored | 3 failures: `ACompletelyIdleWorkloadStillCostsFifteenPercent` — *Expected: 0.15, Actual: 0* — `TheFloorStopsAutoscaleFromEverBeingFree` — *Expected: 0.15, Actual: 0.0015* — and `BelowTheFloorTheBillStopsFalling` |
| indexing savings returned as an absolute value | 1 failure: `AddingAnIndexIsReportedAsANegativeSaving` |
| a composite index prefix accepted as a match | 1 failure: `APrefixOfACompositeIndexIsNotAMatchForALongerQuery` |
| composite index matching made case-insensitive | 1 failure: `CompositeIndexMatchingIsCaseSensitiveBecauseJsonIs` |
| a workload priced by its worst query instead of by frequency | 3 failures: `AWorkloadIsPricedByFrequency` — *Expected: 500, Actual: 5* — `AWorkloadIsTheSumOfItsPatterns`, `ARareExpensiveQueryCostsLessPerSecondThanACommonCheapOne` |

Two of these are worth a second look.

The autoscale floor is the one that nearly escaped. A *boolean* "is autoscale
cheaper?" cannot detect a missing floor at all — 10% of the peak times 1.5 is
15% of the manual bill, which is below 1.0 for every input, so the answer is
identical with and without the floor. The evaluator had to be changed to check
the **ratio** rather than the verdict. A design decision that is invisible to the
API you expose is a design decision you cannot test.

The composite index prefix is the other. Accepting `(day, celsius)` as a match
for `ORDER BY day, celsius, stationId` is a perfectly reasonable-sounding
generalisation, it passes every test that does not specifically probe it, and it
produces a query that Cosmos refuses at runtime in an environment where the
emulator did not.

## 🌍 Environments

- **🧪 Emulator.** `docker compose up -d cosmos`, then wait for
  `http://127.0.0.1:8080/ready`. The companion creates and deletes its own
  database, so a failed run leaves nothing behind. The exercise evaluator needs
  nothing running at all.
- **☁️ Azure alternative — required.** Run
  `bash infra/azure-cli/cosmos-modeling.sh` or
  `pwsh -File infra/powershell/cosmos-modeling.ps1` end to end.
  This module is about cost and the emulator does not model cost: request
  charges are a flat 1 RU, query metrics are zeros, there is one physical
  partition forever, and there is no rate limiter. Everything the module teaches
  about *money* has to be read off a real account once. Budget roughly USD 0.01
  and ten minutes; step 9 deletes the resource group.

## Review questions

1. A colleague proposes `/type` as a partition key because "every document has
   one and there are only six values". Give the two independent reasons this
   fails, and say which one cannot be repaired later.
2. A container is partitioned by `/day`. It has run happily for a year. Describe
   what changes on the day the system doubles its number of stations, and what
   does *not* change.
3. A point read returns 404 for a document you can see in Data Explorer. List
   the checks you would run, in order, and say what each one rules out.
4. A tenant-partitioned container has 10,000 tenants; one holds 40% of the data.
   Its cardinality is excellent and its skew is 4,000. Explain why the second
   number is the one that matters, and describe two repairs with their costs.
5. You bucket `/day` into 16 buckets using a hash of the document id. State
   precisely what a query for "everything on 2026-08-07" now has to do, and what
   it costs relative to before.
6. A container is provisioned at 10,000 RU/s over ten physical partitions and is
   being throttled while Azure Monitor reports 900 RU/s consumed. Explain the
   apparent contradiction, and say why raising the provisioning to 20,000 might
   not help.
7. A workload has one report costing 500 RU that runs hourly and one read
   costing 3 RU that runs 200 times a second. Which do you optimise, and what is
   the arithmetic?
8. A team excludes 36 of 40 indexed paths on a container taking 1,000 writes a
   second. Compute the saving in RU/s, then name the query that this change will
   break and how you would find it before deploying.
9. Autoscale is proposed for a container that is idle 23 hours a day and peaks
   at 20,000 RU/s for one. Compute the relative cost against manual provisioning
   and state the assumption that makes your answer wrong.
10. Module 7's Table Storage also had a partition key. State the two things
    Cosmos DB's partition key does that Table Storage's does not, and the one
    thing Table Storage's row key gives you that Cosmos DB's id does not.

## 🧭 What you can now assume

You can now choose a partition key on evidence rather than intuition, project it
against the ceiling that ends its life, manufacture one when the document does
not contain a good one, and price a workload in request units before writing a
line of application code.

What you cannot yet do is *talk* to the container safely: read a page at a time
without holding a result set in memory, update a document without overwriting a
concurrent change, or survive the 429 that this module has told you to expect.
Module 11 is those mechanics.
