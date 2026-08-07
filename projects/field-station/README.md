# Project: the Field Station

> **Applied project.** No new Azure service, no new API. Everything here was
> taught in modules 4 to 7; nothing here is scaffolded for you.
> Prerequisites: [module 5](../../lessons/05-blob-lifecycle/README.md),
> [module 6](../../lessons/06-queue-storage/README.md), and
> [module 7](../../lessons/07-table-storage/README.md), plus Docker.
> **No Azure subscription is required.** A live run is optional, opt-in, and
> billable — see [Running against real Azure](#running-against-real-azure).

The exercises so far each isolated one idea. Production does not. A field
station uploads observations, something has to process them, and something has
to report where each observation has got to — and all three parts have to stay
correct while the network retries, the worker restarts, and two writers touch
the same row.

This project is that worker, end to end, in one codebase you own.

## Objectives

By the end of this project you can:

- **build** a Blob + Queue + Table pipeline behind application-owned ports, with
  no Azure type in the domain, so the whole flow is testable without a service;
- **implement** processing that survives duplicate delivery, restart, and
  partial failure, and prove it with adversarial evidence rather than a happy
  path;
- **apply** conditional writes — `If-None-Match: *`, `If-Match`, conditional
  insert, ETag replace — as the mechanism that makes those guarantees, instead
  of read-then-write checks that look correct and race;
- **operate** the run safely: bounded retries, poison quarantine, cooperative
  cancellation, and a teardown that reports whether it actually finished; and
- **verify** everything deterministically against fakes, a scripted transport,
  and Azurite, with no live Azure dependency.

## The scenario

Ridge Camp is 200 km from the nearest town, on a satellite link that drops. Its
field laptop uploads observations whenever the link is up, retrying whatever it
is unsure about. Back at base, a worker checksums each observation and the
expedition lead wants one page showing where every observation has got to.

Three things are true at once, and every design decision follows from them:

1. **The uploader retries.** The same observation arrives more than once.
2. **The queue delivers at least once.** The same work order is handed out more
   than once, and there is no setting that turns that off.
3. **The worker restarts.** A crash can land anywhere, including between "the
   effect happened" and "the record of it was written".

## Architecture

```text
                 ┌──────────────────────────────────────────────┐
   field laptop  │              your application                │
        │        │                                              │
        │ upload │   ArtifactIntake ──► WorkDispatcher           │
        └───────►│        │                    │                │
                 │        │                    │                │
                 │   IArtifactStore       IWorkBacklog          │
                 │        │                    │                │
                 │        │              StationWorker ──► effect│
                 │        │                    │                │
                 │        │          StationStatusProjector      │
                 │        │                    │                │
                 │        │            IStationStatusIndex      │
                 └────────┼────────────────────┼────────────────┘
                          │                    │
             ┌────────────▼────────┐  ┌────────▼──────────────┐
             │  BlobArtifactStore  │  │ QueueStorageBacklog   │
             │  TableStationIndex  │  │ (work + poison queue) │
             └────────────┬────────┘  └────────┬──────────────┘
                          │                    │
                 ┌────────▼────────────────────▼─────────┐
                 │  Azure Storage account / Azurite       │
                 │  expedition-artifacts  (container)     │
                 │  artifact-work         (queue)         │
                 │  artifact-work-poison  (queue)         │
                 │  stationstatus         (table)         │
                 └────────────────────────────────────────┘
```

The three ports — `IArtifactStore`, `IWorkBacklog`, `IStationStatusIndex` —
expose no SDK type. That is not architectural decoration: it is what lets the
evaluator drive the entire pipeline in memory, and it is why the failure cases
in this project are testable at all. The adapters are the only files that know
Azure exists, and they are graded against the *real* SDK clients over a scripted
transport, so the conditional headers are asserted on the wire.

## Data flow

One observation, from the laptop to the expedition lead's report:

| # | step | what makes it safe |
| --- | --- | --- |
| 1 | the laptop uploads `obs-0001` | the blob name is derived from the key, so a retry addresses the same blob |
| 2 | `ArtifactIntake.PreserveAsync` | `If-None-Match: *` — the service decides "new or duplicate", not the caller |
| 3 | `WorkDispatcher.DispatchStoredAsync` | only a write that actually happened produces a work order |
| 4 | the work order lands on `artifact-work` | the message is a *pointer*: the observation stays in the blob |
| 5 | `StationWorker.ProcessAsync` receives it | the delivery budget is checked before any work is claimed or done |
| 6 | `StationStatusProjector.TryClaimAsync` | a conditional **insert**; losing it is how a duplicate is detected |
| 7 | the effect runs | exactly once per observation, however often the message is delivered |
| 8 | confirm, then delete | a crash between them costs one wasted receive, never a lost record |
| 9 | the summary row is incremented | re-read **inside** the retry loop, under the ETag from that read |
| 10 | `FieldStationCleanup.RemoveStationAsync` | lists by prefix, so it removes what earlier crashed runs left too |

The ordering in steps 8 and 9 is the whole project in miniature. The status row
records that the effect **completed**, not that it was attempted, so a row left
`Pending` by a crashed worker means "this may or may not have run" — and the
only safe reading of that is to run it again.

## Where the resources live

| resource | name | why it exists |
| --- | --- | --- |
| container | `expedition-artifacts` | the observations themselves, named `stations/{station}/{observation}.json` |
| queue | `artifact-work` | one work order per stored artifact |
| queue | `artifact-work-poison` | Storage queues have no dead-letter queue, so quarantine is an ordinary second queue you own |
| table | `stationstatus` | partition = station, row = observation, plus a `~summary` row per station |

`~summary` is legal in a Table row key, sorts after every observation, and is
rejected by the identifier rules, so it can never collide with a real
observation. `#`, `?`, `/`, and `\` are illegal in Table keys and legal in blob
names, which is why one strict identifier shape is validated at the boundary
instead of three lenient ones downstream.

## Set up

```bash
docker compose up -d azurite
export AZURITE_CONNECTION_STRING="UseDevelopmentStorage=true"
```

Azurite is the only dependency. Nothing in this project needs an Azure
subscription, and nothing writes to Azure unless you deliberately opt in below.

## Work the milestones

Fill the gaps in [`starter/`](starter/). Every gap throws a
`NotImplementedException` naming the section here that answers it, and the
signatures, doc comments, and reasoning are already written — the work is
deciding, not guessing at an API.

One evaluator judges both trees. Point it at the starter while you work:

```bash
dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter
```

Drop the property to run the same evaluator against
[`solution/`](solution/), the reference implementation.

Grade one milestone at a time, in order:

| # | milestone | command |
| --- | --- | --- |
| 1 | [the domain and the ports](#milestone-1-the-domain-and-the-ports) | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=domain-ports` |
| 2 | [preserving artifacts](#milestone-2-preserving-artifacts) | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=artifact-storage` |
| 3 | [dispatching work and consuming it once](#milestone-3-dispatching-work-and-consuming-it-once) | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=work-dispatch` |
| 4 | [the ledger](#milestone-4-the-ledger) | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=status-index` |
| 5 | [when things go wrong](#milestone-5-when-things-go-wrong) | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=failure-recovery` |

An untouched starter fails every one of them. That is the baseline: see
[Expected results](#expected-results).

### Milestone 1: the domain and the ports

*Gaps 1-3 — `StationNaming`, `WorkOrderCodec`.*

Identity is derived, never invented. The blob name, the work-order id, and the
status row key are all pure functions of one `ArtifactKey`, so a replayed upload
collides with itself in three places and every collision is detectable. Put a
timestamp or a GUID in any one of them and the replay silently becomes a second
observation that nothing downstream can tell apart from a real one.

Two consequences worth internalising:

- **Deduplicate on the producer-chosen id, not the message id.** The queue
  assigns a fresh message id on every enqueue, so deduplicating on it catches
  redelivery of one queue entry and nothing else — a retried *dispatch* slips
  straight through.
- **A partially valid message is still poison.** `{}` deserializes into a work
  order whose every field is null. Letting it through moves the failure to the
  first place a field is dereferenced, which is usually the ledger.

### Milestone 2: preserving artifacts

*Gaps 4, 5, 14 — `ArtifactIntake`, `BlobArtifactStore`.*

"Does it exist?" followed by "write it" is two round trips with a race between
them, and the caller most likely to be inside that window is precisely the
retrying uploader you are trying to handle. `If-None-Match: *` makes "create
only if absent" a single atomic service decision.

Amendments are the mirror image: the only safe precondition is the ETag that
came back with the bytes the amendment was computed from. `If-Match: *` is
last-write-wins with extra steps.

Three details the evaluator checks on the wire:

- the ETag from a read must be usable as an `If-Match` **without editing it** —
  `ETag.ToString()` drops the quotes and the service rejects an unquoted
  `If-Match`, so use the `"H"` (HTTP) form;
- a lost create is reported as `409` *or* `412` depending on the path the
  service took, and both mean "somebody got there first"; and
- catch by **status**, never by exception type: `catch (RequestFailedException)`
  around a read turns a missing role assignment into "there is no data".

Upload the `Stream`. Buffering it into a `byte[]` first is a memory cost
proportional to the artifact, paid on a machine sized for the metadata.

### Milestone 3: dispatching work and consuming it once

*Gaps 6, 9-12 — `WorkDispatcher`, `StationWorker`.*

Dispatch happens **after** the artifact is durable. A message pointing at a blob
nobody wrote is a guaranteed consumer failure that the consumer cannot tell
apart from a transient read error.

A duplicate upload must not produce a second work order. The consumer is
idempotent, so the extra message would not corrupt anything — it would just pay
for a receive, a claim, and a delete to discover it has nothing to do.

On the consuming side the disposition is a decision, not a status code:

| the worker saw | it does | because |
| --- | --- | --- |
| an undecodable body | quarantine on delivery **1** | retrying a deterministic failure only stops the queue draining |
| a delivery over budget | quarantine before claiming | it will fail again; every attempt is real money |
| `AlreadyProcessed` | delete, do **not** re-run | the effect is done; the message is just noise now |
| `Resumed` | run the effect | `Pending` means "may or may not have run" |
| a handler exception | retry until the budget is spent | not deleting the message *is* the retry |
| `OperationCanceledException` | let it propagate | shutdown is not a message defect |

The pop receipt proves *this* receive. That is what stops a worker whose
visibility timeout has expired from deleting a message another worker is now
holding — and why `MessageNotFound` on delete is benign rather than an alert.

### Milestone 4: the ledger

*Gaps 7, 8, 15 — `StationStatusProjector`, `TableStationIndex`.*

The status table is not a report; it is the pipeline's ledger. The claim is a
conditional **insert**, and its *failure* is the answer: "read, then insert if
absent" is the same race intake avoided one milestone ago, except now two
workers both read "absent" and both apply the effect.

The summary row is the contended value. Getting it right requires one thing that
is easy to state and easy to get subtly wrong:

> The re-read belongs **inside** the retry loop.

A retry that resends the same stale ETag fails identically forever. A retry that
resends a *fresh* ETag carrying the value computed from the **stale** read
silently reintroduces the lost update the ETag existed to prevent. Both look
like a working retry from the outside; only the second one corrupts the count.

Retries are bounded. An unbounded loop against a hot row is an outage that
presents as a hang, which is the hardest kind to diagnose.

Read with both keys. A filter on one key is a partition scan; a filter on
neither is a table scan. They return the same row for a different amount of
money on every single run.

### Milestone 5: when things go wrong

*Gaps 9-13 — `StationWorker`, `FieldStationCleanup`.*

Quarantine is two operations that must both happen: copy the message somewhere a
human can read it, then remove it from the work queue. Copy first — a crash in
between can duplicate a poison record, which a human can read twice, whereas the
other order loses the evidence entirely.

Cleanup **enumerates before it deletes**. Deleting only what this process
remembers leaves behind everything a previous, crashed run created, which is
exactly the state cleanup exists to resolve. Read the status rows into a list
before deleting any of them: deleting from a partition while paging through the
same partition is a well-known way to skip rows and then report a clean teardown
that is not clean. And report what is left, so the caller can *fail* rather than
log it.

## Run it

The reference solution is an executable that runs the whole pipeline against
Azurite, deliberately including a duplicate upload, a malformed message, and an
effect that fails until its budget is spent:

```bash
docker compose up -d azurite
export AZURITE_CONNECTION_STRING="UseDevelopmentStorage=true"
dotnet run --project projects/field-station/solution
```

Run yours the same way once milestone 5 is green:

```bash
dotnet run --project projects/field-station/starter
```

Observed output, Azurite 3.36.0, .NET 10.0.302:

```text
Field Station — Emulator, station 'ridge-camp'
================================================================

1. Intake — the third upload is a retry of the first
   obs-0001   Stored
   obs-0002   Stored
   obs-0001   Duplicate

2. Dispatch — a duplicate upload produces no second work order
   uploads: 3, work orders: 2
   injected 1 malformed message

3. Drain — obs-0002 fails until its delivery budget is spent
   pass 1: received 3, completed 1, retried 1, quarantined 1, effects applied 1
   pass 2: received 1, completed 0, retried 0, quarantined 1, effects applied 0
   poison: delivery 1 — Undecodable work order: The message body is missing a required work-order field.
   poison: delivery 2 — Checksum tool exited non-zero.

4. Status index — one point-readable row per observation, plus the summary
   obs-0001     Processed    count=1
   obs-0002     Quarantined  count=0
   ~summary     Processed    count=1

5. Cleanup — everything this run created is removed
   artifacts deleted 2, status rows deleted 3, messages remaining 0
   container, queues, and table deleted
```

Three uploads, two work orders, one effect applied. The malformed message never
gets a second delivery; the failing one gets exactly two and is then moved aside
with its reason attached. The run deletes its own container, queues, and table
and exits non-zero if the teardown was incomplete.

Stop the emulator when you are done:

```bash
docker compose down -v
```

### Running against real Azure

**Optional, opt-in, and billable.** Nothing in this project requires it and no
milestone is graded on it. Storage costs for one run of this size are a fraction
of a cent, but a container left behind is a bill that never stops.

The run authenticates with `DefaultAzureCredential` and **refuses to start** if
it finds a shared key or SAS token in the environment. That refusal is
deliberate: a silent fallback to a key is how a course-shaped deployment ends up
with a credential in an environment variable and no audit trail of who used it.

```bash
az login
ACCOUNT="stfieldstation$RANDOM"
GROUP="rg-field-station"

az group create --name "$GROUP" --location westeurope
az storage account create --name "$ACCOUNT" --resource-group "$GROUP" \
  --sku Standard_LRS --kind StorageV2 \
  --allow-shared-key-access false --min-tls-version TLS1_2

SCOPE=$(az storage account show --name "$ACCOUNT" --resource-group "$GROUP" --query id -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)

# Data-plane roles only. Owner on the account does not grant data access, and
# granting it would not be least privilege if it did.
for ROLE in "Storage Blob Data Contributor" \
            "Storage Queue Data Contributor" \
            "Storage Table Data Contributor"; do
  az role assignment create --assignee "$ME" --role "$ROLE" --scope "$SCOPE"
done

unset AZURITE_CONNECTION_STRING
export FIELD_STATION_ENVIRONMENT=live
export FIELD_STATION_ACCOUNT="$ACCOUNT"
dotnet run --project projects/field-station/solution
```

`--allow-shared-key-access false` is the setting that makes the guarantee real:
with it, an account key cannot be used even if one leaks. Role assignments can
take a minute or two to propagate; a `403` immediately after the assignment is
usually that, not a wrong role.

Tear down when you are finished. The run removes its own container, queues, and
table, but the account and group are yours:

```bash
az group delete --name "$GROUP" --yes --no-wait
```

## Expected results

| state | command | result |
| --- | --- | --- |
| untouched starter | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter` | **63 failures**, 22 passing, 85 total |
| finished starter | same command | 85 passing |
| reference solution | `dotnet test projects/field-station/tests` | 85 passing |

The 22 tests that pass against an untouched starter are the ones judging code
you were given — argument validation, the identifier rules, the naming
convention's rejection cases. They are not credit; they are the fixed points the
gaps are measured against.

Per milestone, from an untouched starter:

| milestone | failures | of |
| --- | --- | --- |
| `domain-ports` | 7 | 23 |
| `artifact-storage` | 15 | 15 |
| `work-dispatch` | 8 | 13 |
| `status-index` | 16 | 17 |
| `failure-recovery` | 17 | 17 |

## How this is graded

The evaluator never touches Azure. It uses three deterministic seams, and the
boundary between them is deliberate:

- **In-memory fakes** ([`tests/Fakes.cs`](tests/Fakes.cs)) implement the ports
  with *real* conditional semantics — a create only lands when the name is free,
  a replace only lands when the version matches, and the stored version changes
  on every write. A permissive fake would let last-write-wins pass, which is the
  one bug this project exists to prevent. Redelivery is modelled by receive
  rather than by wall clock, so it is instant and reproducible.
- **A scripted transport** ([`tests/ScriptedClients.cs`](tests/ScriptedClients.cs))
  drives the **real** `BlobContainerClient`, `QueueClient`, and `TableClient` —
  real pipeline, real retry policy, real error classification — with a script
  where the network would be. This is how `If-None-Match`, `If-Match`, the pop
  receipt, and the point-read keys are asserted *on the wire* without a service.
- **Azurite**, for the end-to-end run only. It is where you see the pipeline
  work; it is not where correctness is judged, because emulator timing is not
  reproducible enough to assert on.

Time is injected (`TimeProvider`), so no test sleeps and no row is stamped with
a value that changes between runs.

## What to look at when you are done

Read [`solution/`](solution/) after your own version passes, not before. The
places where the two differ are the interesting ones, and the questions worth
asking are: what happens to this code if the process dies exactly here, and who
arbitrates when two writers disagree?
