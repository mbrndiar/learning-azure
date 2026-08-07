# 🧭 1. Choose the right Azure data primitive

> **Read** this page, **run** the tour in
> [`PrimitiveTour/`](PrimitiveTour/), then **practise** in
> [`exercises/01-azure-data-map/`](../../exercises/01-azure-data-map/).
> Prerequisites: none beyond the [course profile](../../README.md). No Azure
> subscription, no emulator, no network.

## Objectives

By the end of this module you can:

- **choose** the Azure data primitive that fits a stated expedition requirement
  and **justify** it against the adjacent service;
- **compare** durability, ordering, partitioning, replay, and cost across blob,
  queue, table, event stream, and document storage; and
- **apply** resource-group scoping, safe naming, and teardown rules so every
  later module stays reproducible and nothing keeps billing.

## The question this module answers

Azure does not have *a* storage service. It has five that overlap enough to make
the wrong choice look reasonable for weeks, and then expensive.

You are joining the **Cloud Expedition Field Journal**. Field stations send back
photographs, hand-written observation notes, sensor readings, and processing
requests. Every one of those could be argued into any of the five services. Blob
Storage will happily hold a 300-byte JSON document. Table Storage will happily
hold a base64-encoded photograph in chunks. Cosmos DB will happily hold anything
at all. None of that means they should.

The distinguishing question is never "can it hold this?" It is:

> **What does the service do that the adjacent one cannot, and does this workload
> need that thing?**

Everything below is a way of making that question answerable without guessing.

## The record we keep coming back to

One station observation, used unchanged for the rest of the module:

```text
station    : station-bravo
observedAt : 2026-07-06T12:00:00Z
caption    : "ice shelf calving, north face"
photograph : 4,404,019 bytes (image/jpeg)
```

That is one logical record with two very different halves. The photograph is
**opaque**: 4.2 MiB nobody will ever filter on. The caption and station are
**queried**: "what did station-bravo report last week?" is the first question
anyone asks. A single record with two halves is the normal case, and it is why
"pick one service" is the wrong framing.

## Compare the primitives

Five characteristics decide every routing argument you will have. Each one is a
promise the service makes — or conspicuously does not make.

| | **Blob** | **Queue** | **Table** | **Event stream** | **Document** |
| --- | --- | --- | --- | --- | --- |
| one item is | an opaque byte range | a work message | a keyed entity | an appended event | a JSON document |
| addressed by | container + name | nothing — you get the next one | PartitionKey + RowKey | partition key, then offset | partition key + id |
| ordering | none | best-effort FIFO | sorted by RowKey in a partition | strict inside a partition | none |
| re-reading | unlimited | until deleted; reappears if not | unlimited | within the retention window | unlimited |
| cost driven by | stored bytes + operations | operations, including empty polls | stored bytes + transactions | provisioned throughput | request units |
| item ceiling | effectively unbounded | 64 KiB *encoded* | 1 MiB per entity | 1 MiB per event | 2 MiB per document |
| .NET client | `BlobContainerClient` | `QueueClient` | `TableClient` | `EventHubProducerClient` | `CosmosClient` |

Three rows in that table are doing most of the work.

**Re-reading is the sharpest line.** Reading a blob, an entity, or a document
does not consume it — you can read it a million times and it is unchanged.
Reading a queue message *hides* it, and if you never delete it, the service hands
it out again. That is not a defect; it is the entire reason a queue is safe for
work. And an event stream is the only one where several unrelated consumers can
each read the same items at their own pace, and rewind. If your workload needs
two independent readers of the same items, a queue cannot serve it *at all* — not
slowly, not expensively. It physically cannot, because the first reader's delete
removes the message the second reader needed.

**Addressing decides cost.** A table's *only* index is PartitionKey plus RowKey.
That sounds like a limitation until you notice that it is why a table costs
almost nothing: there is no secondary index to maintain, so writes are cheap. A
document store maintains an index over every property by default, which is what
makes arbitrary queries fast and what makes every single write cost request
units. If your reads always already know the key, the document store's index is
a bill for a service you never use.

**Ceilings are hard walls, not guidelines.** The queue ceiling is the one that
surprises people, because it is stated in the wrong units almost everywhere. The
limit is 64 KiB **after encoding**, and the SDK's default Base64 encoding expands
a payload by four thirds. So the largest raw payload that actually fits is 48
KiB:

```csharp
/// <summary>
/// Queue Storage accepts a message body up to 64 KiB. Applications that choose
/// Base64 must account for its expansion separately; the v12 SDK defaults to
/// no message encoding.
/// </summary>
public const long MaxQueueMessagePayloadBytes = 65_536;
```

This is the service ceiling. Module 6 deliberately chooses Base64 as an
application codec and validates its smaller raw-payload envelope separately.
The evaluator rejects implementations that confuse those two policies.

## Route a workload

Given those characteristics, routing becomes an ordered set of rules rather than
a debate. Order matters, because a workload frequently satisfies more than one
condition, and the earlier rule always describes the constraint the alternative
*cannot* satisfy at all.

| # | if the workload… | choose | over | because the runner-up… |
| --- | --- | --- | --- | --- |
| 1 | has independent consumers that re-read | **event stream** | queue | deletes the item the second reader needed |
| 2 | hands each item to exactly one worker | **queue** | event stream | has no per-item completion at all |
| 3 | filters on fields the key does not address | **document** | table | would turn those filters into a scan |
| 4 | looks up by a key it already knows | **table** | document | bills request units for an index nobody queries |
| 5 | otherwise (opaque bytes, read and written whole) | **blob** | document | has a 2 MiB ceiling and indexes bytes nobody inspects |

Rule 1 before rule 2 is the one worth internalising. A telemetry feed that also
has a single archiver looks like queue work — one worker, one item, done. But the
other consumers still need those events, and once the archiver deletes the
message they are gone. Choosing "queue" because rule 2 matched first produces a
system that works perfectly in testing with one consumer and silently loses data
the day a second one is added.

When the chosen primitive's ceiling is smaller than the item, the answer is not
"choose a different primitive" — it is the **claim check**: put the payload in a
blob and put only its name in the message or event.

```csharp
var ceiling = PrimitiveCharacteristics.For(chosen).MaxItemBytes;
var requiresClaimCheck = workload.TypicalItemBytes > ceiling;
return new PrimitiveDecision(chosen, runnerUp, factor, requiresClaimCheck, justification);
```

This illustrative fragment mirrors the contract without exposing the locked
reference implementation.

Note that the size rule reads the ceiling **out of the characteristics table**
rather than repeating a constant. That is deliberate: a routing rule that carries
its own copy of a service limit is a second authority, and second authorities
drift.

## Name it so you can delete it

The last outcome looks like housekeeping and is not. Every later module in this
course creates real Azure resources at a live checkpoint, and cloud resources
that nobody can find are cloud resources that keep billing.

Two rules make cleanup a single operation:

1. **Everything a deployment creates lives in one resource group.** Then teardown
   is `az group delete`, not a hunt through five service blades. This is why the
   group name is derived from the expedition identity rather than typed.
2. **Names are derived, and the unique part is protected.** Azure name rules are
   not uniform: a resource group tolerates hyphens and mixed case; a storage
   account name must be **3 to 24 lowercase letters and digits** and is unique
   across *all of Azure*, not just your subscription.

That second rule has a subtlety worth its own sentence. Because the storage
account name has to be truncated to 24 characters, *where* you put the uniqueness
discriminator decides whether truncation is safe:

```text
st + k3f9a7c1 + northridgesurvey + production
└─ stk3f9a7c1northridgesu ─┘   ← truncated at 24, discriminator intact ✅
```

Put the discriminator last and truncation cuts it off, so two expeditions
generate the same globally unique name and the second deployment simply fails —
with an error message about name availability that says nothing about
truncation. The evaluator asserts the discriminator survives; the
[mutation run](#how-this-evaluator-is-known-to-be-strong) shows what moving it
looks like.

Finally, every resource carries four tags — `expedition`, `environment`,
`expires-on`, `managed-by` — so a resource that escapes its group is still
attributable and still has a date on it.

## Run the companion

The tour projects the record above onto all five primitives. It is offline and
deterministic: no Azure account, no emulator, no network.

```bash
dotnet run --project lessons/01-azure-data-map/PrimitiveTour
```

Captured output (abridged to three primitives; the run prints all five):

```text
Cloud Expedition Field Journal — one record, five primitives
============================================================

The record:
  station    : station-bravo
  observedAt : 2026-07-06T12:00:00Z
  caption    : "ice shelf calving, north face"
  photograph : 4,404,019 bytes (image/jpeg)

-- Blob ----------------------------------------------------
  client       : Azure.Storage.Blobs.BlobContainerClient
  stores       : one opaque byte range with metadata
  addressed by : container + blob name (a flat namespace with '/' in the name)
  ordering     : none across blobs; last write wins per blob
  re-reading   : unlimited — reading never consumes
  cost driver  : stored GiB-months, plus per-operation and egress charges
  this record  : observations/station-bravo/2026-07-06T12:00:00Z.jpg carrying 4,404,019 bytes, with caption and station recorded as blob metadata
  boundary     : no query by station or date — listing is a prefix scan, so an index has to live elsewhere

-- Queue ---------------------------------------------------
  client       : Azure.Storage.Queues.QueueClient
  stores       : one work message up to 64 KiB encoded
  addressed by : no key — the next visible message is handed out
  ordering     : best-effort FIFO, not guaranteed
  re-reading   : until deleted; redelivery after the visibility timeout
  cost driver  : per operation, including every empty poll
  this record  : a JSON work order — {"blob":"observations/station-bravo/…jpg","station":"station-bravo"} — roughly 120 bytes, handed to exactly one processor
  boundary     : the photograph is 4,404,019 bytes and the message limit is 65,536 bytes, so the payload must stay in a blob and the message must carry only its name

-- Table ---------------------------------------------------
  client       : Azure.Data.Tables.TableClient
  stores       : one entity: partition key, row key, and flat properties
  addressed by : PartitionKey + RowKey, which is the only index
  ordering     : rows sorted by RowKey inside a partition
  re-reading   : unlimited — reading never consumes
  cost driver  : stored GiB-months, plus per-transaction charges
  this record  : PartitionKey="station-bravo", RowKey="2026-07-06T12:00:00Z", plus Caption and BlobName properties
  boundary     : an entity property tops out at 64 KiB and an entity at 1 MiB, so the photograph cannot live here either

Verdict
------------------------------------------------------------
  The photograph is opaque bytes nobody queries by content, so it belongs
  in a blob. The caption and station id are queried by station, so they
  belong in a table entity that carries the blob name. The queue carries a
  work order naming that blob, never the photograph itself.

  Chosen: Blob for the payload, Table for the index, Queue for the work.
  Rejected: EventStream (nothing replays this record) and Document (no
  query touches fields the table's keys do not already address).
```

The client type names are printed from `typeof(...).FullName` for the three
Storage primitives, so they cannot drift from the packages the course actually
references. Event Hubs and Cosmos DB are labelled as taught later, because this
module deliberately does not reference their packages.

## A bounded experiment

Ten minutes, one file, one prediction.

1. Open
   [`PrimitiveTour/PrimitiveCatalog.cs`](PrimitiveTour/PrimitiveCatalog.cs) and
   change the `Primitive.Queue` profile's `Replay` text from
   `"until deleted; redelivery after the visibility timeout"` to
   `"unlimited — reading never consumes"`.
2. **Predict before running:** which line of the Verdict is now wrong, and what
   would a system built on that belief do when a consumer crashes halfway through
   processing a work order?
3. Run the tour again and read the Queue block against the Blob block. They now
   claim identical consumption semantics, which would make the two
   interchangeable for work dispatch. They are not.
4. Revert with `git checkout -- lessons/01-azure-data-map/PrimitiveTour/PrimitiveCatalog.cs`.

The point is not the text. It is that "reading consumes" is the single property
that makes a queue safe for work and a blob useless for it, and it is invisible
in any type signature.

## Common mistakes and how to diagnose them

| symptom | what actually happened | how to tell |
| --- | --- | --- |
| `RequestBodyTooLarge` when enqueuing | the payload was under 64 KiB *before* Base64 encoding, and over it after | compare the raw length against 49,152, not 65,536 |
| a second consumer sees nothing | a queue was chosen for a workload with independent readers; the first reader deleted the messages | the workload needed rule 1, and rule 2 matched first |
| table queries get slower every week | a filter runs on a property that is not the PartitionKey or RowKey, so it is a scan whose cost grows with the table | the request charge grows with table size, not result size |
| the Cosmos DB bill is high for simple reads | request units are being paid to maintain an index over properties nobody queries | every write costs RUs proportional to indexed properties |
| `StorageAccountAlreadyTaken` on a fresh deployment | the uniqueness discriminator was truncated away, so two deployments generated the same globally unique name | the generated name is exactly 24 characters and does not contain the discriminator |
| resources still billing after cleanup | something was created outside the deployment's resource group | `az resource list --tag managed-by=learning-azure` finds strays the group delete missed |

## Practice

```bash
# Your work. Expected to FAIL at GAP 1 until you implement it.
dotnet test exercises/01-azure-data-map/tests -p:Implementation=starter

```

The starter has six numbered gaps, in dependency order: fill the characteristics
table (GAP 1), implement the routing rules (GAP 2), then naming, tags, and
teardown (GAPs 3–6). Each throws a `NotImplementedException` naming the section
of this page that derives it.

**Untouched-starter baseline: fails.** All 46 checks fail, the first with:

```text
System.NotImplementedException : GAP 1: implement PrimitiveCharacteristics.For.
See lessons/01-azure-data-map/README.md#compare-the-primitives.
```

That failure is your next action, not a repository defect.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| queue service ceiling reduced to an application codec envelope | `Queue_service_ceiling_is_exactly_64_KiB` — *Expected: 65536; the optional Base64 policy belongs in module 6.* |
| rule 1 weakened to `ConsumersAreIndependentAndReplay && !ItemIsHandedToExactlyOneWorker`, so rule 2 wins ties | `Replay_beats_single_worker_handoff_when_a_workload_looks_like_both` — *Assert.Equal() Failure: Values differ* |
| discriminator moved to the end of the storage account name | `Truncation_never_removes_the_uniqueness_discriminator` and `A_storage_account_name_is_lowercase_alphanumeric_and_within_the_azure_limit` both fail |

Each fault produced exactly one intended failure category and left the other 45
checks passing, so the evaluator localises the defect rather than collapsing.

## Environments

- **Local only.** This module creates nothing and connects to nothing.
- **No emulator needed.** Azurite is introduced in
  [module 3](../03-storage-account/README.md).
- **No live checkpoint.** The first live Azure checkpoint is in
  [module 3](../03-storage-account/README.md), where the naming and teardown
  rules you implement here are used for real.

## Review questions

1. A workload has one consumer, and each item is processed once and then
   finished. Six months later a second, unrelated consumer is added that needs
   the same items. Which rule did the original design apply, which one should it
   have applied, and what specifically breaks?
2. Why is 49,152 rather than 65,536 the number a routing rule should compare a
   payload against?
3. Table Storage is cheaper than Cosmos DB for the same data volume. Name the
   capability you give up to get that price, and describe a query that would make
   the trade a bad one.
4. Both a blob and a table entity can be read repeatedly without being consumed.
   Name two characteristics that still make them non-interchangeable.
5. A deployment's storage account name is exactly 24 characters and the second
   deployment of the same expedition fails to create one. What is the most likely
   cause, and which property of the naming scheme prevents it?
6. Why does the routing rule read the item ceiling from the characteristics table
   instead of comparing against its own constant?

## What you can now assume

The rest of the course takes for granted that you can name the primitive a
requirement calls for, defend it against the adjacent one, and scope a deployment
so it can be removed in one command. [Module 2](../02-azure-sdk-foundations/README.md)
turns to the client library all five are reached through, and to the seams that
keep it testable without a live service.
