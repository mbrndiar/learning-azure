# Learning Azure — Data and Messaging

> **Status: the course is complete — all fourteen units are built.** From choosing a data primitive,
> through Storage, Event Hubs, and Cosmos DB for NoSQL, to securing and operating
> the live architecture, each module ships complete: a narrative lesson, a
> runnable companion whose captured output is in the lesson, a starter, a
> reference solution, a shared xUnit evaluator, and (from module 3) paired Azure
> CLI and Azure PowerShell labs. The **Field Station project** sits between
> modules 7 and 8 and applies all three Storage services in one worker across five
> milestones. The **Cloud Expedition Field Journal capstone** closes the course
> after module 12 and integrates all five services — Event Hubs ingress, Blob
> checkpoints and artifacts, Queue dispatch, Table state, Cosmos projection —
> across five more milestones. Nothing here claims coverage the repository does
> not have: the course verifier fails if a unit is advertised or made trackable
> before its content exists.

A narrative-driven, hands-on course that teaches the Azure data and messaging
plane in **C# on .NET 10** through one continuing story: the **Cloud Expedition
Field Journal**. You join a field expedition and incrementally build its data
plane, meeting each Azure service where a real design problem calls for it.

- **Azure Blob Storage** — field reports, photos, and immutable artifacts.
- **Azure Queue Storage** — background artifact-processing work orders.
- **Azure Table Storage** — inexpensive station status and observation indexes.
- **Azure Event Hubs** — high-volume sensor telemetry partitioned by station.
- **Azure Cosmos DB for NoSQL** — the queryable global expedition catalog.

## Who this is for

- **Audience:** experienced developers who already know basic C# and .NET.
- **Assumed knowledge:** writing, building, and running a small C# program;
  reading types, generics, `async`/`await`, and exceptions; using a terminal and
  Git. You do **not** need prior Azure, cloud, or distributed-systems experience.
- **Prerequisites are distinct from what the course teaches.** Azure services,
  the Azure SDK for .NET, emulators, partitioning, delivery semantics, and cloud
  operations are taught here, not assumed.

## What you will be able to do

By the end of the course you will be able to:

- choose the right Azure data primitive — blob, queue, table, event stream, or
  document — and justify it against the adjacent option;
- build a testable C# Azure client using the Azure SDK for .NET with
  `DefaultAzureCredential`, cancellation, retries, and injected seams;
- store and retrieve artifacts in Blob Storage with conditional writes and ETags;
- dispatch and idempotently process work through Queue Storage, including
  visibility timeout, dequeue count, and poison handling;
- index observations in Table Storage with partition/row-key design and
  optimistic concurrency;
- produce and consume partitioned telemetry through Event Hubs with consumer
  groups and Blob checkpointing;
- model, query, and update data in Cosmos DB for NoSQL with request-unit and
  consistency awareness; and
- operate the live architecture with Microsoft Entra ID, least-privilege RBAC,
  diagnostics, cost controls, and complete cleanup.

## Course map

Fourteen units: twelve modules, one applied project, and the capstone. The
prerequisite graph, measurable outcomes, split decisions, and per-unit evidence
records are in [`docs/architecture/curriculum.md`](docs/architecture/curriculum.md);
the current coverage record is
[`docs/architecture/evidence-matrix.md`](docs/architecture/evidence-matrix.md).

| # | unit | teaches | status |
| --- | --- | --- | --- |
| 1 | [`module.azure-data-map`](lessons/01-azure-data-map/README.md) | choosing between blob, queue, table, stream, and document | built |
| 2 | [`module.azure-sdk-foundations`](lessons/02-azure-sdk-foundations/README.md) | testable Azure SDK clients, credentials, retries, cancellation | built |
| 3 | [`module.storage-account`](lessons/03-storage-account/README.md) | the shared account boundary, plus the first live checkpoint | built |
| 4 | [`module.blob-storage`](lessons/04-blob-storage/README.md) | containers, streaming transfer, metadata, tags, listing | built |
| 5 | [`module.blob-lifecycle`](lessons/05-blob-lifecycle/README.md) | ETag preconditions, versioning, soft delete, tiering | built |
| 6 | [`module.queue-storage`](lessons/06-queue-storage/README.md) | visibility timeout, at-least-once delivery, idempotency, poison work | built |
| 7 | [`module.table-storage`](lessons/07-table-storage/README.md) | partition/row-key design, optimistic concurrency, batches | built |
| — | [`project.field-station`](projects/field-station/README.md) | **applied project:** Blob + Queue + Table worker | built |
| 8 | [`module.event-hubs-model`](lessons/08-event-hubs-model/README.md) | partitions, keys, batching, retention, throughput | built |
| 9 | [`module.event-hubs-processing`](lessons/09-event-hubs-processing/README.md) | consumer groups, Blob checkpointing, replay, recovery | built |
| 10 | [`module.cosmos-modeling`](lessons/10-cosmos-modeling/README.md) | partition-key design, request units, consistency, indexing | built |
| 11 | [`module.cosmos-development`](lessons/11-cosmos-development/README.md) | queries, pagination, patch, batch, throttling, diagnostics | built |
| 12 | [`module.secure-operable-cloud`](lessons/12-secure-operable-cloud/README.md) | Entra ID least privilege, credential resolution, diagnostics, cost, teardown | built |
| — | [`capstone.cloud-expedition-journal`](capstones/cloud-expedition-journal/README.md) | **capstone:** the end-to-end field journal across all five services | built |

### How a module works

Every built module follows the same shape, so the second one costs you no
navigation:

1. **Read** `lessons/<NN>-<slug>/README.md`. It is the primary teaching text —
   prose first, code where prose is not enough.
2. **Run the companion**, for example
   `dotnet run --project lessons/04-blob-storage/ArtifactVault`. The lesson quotes
   its real output, so you can tell a broken environment from a surprising result.
3. **Do the paired management labs** (modules 3-12): `infra/azure-cli/<slug>.sh`
   and `infra/powershell/<slug>.ps1` do behaviorally equivalent work.
4. **Fill the gaps** in `exercises/<NN>-<slug>/starter`. Every gap throws a
   `NotImplementedException` naming the lesson section that answers it. Grade with
   `dotnet test exercises/<NN>-<slug>/tests -p:Implementation=starter`, which fails
   before you start and passes when you are done. Drop `-p:Implementation=starter`
   to run the same evaluator against the reference solution.

Which emulator a module needs is stated in its lesson:

| modules | command |
| --- | --- |
| 3-7 | `docker compose up -d azurite` |
| 8-9 | `ACCEPT_EULA=Y docker compose up -d eventhubs` (starts Azurite too; module 9 checkpoints into it) |
| 10-11 | `docker compose up -d cosmos` |
| 12 | none — the companion, exercise, and evaluator are offline; the labs are live-only |

Modules 3, 5, and 8 through 12 additionally have a **required live checkpoint**
against a real Azure subscription; all are clearly marked, state their cost
shape, and end in teardown.

The project sits after every Storage module because Event Hubs and Cosmos DB
build on Storage experience the learner has already applied unaided. The capstone
sits last, for the same reason applied to the whole course: it assumes every
service and adds nothing new.

### How the project and the capstone work

Both are staged into five milestones and share one shape:

1. **Read the guide** — [`projects/field-station/README.md`](projects/field-station/README.md)
   or [`capstones/cloud-expedition-journal/README.md`](capstones/cloud-expedition-journal/README.md).
   It states the architecture, the numbered gaps, and the acceptance criteria.
2. **Fill the gaps** in the `starter` tree, milestone by milestone.
3. **Grade one milestone** with, for example,
   `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=domain-ports`.
   Drop the `-p:` property to run the same evaluator against the reference
   solution.

Both evaluators are fully offline. The capstone's end-to-end host needs all three
emulators (`ACCEPT_EULA=Y docker compose up -d`), and its live checkpoint is
opt-in and clearly costed.

## Supported versions and environments

- **Runtime:** .NET 10 (LTS), targeting `net10.0`. The SDK band is pinned in
  [`global.json`](global.json) (`10.0.100` with `rollForward: latestFeature`, so
  any `10.0.x` SDK at or above that feature band is used) and the
  target framework in [`Directory.Build.props`](Directory.Build.props).
- **Verified environment:** Linux. macOS and Windows via WSL are supported for
  the same workflow. The Learning Mentor discovery links are relative Git
  symlinks, so a native Windows checkout with `core.symlinks=false` is
  unsupported — use WSL.
- **Local-first:** ordinary lessons, exercises, and most tests run against
  official local emulators (Azurite, the Event Hubs emulator, and the Cosmos DB
  Linux emulator) or deterministic fakes. See [`compose.yaml`](compose.yaml).
- **Live Azure checkpoints:** clearly marked units use a real Azure subscription
  to teach Entra ID, RBAC, resource management, service limits, diagnostics, and
  emulator/cloud differences. Live work incurs cost — see
  [`docs/COST-AND-CLEANUP.md`](docs/COST-AND-CLEANUP.md).

## Required tools

.NET 10 SDK, Git, Docker with Compose, the Azure CLI, and PowerShell 7 with the
Az modules; an Azure subscription for the live checkpoints. Python 3.11+ is
required only for the optional Learning Mentor. Full setup and troubleshooting is
in [`docs/SETUP.md`](docs/SETUP.md).

## Scope and non-goals

**In scope:** Blob, Queue, and Table Storage; Event Hubs; Cosmos DB for NoSQL;
the Azure SDK for .NET; `DefaultAzureCredential` and Entra ID; and paired Azure
CLI and Azure PowerShell management workflows.

**Non-goals:** Bicep, Terraform, and portal-only workflows; Azure Functions,
Service Bus, Data Lake, Files, and the MongoDB/Cassandra/Gremlin/Table Cosmos
APIs; and production platform engineering. Account keys and SAS are taught only
as bounded interoperability and delegation mechanisms, not as the production
default.

## Repository layout

```text
README.md                     # this entry point
global.json                   # pinned .NET 10 SDK band
Directory.Build.props         # central build quality gate (net10.0, nullable, warnings-as-errors)
Directory.Packages.props      # central package management
LearningAzure.slnx            # course solution (companions, exercises, tooling)
compose.yaml                  # local emulators (Azurite, Event Hubs, Cosmos DB)
lessons/<NN>-<slug>/          # module narrative README plus its runnable companion
exercises/<NN>-<slug>/        # starter, solution, and the shared xUnit evaluator
support/AzureFakes/           # shared offline test doubles for Azure SDK clients
projects/<slug>/              # applied projects: guide, starter, solution, evaluator
infra/azure-cli/              # per-module Azure CLI management labs
infra/powershell/             # the behaviorally equivalent Azure PowerShell labs
infra/local/                  # emulator configuration
docs/                         # SETUP, QUALITY, COST-AND-CLEANUP
docs/architecture/            # curriculum graph, plan schema, evidence matrix
tools/CourseVerifier/         # curriculum plan verifier and its tests
.learning-mentor/             # pinned Learning Mentor submodule
.learning-mentor.toml         # course integration descriptor
.agents/skills/azure-learning-path/   # course-owned manifest, adapter, and skill
capstones/<slug>/             # the end-to-end capstone: guide, starter, solution, evaluator
```

## Optional: Learning Mentor

The repository ships an optional interactive mentor that tracks which objectives
you have practiced, schedules reviews, and keeps reference solutions out of sight
until you have made a genuine attempt. It is entirely optional and works in
GitHub Copilot CLI, OpenAI Codex, and Claude Code. See
[section 8 of `docs/SETUP.md`](docs/SETUP.md#8-optional-learning-mentor).

## Get started

1. Clone recursively (the course ships one submodule):
   `git clone --recurse-submodules <REPOSITORY_URL>`
2. Follow [`docs/SETUP.md`](docs/SETUP.md) to install the toolchain and start the
   local emulators.
3. Read [`docs/QUALITY.md`](docs/QUALITY.md) for the validation commands.
4. Start module 1: [`lessons/01-azure-data-map/README.md`](lessons/01-azure-data-map/README.md).
   It needs no cloud account and no emulator.
