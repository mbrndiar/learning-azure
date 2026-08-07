# ♻️ 5. Control artifact versions and deletion

> **Read** this page, **run** the companion in
> [`PreconditionArena/`](PreconditionArena/) against Azurite, **practise** in
> [`exercises/05-blob-lifecycle/`](../../exercises/05-blob-lifecycle/), then
> complete the **required live checkpoint** with the paired
> [CLI](../../infra/azure-cli/blob-lifecycle.sh) and
> [PowerShell](../../infra/powershell/blob-lifecycle.ps1) labs.
> Prerequisites: [module 4](../04-blob-storage/README.md), Docker, and — for the
> checkpoint only — an Azure subscription.

## Objectives

By the end of this module you can:

- **implement** conditional writes with ETag preconditions so two field uploads
  cannot silently overwrite one another;
- **configure** versioning, soft delete, and access-tier lifecycle rules that
  match the expedition's retention promise; and
- **diagnose** precondition-failed and conflict responses and recover from them
  deterministically instead of retrying blindly.

## The question this module answers

Module 4 stored artifacts. It assumed one writer, and it never asked what
happens when the same artifact is written twice, or deleted by mistake, or kept
for ten years. All three happen.

> **Two field laptops write the same observation file within a second of each
> other, and both get HTTP 201. Where did one of the observations go?**

The answer is the most expensive fact in this course: **Blob Storage's default
is last-write-wins, and losing a write is not an error.** No exception, no log
line, no 409. The bytes are simply gone, and the only trace is a customer asking
where their data went.

## The lost update, in three lines

```text
alice reads  "temp=-3C"
bob   reads  "temp=-3C"
alice writes "temp=-3C;wind=12kt"   -> 201
bob   writes "temp=-3C;ice=thin"    -> 201
```

Alice's wind reading no longer exists. Bob did nothing wrong: he read, he
computed, he wrote. The service did nothing wrong either — it was asked to store
bytes and it stored them.

The defect is in the *question*. "Store these bytes" cannot be answered safely.
"Store these bytes **if the artifact is still the one I read**" can.

## An ETag is a version you can bet on

Every blob carries an **ETag**: an opaque token the service changes on every
write. It is not a hash, not a timestamp, and not something you may parse. It
has exactly one useful property:

> If the ETag is the same, the bytes are the same. If it changed, somebody
> wrote.

That turns into three HTTP headers, and those three headers are the whole
mechanism:

| header | means | failure |
| --- | --- | --- |
| `If-Match: "<etag>"` | write only if the artifact is still this version | 412 `ConditionNotMet` |
| `If-None-Match: *` | write only if the artifact does not exist yet | 409 `BlobAlreadyExists` |
| `If-None-Match: "<etag>"` | read only if it changed since | 304 `NotModified` |

In the SDK they are `BlobRequestConditions`:

```csharp
await blob.UploadAsync(
    BinaryData.FromBytes(content),
    new BlobUploadOptions
    {
        Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) },
    },
    cancellationToken);
```

Delete that `Conditions` line and the code still compiles, still passes a
single-writer test, and silently reintroduces the lost update. That is why the
evaluator asserts on the **header on the wire**, not on the return value.

### One detail that only fails against a real service

`ETag.ToString()` returns the value **without** quotes. The HTTP header requires
them, and the service rejects an unquoted `If-Match`. The SDK exposes the right
form as `ToString("H")`:

```csharp
response.GetRawResponse().Headers.ETag?.ToString("H")   // "0x8DEADBEEF"  (quoted)
response.GetRawResponse().Headers.ETag?.ToString()      //  0x8DEADBEEF   (not)
```

Read with one and write with the other and every conditional write fails — but
only against a service that checks, which is why this belongs in a test that
drives the real SDK rather than a mock.

### Create-if-absent is a header, not a check

```csharp
if (!await blob.ExistsAsync()) { await blob.UploadAsync(content); }   // a race
```

Two nodes can both see "absent" and both upload. `IfNoneMatch = ETag.All` moves
the decision inside the service, where it is atomic, and exactly one caller
gets 201 while the other gets 409.

## Read-modify-write is the only safe shape

A conditional write tells you that you lost. It does not tell you what to do
next, and the answer is not "retry".

```csharp
for (var attempt = 1; attempt <= maxAttempts; attempt++)
{
    var current = await store.TryReadAsync(name, ct);      // INSIDE the loop
    var updated = change(current.Content);                 // applied to CURRENT
    if (await store.WriteIfUnchangedAsync(name, updated, current.ETag, ct)
        == PreconditionOutcome.Written)
    {
        return attempt;
    }
}
throw new ConcurrencyExhaustedException(name, maxAttempts);
```

Three properties, each of which someone removes and regrets:

- **The read is inside the loop.** Hoisting it above turns every retry into the
  same stale bet. It either fails forever, or — worse — succeeds on the attempt
  where the competing writer happens to pause, destroying their work with a
  conditional write. The evaluator observes the *sequence of ETags bet on*: they
  must all differ.
- **The change is applied to the freshly read bytes.** Re-reading but then
  re-applying to the old copy is the same lost update wearing a precondition.
- **The loop is bounded.** Unbounded retry under sustained contention is a
  livelock that presents as a hang with no error and no log. Bounded retry
  presents as an exception with a number in it, which an operator can act on.

The attempt budget is a design decision, not a magic number: five is enough to
survive real contention and small enough that a design problem — say, every node
updating one blob — surfaces as failures instead of latency.

## A 412 is an answer, not an error

The single most common bug in this area is a retry policy that treats every
failure the same. Statuses mean different things and demand different actions:

| status | meaning | action |
| --- | --- | --- |
| 412 `ConditionNotMet` | your copy is stale | re-read, re-apply, retry |
| 409 `BlobAlreadyExists` | someone created it first | re-read, re-apply, retry |
| 429, 500, 503 | the service asked you to come back | back off, retry the same request |
| 404 | it is not there | the caller decides whether that is an error |
| 400, 401, 403 | the request or the identity is wrong | abort; the answer will not change |

Two rules make this survivable:

1. **Classify on the status, never the message.** Messages are prose, are
   localized, and change without notice. `error.Message.Contains("condition")`
   ships green and breaks on a service update nobody announced.
2. **Never blindly retry a 412.** It is not transient. Retrying the identical
   bytes either fails forever or eventually wins and destroys data. The SDK
   agrees: its retry policy does not retry 412, which the evaluator verifies by
   counting attempts.

## Retention is three independent promises

"We keep expedition artifacts for seven years and can recover from mistakes" is
not one setting. It is three, and each covers a *different* loss:

| mechanism | covers | does not cover |
| --- | --- | --- |
| **soft delete** | deletes and, on flat-namespace accounts without versioning, a pre-overwrite soft-deleted snapshot | recovery after the retention window; HNS overwrite semantics differ |
| **versioning** | explicit previous versions after writes | recovery after versions are deleted or expire |
| **lifecycle rules** | cost over time, and eventual deletion | any kind of accident |

The mechanisms overlap, but they are not interchangeable. On the flat-namespace
accounts used in this course, when versioning is disabled and soft delete is
active, an overwrite creates a soft-deleted snapshot of the previous bytes.
Versioning makes previous states first-class versions instead. Accounts with a
hierarchical namespace have different overwrite behavior, so do not transfer
this recovery table to Data Lake Storage without checking that account model.

### Lifecycle rules are data the service evaluates without you

A lifecycle rule is JSON attached to the account, evaluated roughly once a day.
Nothing runs while you watch, so the plan has to be right on paper:

```json
"tierToCool":    { "daysAfterModificationGreaterThan": 30 },
"tierToArchive": { "daysAfterModificationGreaterThan": 180 },
"delete":        { "daysAfterModificationGreaterThan": 2555 }
```

Two constraints turn a plausible rule into an expensive one, and Azure enforces
neither by rejecting it — it bills instead:

| tier | minimum billed retention | what an early move costs |
| --- | --- | --- |
| Hot | none | — |
| Cool | 30 days | deleting on day 5 is billed as 30 |
| Archive | 180 days | deleting on day 30 is billed as 180, plus a rehydration delay of hours to read at all |

A rule that moves artifacts to Archive after 3 days and deletes them after 30
therefore costs *more* than leaving them Hot, and looks like a saving in the
plan. That check is exactly what `RetentionPlanner.Evaluate` performs, and why
it reports **every** violation rather than the first.

## Run the companion

```bash
docker compose up -d azurite
dotnet run --project lessons/05-blob-lifecycle/PreconditionArena
```

Every status code below came back from Azurite; nothing is asserted. The ETag
values are minted per write, so yours will differ; this is one representative
run, and what matters is that both readers hold the *same* ETag and only one
`If-Match` write survives.

```text
1. The lost update, with no error anywhere
------------------------------------------
  alice read : temp=-3C
  bob   read : temp=-3C
  alice wrote: temp=-3C;wind=12kt   -> HTTP 201
  bob   wrote: temp=-3C;ice=thin    -> HTTP 201

  stored now : temp=-3C;ice=thin
  alice's wind reading is gone. Both writes returned 201. Nothing
  failed, nothing logged, and no retry would have helped.

2. The same race, with one header
---------------------------------
  alice read ETag : "0x2354D2653FB20A0"
  bob   read ETag : "0x2354D2653FB20A0"
  identical       : True

  alice wrote with If-Match: "0x2354D2653FB20A0" -> HTTP 201
  the ETag is now          : "0x1D46FF59DD75BC0"
  bob   wrote with If-Match: "0x2354D2653FB20A0" -> HTTP 412 ConditionNotMet

  stored now : temp=-3C;wind=12kt
  bob's write was refused, not silently applied. He now knows his
  copy is stale and can re-read, re-apply, and try again.

3. Create-if-absent is a header, not a check
--------------------------------------------
  node-1 create with If-None-Match: * -> HTTP 201
  node-2 create with If-None-Match: * -> HTTP 409 BlobAlreadyExists

  stored now : claimed by node-1
  exactly one node won, decided by the service. An 'ExistsAsync then
  Upload' would have let both of them think they won.

4. What Azurite cannot decide for you
-------------------------------------
  service reports soft delete enabled : (not reported)
  service reports retention days      : (not reported)
  blob reports a version id           : (none)

  Conditional writes are identical here and in Azure: same headers,
  same 412, same semantics. Everything below is not:

    versioning        - no version id above means no version to promote
    soft delete       - undelete cannot be rehearsed here
    lifecycle rules   - the management plane the rules live in is absent
    blob index tags   - the account-wide tag index does not exist
    tier transitions  - Archive and its rehydration delay are not emulated

  A retention promise that has only been tested here has not been
  tested. That is what the required live checkpoint is for.
```

Sections 1 and 2 are the same code with one header added. That is the entire
difference between silent data loss and a detected conflict.

## Required live checkpoint

Sections 1–3 above behave identically on Azurite and in Azure. Section 4 does
not, and everything in it is load-bearing for a retention promise. **This
checkpoint is required**, and it is the second Storage checkpoint. Later
chapters return to live Azure for Event Hubs, Cosmos DB, and identity boundaries.

```bash
bash infra/azure-cli/blob-lifecycle.sh
```

```powershell
pwsh -File infra/powershell/blob-lifecycle.ps1
```

Both run the same nine steps in the same order. Both confirm your identity and
subscription *before* creating anything, and both tear the resource group down
in step 9 — which is not optional. Total cost is well under USD 0.01.

What only the live run can show you:

| step | what it proves | why the emulator cannot |
| --- | --- | --- |
| 3 | versioning, blob soft delete, and container soft delete are three separate switches | the emulator does not report them |
| 4 | an overwrite produced a **version id** and the old bytes still exist | Azurite reports no version id at all |
| 5 | `--if-match` with a stale ETag is refused with 412 by the real service | (this one Azurite does get right — compare them) |
| 6 | a deleted blob is listed with `remainingRetentionDays` and can be undeleted | there is no undelete to call |
| 7–8 | a lifecycle policy is accepted, stored, and readable back | the management plane the policy lives in does not exist |

If the script is interrupted, the teardown is one command:

```bash
az group delete --name rg-expedition-lifecycle --yes --no-wait
```

## A bounded experiment

Ten minutes, one line, one prediction.

1. In [`PreconditionArena/Program.cs`](PreconditionArena/Program.cs), section 2,
   change bob's `IfMatch` from `bobETag` to `afterAlice.Value.ETag` — the ETag
   alice's write produced. This is what "refresh the ETag and retry" looks like
   when the *data* is not refreshed with it.
2. **Predict before running:** bob is still writing bytes computed from the
   pre-alice copy. Does the service refuse him?
3. Run it. Bob gets **HTTP 201**, the run prints
   `bob wrote -> HTTP 201 (this line should be unreachable)`, and the stored
   value is `temp=-3C;ice=thin` — alice's wind reading destroyed again, with a
   conditional write in place.
4. Revert the change.

The point: the precondition protects the *version you actually read*. Betting on
a fresher ETag than the one your data came from is a lost update with extra
steps, and no header can detect it. That is the entire reason the re-read must
be inside the retry loop.

## Common mistakes and how to diagnose them

| symptom | what actually happened | how to tell |
| --- | --- | --- |
| data silently disappears under load, no errors anywhere | unconditional overwrite; last write wins | no `If-Match` on the request; every write returns 201 |
| every conditional write fails with 412 against Azure, works against a fake | the ETag was sent unquoted | compare `ToString()` with `ToString("H")` |
| a retry loop hangs forever | unbounded retry under sustained contention | attempt count grows without limit; no exception is ever thrown |
| the retry "works" but data is still lost | the read was hoisted out of the loop, so every attempt bets the same stale ETag | all attempts send an identical `If-Match` |
| two nodes both believe they created the artifact | `ExistsAsync` then `Upload` instead of `If-None-Match: *` | both got 201 for the same name |
| a 403 is retried five times | classification on exception type, or on message text, instead of status | retries on a status that can never succeed |
| an overwritten blob is not recoverable | the applicable version or soft-deleted snapshot expired, or the account uses HNS semantics | inspect namespace mode, retention settings, versions, and deleted snapshots |
| the bill rose after adding a lifecycle rule | blobs move to Cool or Archive and are deleted before the minimum retention | compare transition days with the 30/180-day minimums |
| an archived artifact cannot be read | Archive requires rehydration, which takes hours | `x-ms-access-tier: Archive` and 409 `BlobArchived` on read |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/05-blob-lifecycle/tests -p:Implementation=starter

```

The starter has ten numbered gaps, in dependency order: the conditional adapter
over a real `BlobClient` (GAPs 1–3), the read-modify-write loop and its bounded
failure (GAPs 4–5), failure classification (GAPs 6–7), and the retention plan
with its recovery answers (GAPs 8–10). Each throws a `NotImplementedException`
naming the section of this page that derives it.

**Untouched-starter baseline: fails.** 78 of 80 checks fail, the first with:

```text
System.NotImplementedException : GAP 1: implement ConditionalArtifactStore.TryReadAsync.
See lessons/05-blob-lifecycle/README.md#an-etag-is-a-version-you-can-bet-on.
```

That failure is your next action, not a repository defect. (The two passing
checks read a constant and reject a null constructor argument, both of which the
starter already provides.)

The adapter tests do not use a mock. They build a **real** `BlobContainerClient`
whose transport is a scripted handler, so the assertions are about the bytes and
headers the SDK actually produced — including the pipeline, the retry policy,
and the error classification you did not write.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| the read is hoisted out of the retry loop — every attempt still writes conditionally | 3 failures: `EveryRetryReReadsBeforeWriting`, `AContendedUpdateRetriesAndStillLands`, `TheChangeIsAppliedToTheFreshlyReadBytesNotTheStaleOnes`, all with *ConcurrencyExhaustedException : Gave up updating 'note.txt' after 5 attempts* |
| `WriteIfUnchangedAsync` drops `Conditions` — the write still succeeds and returns `Written` | 2 failures: `AConditionalWritePutsTheIfMatchHeaderOnTheWire` — *Assert.Equal() Failure: Expected ""0x1"", Actual null*; `TheEtagFromAReadIsUsableAsAnIfMatchWithoutEditing` |
| `Classify` treats 412 as `BackOffAndRetry` | 2 failures: `AConflictMeansReReadAndRetry(status: 412)` — *Expected RereadAndRetry, Actual BackOffAndRetry*; `APreconditionFailureIsNeverBackedOffAndRetried` |

The second fault is the one to notice: it produces a **successful write with the
correct bytes** and differs only in what happens when somebody else is writing
too. An evaluator that checked return values would pass it.

## Environments

- **Emulator.** `docker compose up -d azurite` for the companion. The exercise
  evaluator is pure and offline: it drives real SDK clients over a scripted
  transport and needs nothing running.
- **Live checkpoint: required.** See
  [Required live checkpoint](#required-live-checkpoint). Cost is under USD 0.01
  and the teardown is one command.

## Review questions

1. Two writers, both get 201, one observation is missing. Name the exact HTTP
   header that would have prevented it and the status code the loser would have
   received instead.
2. An ETag round-trips through your code as `0x8DEADBEEF` and every conditional
   write fails against Azure but passes against your fake. What is wrong?
3. Why must the re-read be inside the retry loop? Describe the failure mode when
   it is not — including the case where the retry *succeeds*.
4. A retry policy retries 412, 429, 500 and 503 identically. Which one is a bug,
   and what is the worst outcome it can produce?
5. A flat-namespace account has soft delete on for 30 days and versioning off.
   A script overwrites 400 artifacts with empty files. What is recoverable, in
   what form, and for how long?
6. A lifecycle rule tiers to Archive on day 3 and deletes on day 30. State the
   billed retention for each artifact and whether the rule saves money.
7. Name three things the live checkpoint proves that a green Azurite run does
   not, and say which one you would be most embarrassed to discover in
   production.

## What you can now assume

The rest of the course takes for granted that you can make a shared artifact
safe under concurrent writers, tell a stale write apart from a throttled one and
from a broken one, and state — in advance, from the settings — what is
recoverable after a mistake and for how long.
[Module 6](../06-queue-storage/README.md) moves from artifacts that are written
to work that has to be *done*, where the same duplicate-delivery problem returns
in a completely different shape.
