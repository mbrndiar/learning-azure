# 💸 Cost awareness and cleanup

Most of this course is free: ordinary lessons, exercises, and tests run against
local emulators or deterministic fakes. Cost and cleanup discipline only matters
at the clearly-marked **live Azure checkpoints**, which use a real subscription
to teach what emulators cannot. This guide states the boundary and the teardown
you run after every live checkpoint.

## Local-first: what is free

| service | local emulator | image (pinned in `compose.yaml`) |
| --- | --- | --- |
| Blob, Queue, Table Storage | Azurite | `mcr.microsoft.com/azure-storage/azurite:3.36.0` |
| Event Hubs | Event Hubs emulator | `mcr.microsoft.com/azure-messaging/eventhubs-emulator:2.2.1` |
| Cosmos DB for NoSQL | Cosmos DB Linux emulator | `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260706` |

Start and stop them from the repository root:

```bash
docker compose up -d       # start
docker compose ps          # check health
docker compose down        # stop and remove
docker compose down -v     # also remove named volumes
```

Emulators cost nothing and need no Azure account. Stopping them is the only
"cleanup" required for local work.

## Emulators are not production parity

Emulators are for development and testing. They deliberately differ from the
cloud, and the course never claims parity:

- **Azurite** supports only Blob, Queue, and Table storage — not Files or Data
  Lake Gen2 — and uses local IP-style endpoints and the well-known development
  account, not `*.core.windows.net` with Entra ID.
- **The Event Hubs emulator** does not persist data or entities across a
  container restart, supports only producer/consumer Kafka APIs, and omits
  virtual-network integration, Entra ID, Capture, and autoscale. It has no
  control plane at all — namespaces, hubs, partition counts, retention, and
  throughput units cannot be changed locally — it does not enforce retention, and
  its single namespace is fixed at `emulatorNs1` (see
  [`infra/local/README.md`](../infra/local/README.md)).
- **The Cosmos DB Linux emulator** implements the API for NoSQL in gateway mode
  with a subset of features and defaults to HTTP; the .NET SDK needs HTTPS mode
  enabled explicitly.

Entra ID, RBAC, real service limits, redundancy, diagnostics, and cost behavior
are taught only at live checkpoints because emulators cannot model them.

## Live checkpoints: what costs money

Live checkpoints create real resources (storage accounts, an Event Hubs
namespace, a Cosmos DB account) that incur charges while they exist. The course
keeps this bounded:

- **Isolate per learner and run.** Resource names and a dedicated resource group
  are unique per learner/run and tagged for discovery and cleanup.
- **Fail closed.** Live scripts stop on the wrong subscription, a missing login,
  a missing role, or an ambiguous resource selection rather than acting on the
  wrong target.
- **Preflight and estimate.** Each checkpoint states the expected cost shape and
  a preflight check before creating anything.
- **Never in CI.** Continuous integration never creates cloud resources and
  never requires stored cloud secrets.
- **Every live checkpoint states its own shape.** Modules 3 and 5 create a
  storage account; module 8 creates a Standard Event Hubs namespace (roughly
  USD 0.03 per throughput-unit-hour, so well under a cent for a ten-minute run);
  module 9 adds a storage account for the checkpoint store; modules 10 and 11
  create a serverless Cosmos DB account, with module 10 briefly switching one
  container to 400 RU/s provisioned throughput at roughly USD 0.008 per hour;
  module 12 creates one standard storage account and a Log Analytics workspace
  and holds them for about forty minutes, which is under a cent of storage and
  ingestion. Each script prints the figure before it creates anything.
- **The Field Station project is graded on the emulator.** Its optional live run
  (`FIELD_STATION_ENVIRONMENT=live`) is opt-in, authenticates with
  `DefaultAzureCredential` against an account with shared-key access disabled,
  and deletes its container, queues, and table at the end of the run. See
  [`projects/field-station/README.md`](../projects/field-station/README.md#running-against-real-azure).
- **The capstone is graded offline.** Its 112-test evaluator needs no emulator
  and no subscription, and its end-to-end host runs on Azurite plus the Event
  Hubs and Cosmos DB emulators. Only milestone 5 goes live, and it is opt-in
  (`EXPEDITION_ENVIRONMENT=live`).
- **The capstone's live checkpoint is the most expensive one in the course**,
  because it is the only one that holds a Standard Event Hubs namespace, a
  serverless Cosmos DB account, and a storage account at the same time. For a
  half-hour run that is roughly two cents of Event Hubs throughput units, a few
  cents of Cosmos request units and storage, and effectively nothing for the
  blobs, queues, and tables — call it under USD 0.10 end to end, and materially
  more if the namespace is left running. The run authenticates with
  `DefaultAzureCredential` against an account with shared-key access disabled and
  removes the container, queues, table, consumer group, and Cosmos container it
  created before the resource group is deleted. See
  [`capstones/cloud-expedition-journal/README.md`](../capstones/cloud-expedition-journal/README.md#run-it-against-live-azure).

### Always tear down after a live checkpoint

Deleting the checkpoint's resource group removes every resource it created.
Both management tracks are equivalent:

```bash
# Azure CLI
az group delete --name <rg-name> --yes --no-wait
```

```powershell
# Azure PowerShell
Remove-AzResourceGroup -Name <rg-name> -Force
```

Then verify the group is gone:

```bash
az group exists --name <rg-name>        # expect: false
```

```powershell
Get-AzResourceGroup -Name <rg-name> -ErrorAction SilentlyContinue  # expect: no output
```

Per-checkpoint teardown and post-cleanup verification are repeated in each live
unit as it is built, so cleanup is never left implicit.

### Deleting the group is not the whole story

[Module 12](../lessons/12-secure-operable-cloud/README.md) turns this section
into a graded outcome. Deleting a resource group leaves three classes of residue
behind, and its labs verify all of them after the group is gone:

- **Soft-deleted resources.** A deleted storage account stays recoverable for 14
  days, a Log Analytics workspace for 14 days with its name reserved, and a key
  vault for the vault's retention period of 7 to 90 days. They keep no
  meaningful cost, but purge protection and a reserved name will refuse a rerun.
- **Orphaned role assignments.** A role assignment scoped to a deleted resource
  outlives it as an entry with an unresolvable principal or scope, and has to be
  removed explicitly.
- **Subscription-scope leftovers.** Anything the lab created outside the group —
  a diagnostic setting, a deployment record — is not covered by
  `az group delete`.

The module's paired labs end with a post-cleanup verification step that reports
every one of these, and the exercise makes you implement the same check as
`TeardownPlan.Verify`.

## Security and secrets

- Never commit credentials, connection strings, account keys, SAS tokens, or
  `.env` files with secrets. `.gitignore` excludes `.env`.
- The default live-authentication story is `DefaultAzureCredential` with
  Microsoft Entra ID. Account keys and SAS are bounded interoperability lessons,
  not the production default.
- Learner progress state stays outside the repository (see
  [`SETUP.md`](SETUP.md#where-your-progress-is-stored)) and is never committed.
