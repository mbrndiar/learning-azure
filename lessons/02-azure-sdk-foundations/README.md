# 🧰 2. Build a testable C# Azure client

> **Read** this page, **run** the tour in
> [`ClientSeams/`](ClientSeams/), then **practise** in
> [`exercises/02-azure-sdk-foundations/`](../../exercises/02-azure-sdk-foundations/).
> Prerequisites: [module 1](../01-azure-data-map/README.md). No Azure
> subscription, no emulator, no network.

## Objectives

By the end of this module you can:

- **build** an Azure SDK client behind an application-owned interface so its
  behavior can be verified without a live service;
- **configure** `DefaultAzureCredential` for live services and emulator
  credentials for local runs without placing secrets in source; and
- **diagnose** transient failures using the SDK retry policy, cancellation
  tokens, and client diagnostics.

## The question this module answers

Module 1 chose primitives. Every one of them is reached through the same client
library shape, and every one of them will be wired into an exercise you have to
verify offline. So before touching a single byte of storage, this module answers
one question:

> **Where are the seams in an Azure SDK client, and which of them belong to the
> application rather than to the SDK?**

A seam is a place where behavior can be replaced without editing the code under
test. Azure SDK clients expose exactly four that matter for this course:

| seam | replaced through | what it buys |
| --- | --- | --- |
| credential | `TokenCredential` passed to the constructor | run as a managed identity live, as a development account locally |
| transport | `ClientOptions.Transport` | drive the real client with no network |
| retry policy | `ClientOptions.Retry` | make a bounded, deterministic failure story |
| cancellation | `CancellationToken` on every async method | stop work the caller no longer wants |

None of these is a testing hack. All four are load-bearing in production; that
they also make the client testable is the design paying for itself.

## Ports, adapters, and the one rule

The application-owned interface — the *port* — is the fifth seam, and the only
one you write yourself:

```csharp
public interface IStationDirectory
{
    Task<StationRecord?> TryGetAsync(string stationId, CancellationToken cancellationToken);
    Task SaveAsync(StationRecord record, CancellationToken cancellationToken);
}
```

Notice what is **not** in that signature: no `BlobClient`, no `Response<T>`, no
`RequestFailedException`, no `ETag`. The rule for the rest of this course is:

> **Azure SDK types stop at the adapter.** Code above the port is verified
> against a fake; the adapter itself is verified against a scripted transport.

The temptation is to skip the port and mock `BlobClient` directly. That fails for
a concrete reason: the SDK's return types (`Response<T>`, `Pageable<T>`,
`BlobDownloadStreamingResult`) are expensive to construct and their virtual
surface changes between versions, so the mock encodes assumptions about the SDK
rather than about your application. Worse, a mocked `BlobClient` never runs the
retry policy or the response parser, so it cannot catch the bugs this module is
about.

The adapter is small on purpose:

```csharp
public sealed class BlobStationDirectory(BlobContainerClient container) : IStationDirectory
{
    public async Task<StationRecord?> TryGetAsync(string stationId, CancellationToken cancellationToken)
    {
        var blob = Container.GetBlobClient(BlobName(stationId));
        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return response.Value.Content.ToObjectFromJson<StationRecord>(SerializerOptions);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }
    // ...
}
```

Everything interesting in this module is in those seven lines and in the four
sections that follow.

## The credential seam

An Azure SDK client takes its identity from a `TokenCredential` handed to its
constructor. For live Azure the course default is one type:

```csharp
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true,
});
```

`DefaultAzureCredential` is a *chain*. It tries sources in order and uses the
first one that produces a token:

| order | source | where it is the right answer |
| --- | --- | --- |
| 1 | environment variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, …) | explicit service-principal deployments |
| 2 | workload identity | pods on AKS |
| 3 | managed identity | App Service, Container Apps, VMs, Functions |
| 4 | Azure CLI / Azure PowerShell / Azure Developer CLI | a developer's own machine |

The chain is why the *same compiled binary* runs on your laptop as you and in
Azure as a managed identity, with no configuration branch and no key. Constructing
the credential does no network work; the chain is only walked when the pipeline
first asks for a token, which is why a misconfigured identity surfaces as a
failure on the first call rather than at startup.

### The emulator is the exception, and it is bounded

Azurite has no Entra ID. It authenticates with a shared key — and that key is
published in Microsoft's own documentation, identical on every machine on earth,
protecting a service listening on `127.0.0.1`. It is not a secret in any
meaningful sense.

That does not make it a pattern. The exercise's resolver encodes the boundary as
a decision, not a habit:

| target | endpoint | authentication | secret |
| --- | --- | --- | --- |
| live Azure | `https://{account}.blob.core.windows.net/` | `DefaultAzureCredential` | none |
| Azurite | `http://127.0.0.1:10000/devstoreaccount1` | development shared key | `AZURITE_CONNECTION_STRING` |

Two rules keep the boundary from eroding:

1. **The resolver returns the variable's *name*, never its value.** A resolver
   that returns the connection string has already put a credential into every log
   line, every exception message, and every test failure that prints the resolved
   connection.
2. **A live target that finds a shared key fails.** Not "prefers Entra ID" —
   *fails*. A shared key present in a live deployment is a security defect
   somebody introduced deliberately, and silently ignoring it means it stays
   there. The exercise throws an `InvalidOperationException` naming the offending
   variable and omitting its value.

Rule 2 is the one people argue with. The argument for failing is that a
connection string bypasses RBAC entirely: it grants full account access with no
role assignment, no conditional access, no expiry, and no per-identity audit
trail. A fallback that "just works" is exactly how that becomes permanent.

## The retry seam

Storage returns `503 ServerBusy` when it throttles, and `500`/`408` for transient
faults. The SDK pipeline classifies those as retryable and retries them *below*
your code — one `await`, several HTTP requests.

```csharp
var options = new BlobClientOptions();
options.Retry.MaxRetries = maxRetries;
options.Retry.Mode = RetryMode.Exponential;
options.Retry.Delay = delay;
options.Retry.MaxDelay = delay * 8;
options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
```

Four numbers, and each one is a decision:

| setting | what happens if you get it wrong |
| --- | --- |
| `MaxRetries` | too high and a throttled dependency becomes an outage that never resolves, because every client keeps adding load; too low and a one-second blip becomes a user-visible error |
| `Mode` | `Fixed` retries at a constant rate, so a thundering herd stays a herd; `Exponential` spreads the retries apart |
| `MaxDelay` | without a ceiling, exponential growth eventually parks an operation for minutes |
| `NetworkTimeout` | a stalled connection with no timeout consumes the whole budget on one attempt that will never answer |

There is no "retry forever" configuration in this course. Unbounded retry is not
resilience; it is a load amplifier pointed at a service that is already in
trouble.

**The counting rule that trips everyone up:** `MaxRetries = 2` means *three*
transport attempts — the first try plus two retries. The tour prints the attempt
count so this is not a matter of belief:

| `MaxRetries` | responses | transport attempts | what the caller sees |
| --- | --- | --- | --- |
| 3 | 503, 503, 200 | 3 | one successful call |
| 2 | 503 forever | 3 | `RequestFailedException`, status 503 |
| 0 | 503 forever | 1 | `RequestFailedException`, status 503 |
| 3 | 404 | 1 | `RequestFailedException`, status 404 — **not retried** |

The last row is the important one. Retries only apply to failures the SDK
classifies as transient. A `404` or `403` is retried zero times, because no
amount of waiting will make a missing blob appear or a missing role assignment
grant itself.

## The cancellation seam

Every async SDK method takes a `CancellationToken`, and the exercise's port
requires one too. Two rules:

1. **Pass it down.** A token that stops at your adapter is decoration.
2. **Never catch `OperationCanceledException` to return a default.**

Rule 2 is the expensive one. Consider:

```csharp
// WRONG
try { return await blob.DownloadContentAsync(ct); }
catch { return null; }
```

When the caller cancels, that returns `null`, and `null` in this port means *the
station has no record*. A user pressing stop, or a request timing out, silently
turns into "this station has never reported". The failure is invisible: no
exception, no log, no metric — just a wrong answer that looks like data.

A cancelled token also stops the operation *before* the retry policy runs, so it
costs zero transport attempts. The tour proves it by cancelling first and then
counting requests: zero.

## The error-classification seam

This is the seam that decides whether your system is debuggable. Every failed
Storage call arrives as one type:

```csharp
catch (RequestFailedException error) when (error.Status == 404)
{
    return null;
}
```

`RequestFailedException` carries a `Status` (the HTTP status) and an `ErrorCode`
(Storage's own string, like `BlobNotFound`). The classification you have to make
is not "did it fail" but **"is this an answer or a failure?"**:

| status | `ErrorCode` | classification | correct handling |
| --- | --- | --- | --- |
| 404 | `BlobNotFound`, `ContainerNotFound` | an **answer** | return `null` from a `TryGet` |
| 403 | `AuthorizationPermissionMismatch` | configuration defect | propagate |
| 401 | `NoAuthenticationInformation` | credential defect | propagate |
| 409 | `BlobAlreadyExists`, `ContainerBeingDeleted` | contention or state | propagate; module 5 handles it deliberately |
| 412 | `ConditionNotMet` | a lost race | propagate; module 5 turns it into a retry-with-reread |
| 503 | `ServerBusy` | transient | already retried by the pipeline |

Note the `when (error.Status == 404)` filter. Catching `RequestFailedException`
without it is the single most expensive mistake in this module: a missing role
assignment (403) becomes indistinguishable from "this station has no record", so
a permissions problem presents as an empty dataset. Teams have shipped that and
discovered it weeks later, from a customer.

Filter on `Status`, not on `ErrorCode`, for the same reason `TryParse` beats
parsing an exception message: the status is a small closed set that Storage
guarantees, while error codes are added over time.

## ▶️ Run the companion

The tour drives a real `BlobContainerClient` — its real pipeline, its real retry
policy, its real response parsing — over a scripted transport. It is offline and
deterministic: no Azure account, no emulator, no network.

```bash
dotnet run --project lessons/02-azure-sdk-foundations/ClientSeams
```

Captured output:

```text
1. CREDENTIAL SEAM
============================================================
  live credential   : Azure.Identity.DefaultAzureCredential
  resolves          : environment -> workload identity -> managed identity -> Azure CLI -> ...
  secret in source  : none — the chain reads the ambient environment

  emulator          : Azurite's well-known development account
  secret in source  : none — read from the AZURITE_CONNECTION_STRING variable
  boundary          : the emulator key is public and worthless; it is NOT a pattern for live Azure

2. RETRY SEAM
============================================================
  configured retries : 3, exponential, zero delay for this tour
  transport attempts : 3
  application saw    : one call returning HTTP 200
    attempt 1: GET /stations/station-bravo.json
    attempt 2: GET /stations/station-bravo.json
    attempt 3: GET /stations/station-bravo.json

  Retries are BOUNDED. With MaxRetries = 3 the client makes at most four
  attempts and then surfaces the failure. An unbounded retry loop turns a
  throttled service into an outage that never resolves.

3. CANCELLATION SEAM
============================================================
  exception type     : System.Threading.Tasks.TaskCanceledException
  transport attempts : 0
  elapsed            : immediate — no retry budget was spent

  A cancelled token stops the operation BEFORE the retry policy runs. Code
  that catches Exception and returns a default here converts a caller's
  cancellation into a silent wrong answer.

4. ERROR-CLASSIFICATION SEAM
============================================================
  missing blob  : status 404, ErrorCode 'BlobNotFound', attempts 1
  no permission : status 403, ErrorCode 'AuthorizationPermissionMismatch', attempts 1

  Both arrive as RequestFailedException, and the Status and ErrorCode are the
  only things that distinguish them. 404 is usually an expected value — the
  station has no record yet — and belongs in a TryGet that returns null. 403
  is a configuration defect and must keep propagating.

  Notice the attempt counts: neither was retried. Storage classifies 404 and
  403 as non-retryable, so the pipeline surfaces them immediately.
```

Three details are worth pausing on. The retry seam printed **three** transport
attempts for **one** `await` — the retries are real HTTP requests the application
never sees. The cancellation seam printed `TaskCanceledException`, a subclass of
`OperationCanceledException`, which is why exercise code catches the base type.
And both error rows show `attempts 1`: neither 404 nor 403 was retried.

## 🔬 A bounded experiment

Ten minutes, one file, one prediction.

1. Open [`ClientSeams/Program.cs`](ClientSeams/Program.cs) and in
   `ShowRetrySeamAsync`, change `maxRetries: 3` to `maxRetries: 1`.
2. **Predict before running:** the script still answers 503, 503, 200. Does the
   call succeed, and how many transport attempts does it take?
3. Run the tour again. It does not print an attempt count at all — the budget of
   one retry is exhausted by the second 503, so the call throws and the tour
   dies before the reporting lines:

   ```text
   2. RETRY SEAM
   ============================================================
   Unhandled exception. Azure.RequestFailedException: The server is busy.
   Status: 503 (Service Unavailable)
   ErrorCode: ServerBusy
   ```

   The success in the original output was not luck; it was budget. One more
   retry and the scripted 200 was never reached.
4. Revert by setting it back to `maxRetries: 3`.

The point: the retry budget is the difference between an operation that survives
a throttle and one that does not, and it is a number you chose, not a default you
inherited.

## ⚠️ Common mistakes and how to diagnose them

| symptom | what actually happened | how to tell |
| --- | --- | --- |
| "no data" for one customer, correct data for others | a bare `catch (RequestFailedException)` turned a 403 into `null` | the adapter has no `when (error.Status == 404)` filter; check the identity's role assignments |
| an operation "hangs" for minutes | `MaxDelay` was left unbounded with exponential mode, so late retries park for a long time | wall-clock time is far larger than `MaxRetries × Delay` |
| a cancelled request still costs money | the token was accepted by the method but never passed to the SDK call | the transport attempt count is non-zero after cancelling |
| `AuthenticationFailedException` locally but not in CI | `DefaultAzureCredential` fell through to the Azure CLI credential, and the CLI is not logged in | the exception message lists every chain member and why each was skipped |
| a live deployment works "for now" with a connection string | shared-key auth bypassed RBAC entirely | no role assignment exists for the workload identity, yet it has full account access |
| retries never happen for a genuinely transient error | the failure surfaced as `TaskCanceledException` from `NetworkTimeout`, which is not classified as retryable in the same way | compare `Retry.NetworkTimeout` against the operation's real duration |

## 🧩 Practice

```bash
# Your work. Expected to FAIL at GAP 1 until you implement it.
dotnet test exercises/02-azure-sdk-foundations/tests -p:Implementation=starter

```

The starter has four numbered gaps, in dependency order: resolve the connection
without leaking a secret (GAP 1), configure a bounded retry budget (GAP 2),
implement the read with correct error classification (GAP 3), and implement the
write (GAP 4). Each throws a `NotImplementedException` naming the section of this
page that derives it.

**Untouched-starter baseline: fails.** 44 of 46 checks fail; the first reports:

```text
System.NotImplementedException : GAP 2: implement StorageConnectionResolver.CreateClientOptions.
See lessons/02-azure-sdk-foundations/README.md#the-retry-seam.
```

That failure is your next action, not a repository defect. (The two checks that
pass without any implementation are the two argument-validation checks on
`BlobName`, which the starter already provides.)

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| exception filter dropped, so `catch (RequestFailedException)` swallows every status | 7 failures, including `AForbiddenResponseIsNotReportedAsMissingData` and `ARealFailureKeepsPropagating` for 403/401/409 — *"Assert.Throws() Failure: No exception was thrown. Expected: typeof(Azure.RequestFailedException)"* |
| `MaxRetries` raised to `Math.Max(maxRetries, 5)`, an "always be resilient" default | 5 failures: `MaxRetriesIsWhatTheCallerAskedFor` for 0, 1, and 3, plus `TheRetryBudgetIsBounded` and `ZeroRetriesMeansExactlyOneAttempt` |
| emulator resolver returns the connection string instead of the variable name | 1 failure: `EmulatorReturnsTheVariableNameNotTheSecret` — *Expected: "AZURITE_CONNECTION_STRING", Actual: "DefaultEndpointsProtocol=http;AccountName=devstore"…* |

Each fault produced exactly one intended failure category and left the remaining
checks passing, so the evaluator localises the defect rather than collapsing.

## 🌍 Environments

- **Local only.** This module creates nothing and connects to nothing; the
  scripted transport in
  [`support/AzureFakes`](../../support/AzureFakes/ScriptedHandler.cs) answers
  every request.
- **No emulator needed.** Azurite is introduced in
  [module 3](../03-storage-account/README.md).
- **No live checkpoint.** The first live Azure checkpoint is in
  [module 3](../03-storage-account/README.md), where the credential seam you
  configured here is used against a real account for the first time.

## Review questions

1. `MaxRetries` is 2 and the service returns 503 to every request. How many HTTP
   requests leave the machine, and what does the caller finally observe?
2. An adapter catches `RequestFailedException` and returns `null`. Describe the
   production incident this causes, and name the one clause that prevents it.
3. Why does the course require `DefaultAzureCredential` for live Azure rather
   than a connection string, given that a connection string is simpler and
   works?
4. A cancelled token produced zero transport attempts in the companion run. Why
   is that stronger evidence than the exception type alone?
5. The port `IStationDirectory` mentions no Azure type. Name two concrete things
   that become possible above the adapter because of that, and one thing that
   becomes harder.
6. `404` and `503` both arrive as `RequestFailedException`. Explain why one is
   retried automatically and the other is not, and what would break if the SDK
   retried both.

## 🧭 What you can now assume

The rest of the course takes for granted that you can construct an Azure client
with an explicit credential, a bounded retry budget, and a replaceable transport;
hide it behind a port; and classify its failures into answers and defects.
[Module 3](../03-storage-account/README.md) leaves the pure-local world for the
first time and creates the account all of those clients will point at.
