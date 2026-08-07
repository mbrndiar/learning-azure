# 🗄️ 4. Preserve expedition artifacts

> **Read** this page, **run** the companion in
> [`ArtifactVault/`](ArtifactVault/) against Azurite, **practise** in
> [`exercises/04-blob-storage/`](../../exercises/04-blob-storage/), then drive
> the same data plane from the command line with the paired
> [CLI](../../infra/azure-cli/blob-storage.sh) and
> [PowerShell](../../infra/powershell/blob-storage.ps1) labs.
> Prerequisites: [module 3](../03-storage-account/README.md) and Docker. No
> Azure subscription is needed: everything in this module runs on the emulator.

## Objectives

By the end of this module you can:

- **implement** streaming upload and download of large expedition artifacts
  without buffering a whole payload in memory;
- **organize** artifacts with containers, virtual directories, metadata, and
  tags, and **list** them with pagination and cancellation; and
- **measure** the memory and request cost of buffered versus streamed transfers
  and choose the appropriate transfer option for a stated constraint.

## The question this module answers

Module 3 created the account. This module fills its first service with the
expedition's actual output: 4 GiB camera captures, kilobyte field notes, and
half a million frames a season, all of which someone has to find again.

> **If a blob is nothing but a name and a bag of bytes, where does structure
> come from — and what does it cost?**

The honest answer is that Blob Storage gives you almost nothing and charges you
for everything you ask for. There is one grouping level (the container), one
flat namespace of names, and one billing unit per request. Every folder,
every search, every "just list them all" is something you build out of those
three facts, and each has a price you can compute in advance.

## The namespace is flat

A container holds blobs. That is the entire hierarchy. There is no directory
object, no `mkdir`, no rename, and no move.

What looks like a path is one string:

```text
observations/station-bravo/2026/07/06/frame-0001.jpg
```

That is a **73-character blob name**. The slashes are characters in it. Deleting
`frame-0001.jpg` does not leave an empty `2026/07/06/` behind, because there
never was one. Uploading it did not create four directories, because there are
no directories to create.

Two operations make that flat namespace usable:

| operation | what the service does | what you get back |
| --- | --- | --- |
| list with `prefix` | returns every name that **starts with** the string | a flat list, subtree included |
| list with `prefix` + `delimiter` | stops at the first delimiter after the prefix and folds the rest | blobs at that level, plus prefixes |

Both are string operations on the server. Neither reads any blob content. The
second one is where "virtual directories" come from — the Azure portal's folder
tree is `delimiter: "/"` and nothing else.

### Why the name is a design decision

Because prefix listing is a string comparison, the name *is* the index. There is
exactly one query Blob Storage answers cheaply — "names starting with X" — so
the leading components of the name must be the thing you filter on most.

The expedition's dominant question is *"what did station bravo record on 6
July?"*, so the name leads with station and then date, most significant
component first:

```text
observations/{stationId}/{yyyy}/{MM}/{dd}/{fileName}
```

Two details in that layout are load-bearing, and both are worth a scar:

- **Zero-padding.** `2026/7/6` sorts after `2026/12/31` and the prefix for day
  `1` also matches days 10 through 19. `2026/07/06` does neither.
- **The trailing slash on a prefix.** `observations/station-b` matches
  `station-bravo` *and* `station-b2`. `observations/station-b/` matches only the
  station whose id is exactly `station-b`.

Neither mistake fails. They return the wrong set, quietly, forever.

### Metadata and tags are different tools

Both are key/value pairs attached to a blob, and choosing the wrong one is a
design bug that only shows up as a bill.

| | metadata | tags (blob index tags) |
| --- | --- | --- |
| read with | `GetProperties` — comes back with the blob | `GetTags` — a separate request |
| indexed | no | yes |
| searchable | no | yes, account-wide, by filter expression |
| limit | 8 KiB total | 10 tags, 768 bytes each |
| costs | nothing beyond the properties call | indexing and a separate request |

Metadata describes a blob you already know how to find. Tags find blobs. If you
are scanning a container to `GetProperties` on everything so you can filter by
a metadata value, you needed a tag — and you are paying one request per blob to
discover it.

## Streaming is a memory decision

An upload has to get bytes from somewhere to the service. Between those two
points is a choice that decides whether the process survives.

```csharp
// The version that works until the day it doesn't.
var bytes = await File.ReadAllBytesAsync(path);   // 4 GiB of managed heap
await blob.UploadAsync(new BinaryData(bytes));
```

That code is correct for a field note and fatal for a capture. Worse, it is
fatal *non-deterministically*: it survives one 4 GiB upload on an 8 GiB machine
and dies on the second concurrent one, in production, at the worst moment.

The alternative is the block blob protocol, which exists precisely so that no
participant needs the whole payload:

1. **Stage** a block: send up to 4000 MiB of bytes with an id you choose. The
   block is stored, invisible to readers.
2. Repeat for every block. Nothing is committed; the blob may not exist at all.
3. **Commit the block list**: send the ordered list of ids. *Now* the blob
   exists, atomically, with exactly those blocks in exactly that order.

The resident memory of that loop is one block, forever, whatever the payload
size:

| payload | buffered peak | streamed peak (4 MiB blocks) | ratio |
| --- | --- | --- | --- |
| 64 KiB | 64 KiB | 64 KiB | 1× |
| 256 MiB | 256 MiB | 4 MiB | 64× |
| 4 GiB | 4 GiB | 4 MiB | 1024× |

Notice the first row: for a small artifact, streaming costs nothing extra and
saves nothing either. That is why the exercise's `TransferPlanner` buffers below
256 KiB — not because buffering is better, but because a block protocol for a
64 KiB field note is complexity with no payoff.

### Block ids are a trap with a delayed fuse

Block ids are opaque Base64 strings you invent, with one rule that the
documentation states and everyone discovers the hard way:

> All block ids for one blob must decode to byte arrays of **the same length**.

Write `index.ToString()` and blocks 0–9 produce one-byte ids, block 10 produces
two. Blocks 0 through 9 stage successfully. Block 10 is rejected — not because
anything is wrong with its bytes, but because its id is a byte longer than the
ten already staged.

That is not a hypothetical. Change the companion's id format to
`index.ToString(CultureInfo.InvariantCulture)` and raise the payload past ten
blocks, and Azurite reproduces it exactly:

```text
  staged block 09:  262144 bytes  (resident buffer stays 262144 bytes)
The service rejected a request: InvalidBlobOrBlock (HTTP 400).
The specified blob or block content is invalid.
Status: 400 (The specified blob or block content is invalid.)
ErrorCode: InvalidBlobOrBlock
```

Ten successful stages, 2.5 MiB uploaded, then a rejection whose message —
"the specified blob or block **content** is invalid" — points at the payload
when the defect is in the id. The blob does not exist, because nothing was ever
committed. Pad the ordinal to a fixed width before Base64-encoding it and the
failure cannot happen.

The same defect can also surface later, as `InvalidBlockList` at commit time,
when the mismatched ids arrive in a different order. Either way, the diagnosis
is the same and neither error message says so.

### The commit is the atomic boundary

Because the blob does not exist until the commit, a crashed upload leaves no
half-written artifact for a reader to find. Uncommitted blocks are garbage
collected after a week. This is the one transactional guarantee Blob Storage
offers, and it is worth designing around: stage everything, validate, then
commit once.

## Listing is paged and lazy

`GetBlobsAsync` returns an `IAsyncEnumerable<BlobItem>`, not a `List<BlobItem>`,
and the difference is the entire point.

The service returns **at most 5000 blobs per response**, plus a continuation
token. The SDK hides the token behind the enumerator, so `await foreach` over a
million blobs silently issues 200 requests. Each one is billed, and each one is
a network round trip.

Two consequences follow, and both are the learner's responsibility:

- **`ToListAsync()` on a container is a loaded gun.** It converts a lazy,
  cancellable, bounded-memory operation into "fetch everything, hold it all,
  then decide". On a container with a million blobs it is 200 requests and a
  large heap, to answer a question that might have needed one page.
- **The page is the billing unit.** `ceil(n / pageSize)` requests, and one
  request even when the answer is empty — the service still has to be asked.

Stopping early actually stops:

```csharp
await foreach (var artifact in ArtifactCatalog.ListAsync(source, prefix, 1000, ct))
{
    if (artifact.Name.EndsWith(".json", StringComparison.Ordinal))
    {
        return artifact;   // no further page is ever requested
    }
}
```

The same is true of cancellation: the token is checked between pages, so a
cancelled enumeration stops issuing requests rather than finishing the job and
throwing at the end. An implementation that materializes pages into a list
before yielding loses both properties while still passing a naive test that only
checks the returned items — which is exactly why this module's evaluator counts
requests instead.

### One gotcha the type system will not catch

Guard clauses in an `async` iterator do not run when you call it. An async
iterator body executes nothing until the first `MoveNextAsync`, so a
`ArgumentNullException.ThrowIfNull` at the top of the iterator throws at
enumeration time, in whatever code enumerated it — or never, if nobody does.

The fix is the two-method split used in the solution: a plain method that
validates and *returns* the iterator, plus a private `async IAsyncEnumerable`
that does the work.

## ▶️ Run the companion

```bash
docker compose up -d azurite
dotnet run --project lessons/04-blob-storage/ArtifactVault
```

Every number below is measured against Azurite, not asserted. The container is
deleted on the way out.

```text
1. The namespace is flat
------------------------
  uploaded 4 blobs; none of them created a directory.
  the container holds exactly these keys:
    manifest.json
    observations/station-bravo/2026/07/06/frame-0001.jpg
    observations/station-bravo/2026/07/06/frame-0002.jpg
    observations/station-delta/2026/07/06/frame-0001.jpg

  a blob name is one string; '/' has no meaning to the service
  except as an optional listing delimiter (see section 5).

2. Streaming is a memory decision
---------------------------------
  staged block 00:  262144 bytes  (resident buffer stays 262144 bytes)
  staged block 01:  262144 bytes  (resident buffer stays 262144 bytes)
  staged block 02:  262144 bytes  (resident buffer stays 262144 bytes)
  staged block 03:  262144 bytes  (resident buffer stays 262144 bytes)
  staged block 04:  262144 bytes  (resident buffer stays 262144 bytes)
  staged block 05:    4096 bytes  (resident buffer stays 262144 bytes)

  payload size        : 1314816 bytes
  committed length    : 1314816 bytes
  committed blocks    : 6
  uncommitted blocks  : 0
  peak buffer         : 262144 bytes, whatever the payload size

  before the commit the blob does not exist at all: staged blocks are
  invisible to readers, which is why a failed upload leaves no torso.

3. Metadata and tags are different tools
----------------------------------------
  metadata (returned with GetProperties, never indexed):
    capturedUtc  = 2026-07-06T04:12:55Z
    station      = station-bravo
  tags (a separate call, and the only one the service can index):
    retention    = cold
    station      = station-bravo

  metadata costs nothing extra to read with the blob and cannot be
  searched; tags can be searched across a whole account and cost a
  separate request to read. Choosing wrongly is a design bug, not a bug.

4. Listing is paged and lazy
----------------------------
  page 1: 5 blobs, continuation = present
  page 2: 5 blobs, continuation = present
  page 3: 2 blobs, continuation = (none)
  3 service calls for 12 blobs at a page size of 5.
  stopping after the first page costs 1 call, not 3.
  that is the whole reason the API is an IAsyncEnumerable and not a List.

5. Virtual directories are a listing feature
--------------------------------------------
  GetBlobsByHierarchy(prefix: "observations/", delimiter: "/"):
    [prefix] observations/station-bravo/
    [prefix] observations/station-delta/

  the same blobs, listed flat with the same prefix:
    observations/station-bravo/2026/07/06/frame-0001.jpg
    observations/station-bravo/2026/07/06/frame-0002.jpg
    observations/station-delta/2026/07/06/frame-0001.jpg

  same data, two views. Nothing was moved, created, or renamed:
  the delimiter only tells the service where to stop and fold.
```

Section 2 is the one to sit with. `committed blocks: 6`, `uncommitted blocks: 0`,
and a resident buffer of 256 KiB for a 1.25 MiB payload — the same 256 KiB it
would be for 4 GiB.

## Drive the same data plane from a shell

The two labs perform the same ten steps in the same order, against Azurite, at
zero cost:

```bash
docker compose up -d azurite
bash infra/azure-cli/blob-storage.sh
```

```powershell
docker compose up -d azurite
pwsh -File infra/powershell/blob-storage.ps1
```

Read them side by side. Both create a container, upload four blobs whose names
only look like paths, set metadata, set tags, list by prefix, fold with a
delimiter, page explicitly, round-trip a download, and delete the container.
Both print the endpoint they are about to write to before writing anything —
if it does not say `127.0.0.1`, stop.

Both scripts also document the one-line change that points them at a real
account through Entra ID instead of the emulator's well-known key. That key is
in the source deliberately: it is an emulator credential that grants access to
nothing outside this machine. A real account key never gets that treatment.

## 🔬 A bounded experiment

Ten minutes, two edits, one prediction.

1. In [`ArtifactVault/Program.cs`](ArtifactVault/Program.cs), change the payload
   to `new byte[(BlockSize * 12) + 4096]` so the upload needs more than ten
   blocks.
2. Change the block id to a variable-width ordinal:
   `Encoding.ASCII.GetBytes(index.ToString(CultureInfo.InvariantCulture))`.
3. **Predict before running:** the payload now needs 13 blocks. Which call
   fails first — the stage of block 10, or the commit?
4. Run it. Blocks 00–09 stage. The stage of block **10** fails with
   `InvalidBlobOrBlock`, HTTP 400, and the run never reaches the commit.
5. Revert both edits.

The point: 2.5 MiB was uploaded to produce a blob that does not exist, and the
error message blames the *content*. A block-id defect is invisible until the
tenth block, which on a 4 MiB block size means invisible until the payload
passes 40 MiB — comfortably past every test fixture anyone writes.

## ⚠️ Common mistakes and how to diagnose them

| symptom | what actually happened | how to tell |
| --- | --- | --- |
| `OutOfMemoryException` under load, never in testing | the upload path buffers, and testing never ran two large uploads at once | peak memory tracks payload size; look for `ReadAllBytes`, `ToArray`, or a `MemoryStream` copy |
| `InvalidBlobOrBlock` on the eleventh block of a long upload | block ids decode to different lengths | ids are built from an unpadded ordinal; blocks 0–9 are one byte, block 10 is two |
| a listing returns blobs from another station | a prefix without a trailing slash matched a longer id | the prefix ends in a name component rather than `/` |
| listing "misses" days 2 through 9 | unpadded date components; the day-1 prefix matched 10–19 instead | the prefix contains `/7/` rather than `/07/` |
| a listing costs far more than expected | `ToListAsync()`, or a page size of 1 | request count is `ceil(n / pageSize)`; compare it with what you observe |
| cancelling a long listing does nothing | pages are materialized before yielding, so the token is never checked between calls | requests continue after the token is cancelled |
| `ArgumentNullException` thrown from an unrelated `await foreach` | a guard clause lives inside an async iterator and ran at enumeration time | the stack trace points at the consumer, not the caller |
| a metadata-based search scans the container | metadata is not indexed; only tags are | one `GetProperties` request per blob in the logs |
| deleting "the folder" deletes nothing | there is no folder; there are only names sharing a prefix | delete is per-blob, or per-container |

## 🧩 Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/04-blob-storage/tests -p:Implementation=starter

```

The starter has ten numbered gaps, in dependency order: the naming scheme and
its prefixes (GAPs 1–4), block ids and the streaming upload loop (GAPs 5–6),
lazy paged listing and its request cost (GAPs 7–8), and the transfer decision
with its memory cost (GAPs 9–10). Each throws a `NotImplementedException` naming
the section of this page that derives it.

**Untouched-starter baseline: fails.** All 73 checks fail, the first with:

```text
System.NotImplementedException : GAP 6: implement BlockStreamingUploader.UploadAsync.
See lessons/04-blob-storage/README.md#streaming-is-a-memory-decision.
```

That failure is your next action, not a repository defect.

The evaluator does not ask whether your upload produced the right bytes — that
would pass for a buffering implementation too. It wraps the source in a stream
that records how much has been consumed, and snapshots that counter at every
stage call. "Did you stream or did you buffer?" becomes an assertion about the
first block: it must be staged when exactly `blockSize` bytes have been read,
not when all of them have.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| `UploadAsync` copies the source into a `MemoryStream` before staging — same blocks, same bytes, same commit | 3 failures: `TheFirstBlockIsStagedBeforeTheSourceIsFullyRead` — *Assert.Equal() Failure: Expected 1024, Actual 8192*; `EachStageOnlyEverReadsOneMoreBlock`; `NoReadEverAsksForMoreThanOneBlock` |
| `ListAsync` collects every page into a `List` before yielding — same items, same order | 3 failures: `TakingTheFirstPageCostsExactlyOneCall` — *Assert.Equal() Failure: Expected 1, Actual 1000*; `StoppingEarlyStopsFetching`; `CancellingMidEnumerationStopsFurtherCalls` |
| `DayPrefix` drops the trailing slash | 1 failure: `TheDayPrefixEndsWithASlash` — *Assert.EndsWith() Failure: String end does not match* |

The first two faults are the ones that matter: both produce **byte-for-byte
correct results** and differ only in cost. An evaluator that checked outputs
would pass them both. Each fault also left the other checks passing, so the
evaluator localises the defect rather than collapsing.

## 🌍 Environments

- **🧪 Emulator.** `docker compose up -d azurite` for the companion and for both
  shell labs. The exercise evaluator is pure and needs nothing running.
- **☁️ Azure alternative — optional.** Create the
  [live Storage sandbox](../../infra/README.md#create-a-live-storage-sandbox),
  then run `bash infra/azure-cli/blob-storage.sh` or
  `pwsh -File infra/powershell/blob-storage.ps1 -StorageAccountName
  $env:AZURE_STORAGE_ACCOUNT`. Everything here behaves identically on Azurite;
  Blob index tags, versioning, and lifecycle rules do not, which is why
  [module 5](../05-blob-lifecycle/README.md) requires Azure and this one does not.

## Review questions

1. A container holds `observations/station-bravo/2026/07/06/frame-0001.jpg`.
   How many objects does the service store, and how many directories?
2. Why does listing with the prefix `observations/station-b` return artifacts
   from `station-bravo`? Give the one-character fix and explain it from first
   principles.
3. An upload works in every test and fails in production on the eleventh block
   with `InvalidBlobOrBlock`. What is wrong, why did no test catch it, and why
   is the error message misleading?
4. Your service uploads 30 KiB field notes and 4 GiB captures through the same
   code path. State the rule you would use to choose the transfer mode, and the
   peak memory each branch costs.
5. Two implementations of `ListAsync` return identical items in identical order.
   One is lazy and one materializes. Name two observable differences and the
   measurement that distinguishes them.
6. You need to find every artifact in the account marked `retention=cold`.
   Metadata or tags? What does the wrong choice cost, in requests?

## 🧭 What you can now assume

The rest of the course takes for granted that you can design a blob name that
makes your dominant query a prefix scan, upload an artifact of any size in
bounded memory, attach the right kind of key/value pair to it, and enumerate a
container without paying for pages you never read.
[Module 5](../05-blob-lifecycle/README.md) takes the artifacts you can now store
and asks the harder question: what happens when two writers reach the same one
at the same time, and what happens when someone deletes it by mistake.
