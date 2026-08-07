# 11. Query and update with C#

Module 10 chose a partition key and priced a workload. Nothing was written by an
application; the container existed and the bill was arithmetic. This module is
the other half: the code that actually talks to the container, and the four
mechanics that decide whether it is correct under load.

There is one idea underneath all of them:

> **Every data-plane call is a network call to a system that is allowed to say
> "not all of it", "not yet", or "not any more". Correct code is the code that
> has an answer for each.**

"Not all of it" is pagination. "Not yet" is throttling. "Not any more" is a
stale ETag. And because a network call can also simply *fail to answer*, the
fourth mechanic is deciding which operations may safely be sent twice.

## Objectives

By the end of this module you can:

- explain when a point read is the right call and when a query is, and state the
  cost and the failure mode of using the wrong one;
- write a query drain loop that is correct against a service that cuts pages
  short whenever it likes, and say why a short page is not the end of a result
  set;
- use an ETag to detect a concurrent write, and bound the retry loop that
  follows so it terminates under contention;
- decide when `PatchItemAsync` is safer than a read-modify-write, and when it is
  not enough;
- assemble a transactional batch that Cosmos will accept, and read a failed
  batch for the operation that actually failed rather than the first one that
  reported an error;
- design a retry policy that honours `x-ms-retry-after-ms`, backs off
  exponentially when the service says nothing, and stops before the caller's
  deadline;
- classify an interrupted write as safe or unsafe to retry, and rewrite the
  unsafe ones into operations that survive being sent twice;
- pick a deletion mechanism on cost rather than on habit.

## The question this module answers

A field station uploads readings. The application reads one, updates it while
another process is updating the same one, pages through a day's worth, commits
two documents that must both land or neither, and is told by the service to slow
down — all in the space of one request handler.

Every one of those has a shape that looks right, passes local tests, and loses
data in production. This module is the shapes that do not.

## A point read is not a query

Cosmos gives you two ways to fetch a document by id, and they are not variations
on a theme.

```csharp
// Address.
var direct = await container.ReadItemAsync<Reading>(id, new PartitionKey(station));

// Question.
var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", id);
```

`ReadItemAsync` takes the two halves of the primary key — the partition key and
the id — and goes straight to the replica that owns them. There is no query
engine, no plan, no index lookup, no cross-partition coordination. It is the
cheapest operation Cosmos offers and it is the unit the entire RU scale is
calibrated against: **a 1 KB point read costs exactly 1 RU.**

The query returns the same JSON by asking a question. Even scoped to one
partition it compiles a plan, consults the index, materialises a result set and
returns a *page*. On a real account it costs at least twice the point read, and
the gap widens with the size of the partition.

So the rule is: **if you know the id and the partition key, never write a query.**

Two consequences that are easy to miss.

**The parameter is not optional.** `WithParameter` is not stylistic. Building
the SQL by concatenation is an injection defect, and — because Cosmos caches
query plans keyed on the query text — it also compiles a fresh plan for every
distinct id you interpolate, which is a cost you pay forever for a query you
should not have written.

**Ten point reads are not a query either.** When you need several documents by
id, `ReadManyItemsAsync` takes a list of `(id, partitionKey)` pairs and stays a
set of point reads while paying the network cost once. `WHERE c.id IN (...)` is
a query and is priced as one.

### Both halves of the address, or nothing

`(partition key, id)` is the primary key. Supply the wrong partition key and the
document is not "found in the wrong place" — it is **not found**:

```text
404 NotFound
```

Not a 400, not a warning, not a slow answer. This is the single most common
Cosmos support question, and it is a direct consequence of module 10's model: a
point read is a lookup in one logical partition, and a document that is not in
that partition does not exist as far as the read is concerned.

## A page is not the whole answer

A query does not return a result set. It returns a page, and a token.

```csharp
string? continuation = null;

do
{
    using var iterator = container.GetItemQueryIterator<Reading>(query, continuation, options);
    var page = await iterator.ReadNextAsync();

    Handle(page);
    continuation = page.ContinuationToken;
}
while (continuation is not null);
```

Note the shape: a **`do`/`while`**, not a `while`. The first request has no
token, so a `while (continuation is not null)` loop never executes at all and
the query silently returns nothing — a bug that survives review because the loop
looks exactly like the correct one.

`MaxItemCount` is a *maximum*, not a request. Cosmos ends a page early whenever
it wants to, and it has several reasons to:

- the response reached **4 MB**;
- the execution reached its **five-second** budget;
- the query crossed a **physical partition** boundary;
- the service is under pressure and chose to.

### The token is the only end signal

Which produces the rule that this module exists to install:

> **A short page is not the end of the results. An empty page is not the end of
> the results. A null continuation token is the end of the results.**

`page.Items.Count < requestedSize` is the tempting test and it is wrong in both
directions: it stops early on a service-truncated page, losing data, and it
would keep going forever on a service that returned exactly the requested count
on the final page.

The token itself is opaque, it belongs to one query against one container, and
it is not small — a few hundred characters is normal, because it encodes the
per-partition progress of a possibly-parallel execution. You may hand it back to
a client so the next request resumes, and you should treat it as a bearer token
for that query: it says where you got to, and anyone holding it can continue.

A drain loop also needs a **bound**. A service that keeps handing back tokens —
because the data is growing as fast as you read it, or because of a bug — will
otherwise spin a request handler forever. `PageReader.Drain` in the exercise
takes a `maximumPages` and throws when it is reached with a token outstanding,
which turns an invisible hang into a diagnosable exception.

## An ETag is a version you can argue with

Every Cosmos document carries an `_etag`, and it changes on every write. Two
processes read the same document, both compute an update, and both write (the
ETag values are minted per write, so yours will differ):

```text
   Both readers hold ETag    : 473d978d-22cd-4a6b-8be3-ac5d4fe38425
   Writer 1 replaced         : celsius -> -3.5, new ETag 3765d7ff-278c-486f-b54c-fb8d91aa7e6d
   Writer 2 replaced blind   : celsius is now 99.9
   Writer 1's change is gone, and nothing anywhere reported an error.
```

That is a lost update, and the important word in the last line is *nothing*.
There was no error, no conflict, no log entry. The system did exactly what it
was asked. The defect is entirely in the asking.

Attach the ETag and the second write is refused:

```csharp
await container.ReplaceItemAsync(
    updated, id, key, new ItemRequestOptions { IfMatchEtag = etag });
```

```text
   Conditional write         : 412 PreconditionFailed
```

**412 is not a failure. It is information.** It says: the document you reasoned
about is not the document you are writing to. The only correct response is to
read the current version, re-apply your *intent* to it, and try again.

The word "intent" is carrying weight. This is right:

```csharp
var current = store.Read(id);              // inside the loop
var proposed = current with { Corrections = current.Corrections + 1 };
```

and this is the defect the whole mechanism exists to prevent:

```csharp
var current = store.Read(id);              // outside the loop
for (...)
{
    var proposed = current with { Corrections = current.Corrections + 1 };
    // every attempt carries the same stale ETag, so every attempt is a 412
}
```

The second version burns its entire retry budget achieving nothing. There is a
worse version still: reacting to the 412 by dropping the `IfMatchEtag` and
writing unconditionally. That "fixes" the symptom by reintroducing exactly the
lost update the ETag was added to prevent.

### The loop needs a bound

An unbounded retry-on-412 loop is not a solution, it is a livelock. Under
sustained contention the loop never terminates, and — because each iteration
costs a read and a write — it is a livelock that bills. The exercise's
`ConcurrencyGuard.Apply` takes a `maximumAttempts` and reports `Exhausted` when
it runs out, which pushes the decision about what to do next up to the caller
where it belongs.

### Patch, and the one thing it is genuinely better at

`PatchItemAsync` sends operations instead of documents:

```csharp
await container.PatchItemAsync<Reading>(id, key,
[
    PatchOperation.Set("/status", "verified"),
    PatchOperation.Increment("/corrections", 1),
]);
```

```text
   Status                    : verified
   Corrections               : 1
   Celsius (untouched)       : -17.25
```

The obvious benefit is bandwidth: twenty untouched fields are not read and not
resent. The real benefit is `Increment`. A read-modify-write on a counter is a
lost update waiting for a race; an `Increment` is applied by the *server* to
whatever value is there, so two concurrent increments produce two.

Patch is still not a merge. It is an ordered list of operations against known
paths, it fails if a path's parent does not exist, and it accepts an
`IfMatchEtag` exactly like a replace — which you want whenever the operations
depend on a value you read.

## A batch is one partition, or nothing

`TransactionalBatch` commits several operations atomically. There is one
constraint and it is absolute:

> **A transactional batch is scoped to a single logical partition.**

That is not a limit the API chose to impose; it is the direct consequence of the
architecture in module 10. Atomicity requires one replica set, and there is one
replica set per physical partition. Two documents that must change together must
share a partition key — a decision made when the container was created, long
before this code was written.

Cosmos will also refuse a batch of more than **100 operations** or more than
**2 MB**. So splitting a pile of writes into batches means grouping by partition
key *first* and chunking *second*. Chunking first and hoping each chunk happens
to be single-partition produces a 400 at runtime, for exactly the inputs your
tests did not contain.

### Reading a failed batch

Here is what a failed batch actually looks like:

```text
   Two creates               : 200 OK, 1.00 RU
   One create, one collision : 409 Conflict
     operation 0             : 424 FailedDependency
     operation 1             : 409 Conflict
   Documents from both batches: 2
```

Two documents from the first batch, none from the second. Nothing partial.

Now look at operation 0. It reports **424 Failed Dependency**, which means
"this operation was fine; the batch was not". If you diagnose a failed batch by
reporting the first non-success status, you will send a reader to debug a create
that would have worked, and the actual 409 — a duplicate id, at position 1 —
goes unmentioned.

> **424 is never the answer.** The culprit is the first status that is neither a
> success nor a 424, and on a real account it is frequently the last operation,
> because that is where the conflict was found.

## Retry is a budget, not a loop

The emulator has no rate limiter, so this entire section is invisible locally:

```text
   THROTTLING. There is no rate limiter. Eight hundred concurrent
   writes against a container provisioned for 400 RU/s all succeed, so
   429, x-ms-retry-after-ms, and the SDK's retry policy never engage.
   A load test against the emulator measures your machine, not Cosmos.
```

On a real account, spending more RU/s in a second than the partition is
provisioned for gets you **429 Too Many Requests**. It is flow control, not an
error: the work was not done, the service says so, and it tells you when to come
back.

The client's own backoff — used only when the service says nothing — has two
requirements that pull in opposite directions.

**It must be exponential.** Linear backoff does not shed load fast enough for a
throttled partition to recover: a hundred clients retrying every 100 ms are
still the storm that caused the 429.

**It must be capped.** Uncapped doubling means attempt 20 waits a day, and — a
detail the exercise's evaluator caught — attempt 60 overflows `TimeSpan`
outright. Compute in milliseconds, compare against the ceiling, and construct
the `TimeSpan` afterwards.

### The server knows how long to wait

When the response carries `x-ms-retry-after-ms`, **that number wins**. Not "is
considered". The service knows when the partition's budget will have
replenished; the client's curve is a guess made with no information.

Two symmetric mistakes:

- **Capping the server's value** at your own ceiling. It looks prudent. It
  produces a retry that arrives before the throttle lifts, and is throttled
  again.
- **Ignoring a server value smaller than your curve.** That is throughput thrown
  away for no reason.

### Stopping is part of the policy

The caller has a deadline — an HTTP request to answer, a queue lock to renew. A
retry schedule that outlives the deadline has converted a fast failure the
caller could have handled into a slow one it cannot.

So the check goes **before** the wait:

```csharp
if (total + step.Delay > budget)
{
    return new RetryPlan(steps, Exhausted: true, total);   // do not sleep past the deadline
}
```

Adding the wait and *then* noticing the breach is a policy that is always
exactly one wait too late.

Retrying is also only worthwhile for statuses a later attempt could change: 412,
429, 503, 408. A 404, a 409 or a 400 will return precisely the same answer no
matter how many times it is asked, and retrying them converts a fast, clear
failure into a slow, identical one.

The SDK does some of this for you. `MaxRetryAttemptsOnRateLimitedRequests`
defaults to **9** and `MaxRetryWaitTimeOnRateLimitedRequests` to **30 seconds**,
which is why a throttled application usually presents as *latency* rather than
as errors. Those bounds belong in `CosmosClientOptions`, set deliberately
against your caller's deadline.

## Retrying safely means retrying idempotently

A 429 and a cancellation look similar from the call site and are opposites.

A **429 is an answer**: the service says, in so many words, that it did not do
the work. There is nothing to undo, and any operation may be sent again.

A **cancellation or timeout is not an answer**. The write may have been
committed and only the response lost. The companion shows the easy case — a
token cancelled before the request left:

```text
   Cancelled create          : OperationCanceledException
   Documents with that id    : 0
```

Cancel it a millisecond later and that count is genuinely unknowable from the
client. So safety depends entirely on whether a second application would have a
second effect:

| operation | safe to retry after a cancellation? | why |
| --- | --- | --- |
| upsert with a deterministic id | yes | the second write reaches the same state |
| patch `Set` | yes | setting a fixed value twice is setting it once |
| delete | yes | tolerate the 404 and it is idempotent |
| conditional replace | yes | the ETag makes the second attempt a clean 412 |
| create | **no** | 409 at best; a duplicate under a fresh id at worst |
| patch `Increment` | **no** | a second increment is a wrong number |
| unconditional replace | **no** | it overwrites whatever happened in between |

Every unsafe operation has a safe sibling expressing the same intent. "Create
this reading" becomes "upsert it under an id I can compute again". "Add one"
becomes "read, add one, write it back if nobody else did".

Which makes the id the load-bearing part:

```csharp
public static string DeterministicId(string source, long sequence) =>
    $"{source.Trim().ToLowerInvariant()}-{sequence:0000000000}";
```

A `Guid.NewGuid()` or a timestamp makes the retry a *different document*, which
is precisely how duplicates are born: the first attempt committed, the response
was lost, the retry created a second copy under a second id, and nothing will
ever reconcile them.

The zero padding is not cosmetic. Ids are strings and sort as strings, so
without it `station-1-10` sorts before `station-1-9`, and range queries on id
quietly return the wrong window.

## Deleting is a write

Cosmos has no `DELETE FROM c WHERE ...`. A bulk delete is a query followed by
one `DeleteItemAsync` per document, every one of them charged at roughly the
cost of a write. Three mechanisms, in increasing order of what they cost you and
decreasing order of control:

| mechanism | cost | when |
| --- | --- | --- |
| delete the container | free, instant, total | everything is going and nothing else lives there |
| time-to-live | free to the application; the service uses leftover RU/s | the documents become worthless at a known age |
| `DeleteItemAsync` per document | charged like a write, per document | an arbitrary subset with no predictable expiry |

The first is worth designing for. "One container per concern" is an
*operational* decision as much as a modelling one, because it is what makes
"delete all of it" a free control-plane call instead of a hundred thousand
charged data-plane calls.

TTL is worth setting at creation even when you do not need it yet: `-1` enables
the mechanism without expiring anything, and individual documents can then carry
their own `/ttl`. Turning it on later is easy; realising you needed it after
storing a year of logs is not.

## Run the companion

```bash
docker compose up -d cosmos
curl -sf http://127.0.0.1:8080/ready && echo ready

dotnet run --project lessons/11-cosmos-development/DataPlane
```

The companion creates its own database, exercises each mechanic, and deletes the
database in a `finally` block, so a failed run leaves nothing behind. It obeys
`COSMOS_ENDPOINT` and `COSMOS_KEY`, which is how the management labs point it at
a real account.

Output of a clean run, with the prose commentary the companion prints between
the numbered sections elided:

```text
0. Seed: One station, one logical partition, 120 readings.
----------------------------------------------------------
   Documents written         : 120
   Total charge              : 120.00 RU

1. Two ways to fetch one document: They return the same JSON and are not the same operation.
--------------------------------------------------------------------------------------------
   ReadItemAsync             : 1.00 RU, status 200
   SELECT ... WHERE c.id     : 1.00 RU, 1 document(s)
   ETag from the point read  : d497f261-bd31-4546-8544-3820a75b5e9a

2. Paging: A result set is not a list. It is a sequence of pages and a token.
-----------------------------------------------------------------------------
   MaxItemCount requested    : 25
   Pages returned            : 1
   Documents                 : 120
   Longest continuation token: 0 characters
   Charge                    : 1.00 RU

3. Optimistic concurrency: Two writers, one document, and the difference an ETag makes.
---------------------------------------------------------------------------------------
   Both readers hold ETag    : 473d978d-22cd-4a6b-8be3-ac5d4fe38425
   Writer 1 replaced         : celsius -> -3.5, new ETag 3765d7ff-278c-486f-b54c-fb8d91aa7e6d
   Writer 2 replaced blind   : celsius is now 99.9
   Writer 1's change is gone, and nothing anywhere reported an error.

   Conditional write         : 412 PreconditionFailed
   Document still holds      : celsius 1

4. Patch: Changing one field without reading, or resending, the other twenty.
-----------------------------------------------------------------------------
   Status                    : verified
   Corrections               : 1
   Celsius (untouched)       : -17.25
   Charge                    : 1.00 RU

   Conditional patch         : 412 PreconditionFailed

5. Transactional batch: All or nothing, inside one logical partition.
---------------------------------------------------------------------
   Two creates               : 200 OK, 1.00 RU
   One create, one collision : 409 Conflict
     operation 0             : 424 FailedDependency
     operation 1             : 409 Conflict
   Documents from both batches: 2

6. ReadMany: Point reads in bulk, without turning them into a query.
--------------------------------------------------------------------
   Requested                 : 10 documents by (id, key)
   Returned                  : 10
   Charge                    : 1.00 RU

7. Cancellation: A cancelled write is not a write that did not happen.
----------------------------------------------------------------------
   Cancelled create          : OperationCanceledException
   Documents with that id    : 0

8. Cleanup: Deleting is a write, and it is charged like one.
------------------------------------------------------------
   station-05-9003 was never committed: 404 on delete
   Deleted                   : 2 documents, 2.00 RU

Deleted database expedition-journal.
```

(The commentary between the numbered sections is elided above; the run prints
it. The ETag values are generated per run and will differ, as do the elapsed
times.)

### What the emulator will not tell you

`azure-cosmos-emulator:vnext` is faithful about *behaviour* and silent about
*cost and pressure*. Measured, not assumed:

| behaviour | emulator | real account |
| --- | --- | --- |
| 404 on a point read with the wrong partition key | correct | correct |
| 412 on a stale `IfMatchEtag` | correct | correct |
| patch `Set` / `Increment` | correct | correct |
| batch atomicity, 424 on the innocent operations | correct | correct |
| `ReadManyItemsAsync` | correct | correct |
| `OperationCanceledException` on a cancelled token | correct | correct |
| **`MaxItemCount`** | **ignored** | honoured as a maximum |
| **continuation token** | **always null**, at any volume | issued whenever the page is not the last |
| **429 / `x-ms-retry-after-ms`** | **never** — 800 concurrent writes on a 400 RU/s container all succeed | issued once the budget is spent |
| **request charge** | flat **1 RU** for everything | real, and the point of module 10 |
| **time-to-live** | accepted, never acted on | documents disappear |

The two in bold that matter most are the two this module is built on. The drain
loop and the retry policy are therefore taught **offline**, in the exercise,
where a page size and a `RetryAfter` can be dictated exactly — and confirmed
**live**, at the checkpoint.

## The management labs

Equivalent scripts, same nine steps, same names:

```bash
bash infra/azure-cli/cosmos-development.sh
```

```powershell
pwsh infra/powershell/cosmos-development.ps1
```

Both create a resource group, a Cosmos account, a database and a container
provisioned at **400 RU/s** — the minimum, chosen because it is easy to exceed —
show you the exports that point the companion at the account, read
`TotalRequestUnits` and `TotalRequests` off Azure Monitor, set a 300-second TTL,
move the account between Session and Strong consistency, and delete the resource
group.

Neither ever calls `az login` or `Connect-AzAccount`: step 0 shows you the
identity and subscription and asks for confirmation before anything is created.

**This checkpoint is required.** Four of this module's mechanics have no local
behaviour at all — pagination, throttling, TTL expiry and consistency — and two
of them are the module's core. Running the companion unchanged against a real
account turns `Pages returned: 1` into `Pages returned: 5`, with a continuation
token several hundred characters long. The code does not change; its behaviour
does. Budget roughly USD 0.01 and thirty minutes.

## A bounded experiment

Ten minutes, one run, two constants changed. Both are in
`DataPlane/Program.cs`: `Readings` on **line 35** and `PageSize` on **line 38**.
Section 2 is the only part of the output you need.

**Five times the data, in pages of ten.** Set `Readings = 600` and
`PageSize = 10`.

Observed:

```text
0. Seed: One station, one logical partition, 600 readings.
----------------------------------------------------------
   Documents written         : 600
   Total charge              : 600.00 RU

2. Paging: A result set is not a list. It is a sequence of pages and a token.
-----------------------------------------------------------------------------
   MaxItemCount requested    : 10
   Pages returned            : 1
   Documents                 : 600
   Longest continuation token: 0 characters
   Charge                    : 1.00 RU

   ONE page. The emulator ignores MaxItemCount and never issues a
   continuation token, so this loop ran exactly once. A real account
   would have returned 60 pages here...
```

| | baseline | experiment |
| --- | --- | --- |
| `Readings` | 120 | 600 |
| `PageSize` | 25 | 10 |
| seed charge | 120.00 RU | 600.00 RU |
| pages a real account would return | 5 | 60 |
| **pages the emulator returned** | **1** | **1** |
| **longest continuation token** | **0 chars** | **0 chars** |
| query charge | 1.00 RU | 1.00 RU |

Three things to take from it.

**The negative result is the result.** Twelve times more work spread over
one-sixtieth the page size, and the observable behaviour is byte-for-byte
identical. The emulator's paging is not "approximate" or "different"; it is
*absent*. This is the strongest possible argument for the live checkpoint: no
amount of local testing, at any volume, will exercise the second iteration of
your drain loop.

**The seed charge scaled and the query charge did not.** 120 RU to 600 RU for
the writes — the emulator does count *requests*, one flat RU each — while a
600-document query still reports 1.00 RU. Anything that looks like a per-request
count is real locally; anything that looks like a *charge* is not.

**One paging bug the emulator *can* catch.** With exactly one page and a null
token, `while` and `do`/`while` still differ: the `while` version makes no
request at all and returns nothing, so it fails locally. The other two paging
bugs — stopping on a short page, and dropping the token — are invisible here at
any volume, because there is never a second page to get wrong.

## Common mistakes and how to diagnose them

| symptom | likely cause | how to confirm |
| --- | --- | --- |
| a query returns nothing at all | `while (token is not null)` instead of `do`/`while` — the first request is never made | log the number of `ReadNextAsync` calls; it is zero |
| a report is missing rows in production only | the loop stops on a page shorter than `MaxItemCount` | log the continuation token at the point the loop exits; it is non-null |
| a paged endpoint hangs forever | the token is not carried into the next request, so page 1 repeats | log the token sent with each request; they are all null |
| an update silently disappears | unconditional replace over a concurrent write | add `IfMatchEtag` and see whether the write becomes a 412 |
| a retry loop never terminates | the document is re-read outside the loop, so every attempt is stale | count the reads and the writes; the reads are 1 |
| 412 "fixed" and the data is worse | the ETag was dropped in the retry rather than re-read | look for a code path that writes without `IfMatchEtag` |
| a counter is off by a few under load | read-modify-write instead of `PatchOperation.Increment` | run two writers concurrently and compare the total |
| a batch fails with 400 | operations from more than one partition key | group the operations by partition key and count the groups |
| a batch failure blames the wrong operation | the diagnosis reports the first non-2xx, which is a 424 | look for the first status that is neither 2xx nor 424 |
| production latency spikes with no errors | the SDK is absorbing 429s with its 9 retries over 30 s | Azure Monitor: `TotalRequests` split by status code 429 |
| retries make throttling worse | linear backoff, or the server's `RetryAfter` ignored | log the wait used and whether it came from the header |
| a request handler times out instead of failing fast | the retry schedule outlives the caller's deadline | sum the planned waits and compare with the deadline |
| duplicate documents after a network blip | a `create` with a generated id was retried | look for two documents identical except for `id` and `_ts` |
| a delete-everything job costs more than the ingest | per-document deletes where TTL or a container drop would do | multiply the document count by ~5 RU |
| ids sort wrongly in a range query | the numeric part of the id is not zero-padded | sort a sample as strings and look for `-10` before `-9` |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/11-cosmos-development/tests -p:Implementation=starter

# The reference implementation, judged by exactly the same evaluator.
dotnet test exercises/11-cosmos-development/tests
```

Fourteen gaps across six files, every one of them offline and deterministic —
the paged source is a list, the racing store is a counter, and the retry
schedule is computed rather than slept:

| gap | file | what it decides |
| --- | --- | --- |
| 1 | `PageReader.cs` | carrying the continuation token, and clamping the page size |
| 2 | `PageReader.cs` | recognising the end of a result set from the token alone |
| 3 | `PageReader.cs` | the drain loop, its charge accumulation, and its bound |
| 4 | `ConcurrencyGuard.cs` | which statuses are worth another attempt |
| 5 | `ConcurrencyGuard.cs` | the read-modify-conditional-write loop, re-reading each time |
| 6 | `ThrottlePolicy.cs` | exponential backoff, capped without overflowing |
| 7 | `ThrottlePolicy.cs` | preferring the service's `RetryAfter` to the client's guess |
| 8 | `ThrottlePolicy.cs` | the whole schedule, and stopping before the deadline |
| 9 | `IdempotentWriter.cs` | an id a retry can reproduce, and that sorts |
| 10 | `IdempotentWriter.cs` | refusal versus silence, and what each permits |
| 11 | `IdempotentWriter.cs` | rewriting an unsafe operation into a safe sibling |
| 12 | `BatchPlanner.cs` | grouping by partition key before chunking by limits |
| 13 | `BatchPlanner.cs` | finding the culprit past the 424s |
| 14 | `CleanupPlanner.cs` | the cheapest deletion mechanism that is still correct |

The untouched starter fails **110 of 140 checks**. The first failure names its
gap and this file:

```text
System.NotImplementedException : GAP 9: implement IdempotentWriter.DeterministicId.
See lessons/11-cosmos-development/README.md#retrying-safely-means-retrying-idempotently.
```

The reference implementation passes all 140.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| page exhaustion inferred from the page size | 3 failures, including `Drain_KeepsGoingWhenTheServiceCutsPagesShortOnItsOwn` — *Expected: 20, Actual: 3* |
| drain loop written as `while` instead of `do`/`while` | 7 failures, including `Drain_TakesOnePageMoreThanTheDivision` — *Expected: 5, Actual: 0* |
| the continuation token dropped from the next request | 6 failures, including `NextRequest_CarriesTheContinuationToken` — *Expected: "token-3", Actual: null* |
| 409 Conflict treated as retryable | 2 failures: `ShouldRetry_RefusesStatusesThatWillNotChange` and `Plan_StopsWithoutWaitingOnAStatusRetryingCannotFix` |
| the document read once, outside the retry loop | 4 failures, including `Apply_AppliesTheIntentToTheFreshDocumentNotTheStaleOne` — *Expected: 2, Actual: 1* |
| backoff made linear | 4 failures, including `Backoff_Doubles` — *Expected: 00:00:00.4, Actual: 00:00:00.3* |
| the server's `RetryAfter` capped by the client ceiling | 1 failure: `WaitFor_ObeysTheServiceEvenWhenItAsksForMoreThanTheCeiling` — *Expected: 00:00:30, Actual: 00:00:05* |
| the deadline checked after the wait is added | 3 failures, including `Plan_RefusesEvenTheFirstWaitWhenTheDeadlineIsAlreadyTooClose` |
| batches chunked without grouping by partition key | 3 failures, including `Split_NeverMixesPartitionKeys` — *Expected: 3, Actual: 1* |
| 424 Failed Dependency reported as the cause | 4 failures, including `Diagnose_SkipsPastTheFailedDependencies` — *Expected: 3, Actual: 0* |
| the interruption kind ignored | 4 failures, including `Classify_DistinguishesTheTwoInterruptions` — *Expected: Safe, Actual: Unsafe* |
| time-to-live preferred over dropping the container | 2 failures, including `Plan_DeletesTheWholeContainerWhenEverythingIsGoing` — *Expected: DeleteContainer, Actual: DeletePerDocument* |

Twelve faults, twelve caught. Three are worth a second look.

**The page-size mutation is the one the emulator cannot catch.** Inferring the
end of a result set from a short page passes every possible local run against
the emulator, because the emulator returns exactly one page and no token. Only a
fake source that cuts pages the way Cosmos does — 3, then 1, then 4, each with a
token — exposes it. That is the whole argument for the offline evaluator sitting
alongside a live checkpoint rather than instead of it.

**The `RetryAfter` cap needed a test that looks wrong.** Asserting that a delay
of 30 seconds is *larger* than the policy's own `MaximumDelay` reads like a
mistake until you remember whose number it is. A policy that quietly clamps the
service's instruction is indistinguishable from a correct one on every input
where the service asks for less than five seconds.

**`Backoff` overflowed before it was capped.** The first version computed
`BaseDelay * Math.Pow(2, attempt - 1)` as a `TimeSpan` and compared afterwards,
which throws `OverflowException` at attempt 60. The evaluator's
`Backoff_StopsAtTheCeiling` case found it immediately. A cap applied after the
arithmetic is not a cap.

## Environments

- **Emulator.** `docker compose up -d cosmos`, then wait for
  `http://127.0.0.1:8080/ready`. The companion creates and deletes its own
  database, so a failed run leaves nothing behind. The exercise evaluator needs
  nothing running at all — no emulator, no container, no clock.
- **Live checkpoint: required.** Run one of the two management labs end to end,
  then run the companion with `COSMOS_ENDPOINT` and `COSMOS_KEY` pointing at the
  account it created. Pagination and throttling — two of this module's four core
  mechanics — do not exist locally in any form, and TTL and consistency are
  account-level behaviours with no local switch. Budget roughly USD 0.01 and
  thirty minutes; step 9 deletes the resource group.

## Review questions

1. A colleague replaces `ReadItemAsync(id, key)` with a query on `c.id` "so it
   works even when we don't know the partition key". Describe what this costs on
   a container with 200 physical partitions, and what it does to the query plan
   cache.
2. A paged endpoint returns the right data in test and drops roughly 4 % of rows
   in production. The loop exits when a page contains fewer items than
   `MaxItemCount`. Explain the mechanism, and say why the bug is invisible
   against the emulator.
3. You must hand a continuation token to an untrusted client so it can request
   the next page. State two properties of the token that make this workable and
   one risk it introduces.
4. Two processes increment the same counter 1,000 times each, using read-
   modify-write with no ETag. Give the range of possible final values and the
   condition that produces each end of it. Then give the two fixes and say which
   one costs a round trip.
5. An ETag retry loop is written without a bound and deployed. Under contention
   the endpoint stops responding but reports no errors and consumes RU steadily.
   Explain what is happening and what the metrics look like.
6. A batch of 40 operations across three partition keys is submitted. What
   happens, and what does the fix look like? Now the same 40 operations are all
   in one partition but total 3 MB — what happens then?
7. A failed batch reports `[424, 424, 409, 424, 424]`. Say exactly which
   operation failed, how many documents were written, and what a diagnosis that
   reports "the first non-success status" would tell you instead.
8. A service returns `x-ms-retry-after-ms: 18000` and your policy's maximum
   delay is 5 seconds. Give both possible behaviours and the consequence of
   each, then say which is correct and why.
9. A request handler has a 2-second deadline and uses the SDK defaults for
   rate-limit retries. Compute the worst case, describe what the caller
   experiences, and give the two client options you would change.
10. A write is cancelled by a timeout. For each of `CreateItemAsync`,
    `UpsertItemAsync` with a deterministic id, and
    `PatchItemAsync(Increment)`, say whether you may retry it, and what you would
    have to do first if you may not.
11. A job must delete 800,000 of a container's 1,000,000 documents, selected by a
    predicate. Price the naive approach in RU, then describe two designs that
    would have made this free, and say when each of them had to be chosen.

## What you can now assume

You can now talk to a Cosmos container the way an application does: fetch by
address rather than by question, read a result set that the service is allowed
to cut wherever it likes, update a document without erasing a concurrent change,
commit several documents atomically inside the one boundary that permits it,
back off when the service pushes back, and know which of your writes may safely
be sent twice.

Two things are still missing, and both are about *time*. You have no way to
react to a change as it happens — every read in this module was a poll — and no
way to move data out of Cosmos and into anything else. The change feed is the
answer to the first, and it is the same idea as module 9's checkpointed
processor wearing different clothes: a durable position in an ordered stream,
per partition, that you commit when you are done.
