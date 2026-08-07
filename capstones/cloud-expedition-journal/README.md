# 📓 Capstone: the Cloud Expedition Field Journal

> **Final destination.** No new Azure service and no new API: everything here
> was taught in modules 4 to 12, and nothing here is scaffolded for you.
> Prerequisites: every module up to and including
> [module 12](../../lessons/12-secure-operable-cloud/README.md), plus Docker.
> **No Azure subscription is required.** The live run is optional, opt-in, and
> billable — see [Run it against live Azure](#run-it-against-live-azure).

The Field Station project put Blob, Queue, and Table behind ports you owned. The
modules after it added a stream that remembers, a database that charges by the
request, and an identity story that has to hold under least privilege. This
capstone is all of it in one system: telemetry arrives on Event Hubs, reports
land in Blob Storage, work travels on a queue, station state lives in Table
Storage, and the queryable journal is a Cosmos container.

Five services, one codebase, and the same three inconvenient facts as ever: the
producer retries, the consumer restarts, and the queue delivers more than once.

## Objectives

By the end of this capstone you can:

- **build** an end-to-end pipeline across Event Hubs, Blob, Queue, Table, and
  Cosmos DB behind application-owned ports, with no Azure type above the adapter
  layer, so the whole flow is testable without a service;
- **implement** stream consumption that owns its partitions, resumes from its own
  checkpoints, and treats a redelivered event as a replay rather than as news;
- **apply** the conditional-write vocabulary uniformly — `If-None-Match: *`,
  `If-Match`, conditional insert, ETag replace, Cosmos create-versus-replace —
  as the mechanism behind every idempotency claim the system makes;
- **operate** the run safely: bounded retries, throttle budgets that count the
  cost of a refused request, poison quarantine, cooperative cancellation,
  least-privilege identity, and a teardown that reports whether it finished; and
- **verify** all of it deterministically against fakes, a scripted transport, and
  the local emulators, with no live Azure dependency anywhere in the grade.

## The scenario

Two camps report temperature over a satellite link that drops. Ridge Camp's
laptop retries whatever it is unsure about; Delta Camp's does the same. Base
camp wants three things: one durable report per identity, an idempotent summary artifact
per report, and one page per station showing what was observed and in what
order — cheap enough to refresh all day without an invoice anybody notices.

Four things are true at once, and every decision below follows from them:

1. **The producer retries.** The same reading is published more than once.
2. **The stream redelivers.** A checkpoint is a *position*, and positions are
   coarse: a crash after the checkpoint replays everything since it.
3. **The queue delivers at least once.** There is no setting that turns that off.
4. **The database pushes back.** Cosmos answers `429` when the workload exceeds
   the throughput it was provisioned, and the refused request is still billed.

## Architecture

```text
   stations                ┌───────────────────────────────────────────────┐
      │                    │             your application                  │
      │ readings           │                                               │
      ▼                    │  TelemetryIngress ──► ITelemetryFeed          │
 ┌──────────┐              │                                               │
 │ Event    │◄─────────────┤  TelemetryProcessor ──► ICheckpointStore      │
 │ Hubs     │──────────────►         │                                     │
 └──────────┘   partitions │         ▼                                     │
                           │  ReportIntake ──────► IArtifactVault          │
                           │         │                                     │
                           │         ▼                                     │
                           │  WorkDispatcher ────► IWorkBacklog            │
                           │         │                                     │
                           │         ▼                                     │
                           │  ArtifactWorker ────► IStationRegistry        │
                           │         │                                     │
                           │         ▼                                     │
                           │  JournalProjector ──► IJournalProjection      │
                           └───────────┬───────────────────────────────────┘
                                       │
        ┌──────────────────────────────┼──────────────────────────────┐
        ▼                ▼             ▼              ▼               ▼
 ┌─────────────┐ ┌──────────────┐ ┌──────────┐ ┌────────────┐ ┌─────────────┐
 │ EventHubs   │ │ Blob         │ │ Queue    │ │ Table      │ │ Cosmos      │
 │ TelemetryFeed│ │ ArtifactVault│ │ WorkDisp.│ │ StationReg.│ │ Projection  │
 │             │ │ CheckpointV. │ │          │ │            │ │             │
 └─────────────┘ └──────────────┘ └──────────┘ └────────────┘ └─────────────┘
```

Six ports — `ITelemetryFeed`, `ICheckpointStore`, `IArtifactVault`,
`IWorkBacklog`, `IStationRegistry`, `IJournalProjection` — expose no SDK type.
That is not decoration: it is what lets the evaluator drive five services'
worth of failure behaviour in memory, and it is why the interesting cases here
are testable at all. The adapters are the only files that know Azure exists, and
the ones that can be judged offline are judged against the *real* SDK clients
over a scripted transport, so the conditional headers are asserted on the wire.

## Data flow

One reading, from a camp to the expedition lead's page:

| # | step | what makes it safe |
| --- | --- | --- |
| 1 | the camp publishes `obs-0001` | the partition key is the station, so one station's readings stay ordered |
| 2 | `TelemetryIngress.Plan` batches it | one batch carries one key; a batch is the unit the service routes |
| 3 | `BlobCheckpointVault.TryClaimAsync` | a conditional blob write decides which processor owns the partition |
| 4 | `TelemetryProcessor` reads from the checkpoint | the resume position is the consumer's own, not the service's guess |
| 5 | an event at or below the watermark | counted as a replay and skipped before the handler ever sees it |
| 6 | `ReportIntake.PreserveAsync` | `If-None-Match: *` — the service decides "new or duplicate", not the caller |
| 7 | `WorkDispatcher.DispatchAsync` | only a write that actually happened produces a work order |
| 8 | `ArtifactWorker.ProcessAsync` receives it | the delivery budget is checked before any work is claimed or done |
| 9 | `StationLedger.TryClaimAsync` | a conditional **insert**; losing it is how a duplicate is detected |
| 10 | the effect runs, then confirm, then delete | a crash between them costs one receive, never a lost record |
| 11 | the checkpoint is written | **after** the handler succeeded, so a crash costs a duplicate, not a gap |
| 12 | `JournalProjector.ProjectAsync` | point read by `(partition key, id)`, then create or ETag-replace |
| 13 | `429` from Cosmos | waited out for the service's own interval, and its charge still counted |
| 14 | `ReadStationAsync` pages the station | the continuation token is the only end-of-results signal |
| 15 | `ExpeditionCleanup.RemoveAsync` | enumerates before deleting, so earlier crashed runs are removed too |

Steps 10 and 11 are the capstone in miniature. Both records say an effect
**completed**, never that it was attempted, so anything left half-written reads
as "this may or may not have run" — and the only safe reading of that is to run
it again, which is exactly what every conditional write above makes free.

## Where the resources live

| resource | name | why it exists |
| --- | --- | --- |
| event hub | `telemetry` (4 partitions) | the ordered, replayable record of what the stations said |
| consumer group | `field-journal` | this consumer's own cursor over the hub |
| container | `expedition-journal` | reports at `journal/{station}/{observation}.json`, checkpoints at `checkpoints/{partition}.json` |
| queue | `journal-work` | one work order per stored report |
| queue | `journal-work-poison` | Storage queues have no dead-letter queue, so quarantine is an ordinary second queue you own |
| table | `expeditionstations` | partition = station, row = observation, plus a `~watermark` row per station |
| Cosmos database | `expedition` | the journal projection |
| Cosmos container | `journal` (`/stationId`) | one logical partition per station, because that is what every query filters on |

`~watermark` is legal in a Table row key, sorts after every observation, and is
rejected by the identifier rules, so it can never collide with a real one. The
checkpoint blobs share the reports' container because they share a lifetime:
deleting the container is a complete teardown, and a checkpoint that outlives
its reports is worse than no checkpoint at all.

## Set up

```bash
ACCEPT_EULA=Y docker compose up -d azurite eventhubs cosmos
source capstones/cloud-expedition-journal/emulator.env
```

`ACCEPT_EULA=Y` is the Event Hubs emulator's licence acceptance; it stays down
without it, on purpose. [`emulator.env`](emulator.env) exports the published
emulator defaults and nothing else — see
[docs/SETUP.md](../../docs/SETUP.md) for the stack itself.

The emulators are needed for the end-to-end run only. **Every graded test is
offline**: you can work every milestone with Docker stopped.

## 🧩 Work the milestones

Fill the gaps in [`starter/`](starter/). Each of the 25 gaps throws a
`NotImplementedException` naming the section here that answers it, and the
signatures, doc comments, and reasoning are already written — the work is
deciding, not guessing at an API.

One evaluator judges both trees. Point it at the starter while you work:

```bash
dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter
```

The Learning Mentor unlocks the reference implementation after deterministic
success or an explicit post-attempt unlock request.

Grade one milestone at a time, in order:

| # | milestone | command |
| --- | --- | --- |
| 1 | [the domain and the ports](#milestone-1-the-domain-and-the-ports) | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=domain-ports` |
| 2 | [the storage workflow](#milestone-2-the-storage-workflow) | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=storage-workflow` |
| 3 | [the telemetry pipeline](#milestone-3-the-telemetry-pipeline) | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=telemetry-pipeline` |
| 4 | [the journal projection and operational boundary](#milestone-4-the-journal-projection) | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=cosmos-projection` |

An untouched starter fails every required milestone. The operational-boundary
tests validate the optional Azure adapter offline without making a subscription
part of course completion. See
[Expected results](#expected-results).

### Milestone 1: the domain and the ports

*Gaps 1-5 — `ExpeditionNaming`, `JournalCodec`, `TelemetryIngress`.*

Identity is derived, never invented. The partition key, the blob name, the
work-order id, and the Cosmos item id are all pure functions of one
`ObservationKey`, so a replayed reading collides with itself in four places and
every collision is detectable. Put a timestamp or a GUID in any one of them and
the replay silently becomes a second observation that nothing downstream can
tell apart from a real one.

Four decisions worth internalising:

- **The partition key decides what stays ordered.** Event Hubs guarantees order
  inside a partition and nothing across partitions. Keying on the station keeps
  one station's readings in one partition, in the order it sent them. Keying on
  the observation spreads a single station across every partition and makes "the
  last reading from this station" a lie that is right most of the time.
- **A Cosmos item is identified by `(partition key, id)`, not by `id`.** The
  station is already the partition key, so repeating it inside the id buys
  nothing and costs bytes on every index entry. What the id must be is *stable*:
  derived from the observation, so a replayed reading addresses the document it
  already wrote.
- **A partially valid payload is still poison.** `{"stationId":"ridge-camp"}`
  deserialises into a reading whose temperature is `0` and whose timestamp is the
  zero instant — plausible-looking values that every later stage will believe.
  Validate at the boundary, where the failure is still cheap.
- **A batch carries one partition key.** Mixing keys in one batch is either
  rejected outright or silently unroutable, depending on how the batch was
  created, and the size ceiling belongs to the producer: split at it, and a group
  of exactly the ceiling is one batch, not two.

### Milestone 2: the storage workflow

*Gaps 6-12 — `ReportIntake`, `WorkDispatcher`, `StationLedger`, `ArtifactWorker`.*

This is the Field Station's pipeline, rebuilt over the capstone's domain, and
the reasoning has not changed: "does it exist?" followed by "write it" is two
round trips with a race between them, and the caller most likely to be inside
that window is precisely the retrying producer you are trying to handle.
`If-None-Match: *` and a conditional insert make the decision atomic and put it
where it belongs — in the service, not in your `if`.

What is new is the **watermark row**. It is the one row every event for a station
contends on, and it is where optimistic concurrency stops being a slogan:

- **Re-read inside the retry loop.** A retry that resends the same stale ETag
  fails identically forever. A retry that resends a *fresh* ETag carrying the
  value computed from the stale read silently reintroduces the lost update the
  ETag existed to prevent. Both look like a working retry from the outside; only
  the second corrupts the count.
- **The watermark only moves forward.** An out-of-order or replayed event carries
  a position the row has already passed. Accepting it lets a replay rewind the
  consumer and redeliver everything after it.
- **Retries are bounded.** An unbounded loop against a hot row is an outage that
  presents as a hang, which is the hardest kind to diagnose.

And the worker's three rules, which the evaluator checks individually:
decode and check the delivery budget *before* claiming anything; quarantine an
undecodable message on its **first** delivery, because retrying a deterministic
failure buys nothing but a queue that never drains; and let
`OperationCanceledException` propagate, because shutdown is not a message defect
— not deleting the message *is* the retry.

### Milestone 3: the telemetry pipeline

*Gaps 13-15, 19-21 — `TelemetryProcessor`, `BlobCheckpointVault`,
`TelemetryEventMapper`.*

A checkpoint is two claims in one blob: **who owns this partition** and **how far
they got**. Both are conditional writes, and both failures are answers rather
than errors:

- A free partition is claimed with `If-None-Match: *`. Two processors starting
  together both find no blob; only the conditional create lets the service decide
  which of them owns it.
- Taking over an expired lease is `If-Match` against the version just read.
  Without the precondition, two processors that both observe the same expired
  lease both take it — precisely the race the lease was supposed to settle.
- A checkpoint write under a lease ETag that has moved on returns nothing, and
  that is how a processor learns it no longer owns the partition. Anything it
  keeps handling afterwards is work the new owner is also doing.

Then the ordering that defines the whole stage: **checkpoint after the handler
succeeded, never before.** A checkpoint written first turns every crash into
silent data loss — the position says the event was handled and nothing will ever
deliver it again. Written after, the same crash costs a duplicate, which the
ledger from milestone 2 already absorbs. Write a closing checkpoint for the tail
the interval did not cover, or a clean shutdown replays for no reason at all.

Finally, carry the **service's** coordinates. The sequence number and offset are
how a partition addresses itself: they are what a checkpoint stores and what a
resume seeks to. A counter the consumer maintains itself looks identical on a
first run and diverges permanently after the first restart or rebalance.

### Milestone 4: the journal projection

*Gaps 16-18, 22-23 — `JournalProjector`, `CosmosOutcomes`,
`CosmosJournalProjection`.*

Cosmos makes the cost of a design decision visible on every response, which is
why the projection is the last stage rather than the first.

- **Name the partition key on every operation.** Cosmos will accept a write
  without one and route it by reading the document, but a read or delete without
  one fans out across every physical partition. The cost of a point operation is
  then set by the size of the container rather than by the size of the answer.
- **`429` is rate limiting, not an outage.** It is the service enforcing the
  throughput that was provisioned, and it arrives with the wait it wants.
  Retrying immediately makes the pressure worse; failing makes a healthy,
  correctly sized workload look broken. The refused attempt is still charged —
  counting only successful attempts is how a throttled workload's real cost stays
  hidden until the invoice arrives.
- **Create versus conditional replace is the idempotency.** A create's `409`
  means somebody got there first; a replace's `412` means this caller's version
  is stale. Everything else — `401`, `403`, `503` — must keep travelling. A
  catch-all that swallows them turns a missing role assignment into an empty
  journal.
- **A lost race is re-decided, not re-sent.** Resending the same body under a
  fresh ETag is the lost update the ETag existed to prevent, dressed as a working
  retry. Go round the loop: read again, compare again.
- **The continuation token is the only end-of-results signal.** Cosmos may cut a
  page short at a size or time budget and still have more to give, so "fewer
  items than I asked for" means nothing. A reader that stops on a short page
  truncates its answer only under load, which is when it matters most.

#### Operational boundary: offline graded, live run optional

*Gaps 24-25 — `ExpeditionEnvironmentFactory`, `ExpeditionCleanup`.*

These final gaps remain part of milestone 4 and are graded offline. They make a
live run possible without requiring a subscription or proving completion from
an automated test. Two properties separate a system that can be run in a
subscription from one that merely works on a laptop.

**Identity, with no fallback.** "Use Entra ID if it works, otherwise the key" is
a fallback that succeeds on the day the role assignment is missing, which is the
exact day it should fail. A live run here refuses to start while any key,
connection string, or SAS token is in the environment — including the emulator
values `emulator.env` exports. Refusing outright is what makes the identity path
the only path, and it is why the roles below are data-plane roles: Owner on a
resource does not grant data access, and granting it would not be least
privilege if it did.

**Teardown that enumerates.** Deleting only what this process remembers leaves
behind everything a previous, crashed run created — exactly the state cleanup
exists to resolve. List by prefix, page the query to its end, and read rows into
a list before deleting any of them, because deleting from a partition while
paging through it is a well-known way to skip rows and then report a clean
teardown that is not clean. Then report what is left, so the caller can *fail*
rather than log it.

## ▶️ Run it locally

The reference solution is an executable that runs the whole pipeline against the
emulators, deliberately including a duplicate reading, a replayed partition, a
malformed work order, an effect that fails until its budget is spent, and a
second projection pass over work that is already projected:

```bash
ACCEPT_EULA=Y docker compose up -d azurite eventhubs cosmos
source capstones/cloud-expedition-journal/emulator.env
dotnet run --project capstones/cloud-expedition-journal/solution
```

Run yours the same way once milestone 5 is green:

```bash
dotnet run --project capstones/cloud-expedition-journal/starter
```

Observed output on a freshly created stack:

```text
Cloud Expedition Journal — Emulator
====================================================================

1. Ingress — readings are batched by partition key; the last one is a duplicate
   batch key=ridge-camp   readings=3
   batch key=delta-camp   readings=1
   published 4 readings in 2 batches

2. Processing — partitions are claimed, read in order, then checkpointed
   owned 4, read 4, handled 4, replays skipped 0, checkpoints 3
   reports stored 3, duplicates absorbed 1

3. Replay — a second pass resumes from the checkpoint instead of re-reading
   owned 4, read 0, handled 0, replays skipped 0

4. Work — one malformed order, and one effect that fails until its budget is spent
   pass 1: received 4, completed 2, retried 1, quarantined 1
   pass 2: received 1, completed 0, retried 0, quarantined 1
   poison: delivery 1 — Undecodable work order: The message body is missing a required work-order field.
   poison: delivery 2 — Summary tool exited non-zero.

5. Projection — the journal converges, and re-running it changes nothing
   pass 1: written 4, superseded 0, request units 4.00
   pass 2: written 0, superseded 4, request units 0.00

6. Query — a single-partition read, paged to the continuation token's end
   ridge-camp   entries 2 over 1 pages, 1.00 RU
      entry-obs-0002 seq=1    -13.25C journal/ridge-camp/obs-0002.json
      entry-obs-0001 seq=2    -14.50C journal/ridge-camp/obs-0001.json
   delta-camp   entries 1 over 1 pages, 1.00 RU
      entry-obs-0001 seq=0    -8.75C journal/delta-camp/obs-0001.json

7. Teardown — everything this run created is removed
   reports 3, checkpoints 4, station rows 3, journal entries 3, messages remaining 0
   container, queues, table, and Cosmos database deleted
```

Four readings, three reports, two work orders completed, three journal entries.
The duplicate reading is absorbed at intake and never produces a second work
order; the malformed message never gets a second delivery; the failing one gets
exactly two and is then moved aside with its reason attached; and the second
projection pass writes nothing at all, which is the whole idempotency argument
on one line.

Two counts are worth reading twice. **Pass 1 writes four entries into three
documents** — the repeated reading arrives at a later stream position, so it
updates the entry it already wrote rather than adding another. And **run the
command twice without recreating the stack and the numbers grow**: the run
deletes its own checkpoints during teardown, but the hub keeps its events for
its retention period, so the next run legitimately re-reads them. That is the
difference between a checkpoint's lifetime and a stream's, and it is worth
seeing once. Reset with:

```bash
docker compose down -v
ACCEPT_EULA=Y docker compose up -d azurite eventhubs cosmos
```

Stop the emulators when you are done:

```bash
docker compose down -v
```

## ☁️ Run it against live Azure

**Optional, opt-in, and billable.** Nothing in this capstone requires it and no
milestone is graded on it. A single run of this size costs a few cents; an event
hub namespace and a Cosmos container left running are a bill that does not stop.
Read [docs/COST-AND-CLEANUP.md](../../docs/COST-AND-CLEANUP.md) first.

The run authenticates with `DefaultAzureCredential` and **refuses to start** if
it finds a key, connection string, or SAS token in the environment — so start
from a shell that has never sourced `emulator.env`.

```bash
az login

GROUP="rg-expedition-journal"
LOCATION="westeurope"
ACCOUNT="stexpedition$RANDOM"
NAMESPACE="evhns-expedition$RANDOM"
COSMOS="cosmos-expedition$RANDOM"

az group create --name "$GROUP" --location "$LOCATION"

az storage account create --name "$ACCOUNT" --resource-group "$GROUP" \
  --sku Standard_LRS --kind StorageV2 \
  --allow-shared-key-access false --min-tls-version TLS1_2

az eventhubs namespace create --name "$NAMESPACE" --resource-group "$GROUP" \
  --sku Standard --location "$LOCATION" --disable-local-auth true
az eventhubs eventhub create --name telemetry --namespace-name "$NAMESPACE" \
  --resource-group "$GROUP" --partition-count 4 --cleanup-policy Delete --retention-time-in-hours 1
az eventhubs eventhub consumer-group create --name field-journal \
  --eventhub-name telemetry --namespace-name "$NAMESPACE" --resource-group "$GROUP"

az cosmosdb create --name "$COSMOS" --resource-group "$GROUP" \
  --locations regionName="$LOCATION" --default-consistency-level Session

ME=$(az ad signed-in-user show --query id -o tsv)
STORAGE_SCOPE=$(az storage account show --name "$ACCOUNT" --resource-group "$GROUP" --query id -o tsv)
HUB_SCOPE=$(az eventhubs namespace show --name "$NAMESPACE" --resource-group "$GROUP" --query id -o tsv)

# Data-plane roles only. Owner on the account does not grant data access, and
# granting it would not be least privilege if it did.
for ROLE in "Storage Blob Data Contributor" \
            "Storage Queue Data Contributor" \
            "Storage Table Data Contributor"; do
  az role assignment create --assignee "$ME" --role "$ROLE" --scope "$STORAGE_SCOPE"
done

for ROLE in "Azure Event Hubs Data Sender" \
            "Azure Event Hubs Data Receiver"; do
  az role assignment create --assignee "$ME" --role "$ROLE" --scope "$HUB_SCOPE"
done

# Cosmos data-plane access is its own RBAC system: control-plane roles do not
# grant it, and it is assigned with `az cosmosdb sql role assignment`, not
# `az role assignment`.
az cosmosdb sql role assignment create --account-name "$COSMOS" --resource-group "$GROUP" \
  --role-definition-id 00000000-0000-0000-0000-000000000002 \
  --principal-id "$ME" --scope "/"

export EXPEDITION_ENVIRONMENT=live
export EXPEDITION_STORAGE_ACCOUNT="$ACCOUNT"
export EXPEDITION_EVENTHUBS_NAMESPACE="$NAMESPACE"
export EXPEDITION_COSMOS_ENDPOINT="https://$COSMOS.documents.azure.com:443/"
dotnet run --project capstones/cloud-expedition-journal/solution
```

The mirrored Azure PowerShell workflow is
[`infra/powershell/cloud-expedition-journal.ps1`](../../infra/powershell/cloud-expedition-journal.ps1);
the same steps in Azure CLI form are
[`infra/azure-cli/cloud-expedition-journal.sh`](../../infra/azure-cli/cloud-expedition-journal.sh).

Three settings carry the security argument: `--allow-shared-key-access false`
means an account key cannot be used even if one leaks, `--disable-local-auth
true` does the same for Event Hubs SAS policies, and Cosmos data-plane RBAC is
assigned separately because a control-plane Contributor genuinely cannot read a
document. Role assignments take a minute or two to propagate; a `403`
immediately after assignment is usually that, not a wrong role.

Tear down when you are finished. The run removes its own container, queues,
table, and Cosmos database, but the account, namespace, and group are yours:

```bash
az group delete --name "$GROUP" --yes --no-wait
```

## ✅ Expected results

| state | command | result |
| --- | --- | --- |
| untouched starter | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter` | **84 failures**, 28 passing, 112 total |
| finished starter | same command | 112 passing |

The 28 tests that pass against an untouched starter judge code you were given —
argument validation, the identifier rules, the adapters that carry no gap. They
are not credit; they are the fixed points the gaps are measured against.

Per milestone, from an untouched starter:

| milestone | failures | of |
| --- | --- | --- |
| `domain-ports` | 17 | 30 |
| `storage-workflow` | 17 | 17 |
| `telemetry-pipeline` | 20 | 28 |
| `cosmos-projection` | 30 | 37 |

## 🧪 How this is graded

The evaluator never touches Azure and never opens a socket. It uses three
deterministic seams, and the boundary between them is deliberate:

- **In-memory fakes** ([`tests/Fakes.cs`](tests/Fakes.cs)) implement the six
  ports with *real* conditional semantics — a create only lands when the name is
  free, a replace only lands when the version matches, a lease can be stolen, a
  query returns real continuation tokens, and a projection can be made to
  throttle on demand. A permissive fake would let last-write-wins pass, which is
  the one class of bug this capstone exists to prevent. Redelivery is modelled by
  receive rather than by wall clock, so it is instant and reproducible.
- **A scripted transport**
  ([`tests/ScriptedClients.cs`](tests/ScriptedClients.cs)) drives the **real**
  `BlobContainerClient` — real pipeline, real retry policy, real error
  classification — with a script where the network would be. That is how
  `If-None-Match: *` and `If-Match` on the checkpoint lease are asserted *on the
  wire* without a service. Event Hubs is graded the same way one layer up, with
  `EventHubsModelFactory` events, because AMQP cannot be scripted over HTTP; and
  Cosmos error classification is graded against a real `CosmosException`.
- **The emulators**, for the end-to-end run only. They are where you see the
  pipeline work; they are not where correctness is judged, because emulator
  timing is not reproducible enough to assert on.

Time is injected (`TimeProvider`), so no test sleeps, no row is stamped with a
value that changes between runs, and a throttle's wait is asserted rather than
endured.

### Adversarial evidence

Each of these is a plausible implementation the suite rejects, and the test that
rejects it. They are the reason the milestone commands mean something:

| plausible mistake | what catches it |
| --- | --- |
| a timestamp or GUID in a derived name | `TheArtifactNameIsAPureFunctionOfTheKey` |
| the observation as the partition key | `ThePartitionKeyIsTheStationSoOneStationStaysOrdered` |
| an Azure type leaking into the domain | `TheDomainNeverNamesAnAzureType` |
| accepting a payload with fields missing | `APartiallyValidReadingIsStillRejected` |
| a work-order id that is not derived from its own fields | `AWorkOrderWhoseIdDoesNotMatchItsKeysIsRejected` |
| read-then-write instead of `If-None-Match: *` | `IntakeWritesOnceRatherThanReadingThenWriting` |
| a duplicate upload overwriting the original | `ADuplicateNeverOverwritesTheStoredReport` |
| a duplicate upload dispatching a second work order | `ARepeatedReadingIsPreservedOnceAndDispatchedOnce` |
| the re-read hoisted out of the concurrency loop | `AContendedWatermarkKeepsBothWritersCounts` |
| a watermark that accepts an older position | `TheWatermarkNeverMovesBackwards` |
| an unbounded concurrency retry loop | `APermanentlyContendedRowFailsInsteadOfLoopingForever` |
| quarantining a message on cancellation | `ShutdownDuringAnEffectLeavesTheMessageOnTheQueue` |
| retrying an undecodable message | `AMalformedMessageIsQuarantinedOnItsFirstDelivery` |
| deleting a poison message before copying it aside | `AQuarantinedMessageIsCopiedAsideBeforeItIsDeleted` |
| re-running an effect that already succeeded | `ARedeliveredMessageDoesNotRepeatASucceededEffect` |
| checkpointing before the handler runs | `NothingIsCheckpointedWhenTheHandlerThrows` |
| reading a partition without claiming it | `AProcessorClaimsEveryPartitionBeforeItReadsIt` |
| taking over a live lease | `ALivePartitionHeldByAnotherHostIsNotTouchedAtAll` |
| ignoring a lost lease mid-partition | `AProcessorThatLostItsLeaseStopsInsteadOfCheckpointing` |
| a plain upload where `If-None-Match: *` belongs | `ClaimingAFreePartitionPutsIfNoneMatchOnTheWire` |
| an unconditional takeover of an expired lease | `TakingOverAnExpiredLeaseIsConditionalOnTheVersionJustRead` |
| a missing checkpoint read as position zero | `AMissingCheckpointReadsAsNoPositionRatherThanPositionZero` |
| a consumer-maintained sequence counter | `AnEventCarriesTheServicesOwnCoordinatesBackIntoTheDomain` |
| an empty read treated as an event | `AReadThatTimedOutIsNotMistakenForAnEvent` |
| swallowing a `403` as if it were a throttle | `AForbiddenResponseKeepsTravellingInsteadOfReadingAsNoData` |
| classifying anything but `429` as a throttle | `EveryOtherStatusIsLeftForTheCallerToClassify` |
| a throttle whose refused charge is not counted | `AThrottleIsWaitedOutAndTheRefusedAttemptIsStillCharged` |
| resending a stale body under a fresh ETag | `ALostRaceIsReReadAndReDecidedNotResentUnderAFreshETag` |
| an out-of-order event overwriting a later entry | `AnOutOfOrderEventNeverRewindsTheStoredEntry` |
| stopping paging on a short page | `AShortPageIsNotMistakenForTheEndOfTheResults` |
| a query that leaves its own partition | `AQueryStaysInsideItsOwnPartition` |
| a live run falling back to a key | `EveryShapeOfAmbientSecretIsRefused` |
| a control-plane role standing in for data-plane access | `EveryRequiredRoleIsADataPlaneRoleAndNoneIsContributor` |
| skipping certificate validation outside the emulator | `OnlyTheEmulatorIsAllowedToSkipCertificateValidation` |
| deleting only what this process remembers | `TeardownRemovesWorkAPreviousRunLeftBehind` |
| a teardown that claims success with work still queued | `TeardownReportsAnIncompleteQueueRatherThanClaimingSuccess` |

## 🧭 What to look at when you are done

Read [`solution/`](solution/) after your own version passes, not before. The
places where the two differ are the interesting ones, and the questions worth
asking are the same three the whole course has been circling: what happens to
this code if the process dies exactly here, who arbitrates when two writers
disagree, and what does this cost when it is right.
