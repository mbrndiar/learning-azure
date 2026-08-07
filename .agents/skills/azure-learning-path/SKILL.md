---
name: azure-learning-path
description: Course-owned map of the learning-azure curriculum — what to read, what to run, how each unit is graded, and which reference paths stay locked until a genuine attempt has been made.
---

# Azure learning path

This skill is the **course-owned** half of the Learning Mentor integration. It
describes discovery, native .NET commands, diagnostics, and milestone routing for
`learning-azure`. It contains no teaching policy: the shared
[`guided-learning`](../guided-learning/SKILL.md) skill owns pedagogy, evidence,
state, and review behavior.

The machine-readable authority for semantic IDs, prerequisites, outcomes,
learner and reference paths, commands, and solution locks is
[`course.toml`](course.toml). Its contract is
[`references/course-schema.md`](references/course-schema.md). Never re-derive the
curriculum graph from directory order — read the manifest through the adapter.

## Build status

`learning-azure` is complete. `course.toml` registers modules 1 through 12 — from
choosing a data primitive, through Storage, Event Hubs, and Cosmos DB for NoSQL,
to securing and operating the live architecture — each with its narrative,
runnable companion, starter, reference solution, and shared evaluator, plus
`project.field-station`, the applied project that puts all three Storage services
together across five milestones, and
`capstone.cloud-expedition-journal`, the required final destination that
integrates all five services across five more.

The whole curriculum — 12 modules, the Field Station project, and the
Cloud Expedition Field Journal capstone, with their prerequisite graph, outcomes,
and evidence records — is designed and reviewable in
[`docs/architecture/curriculum.md`](../../../docs/architecture/curriculum.md),
backed by `docs/architecture/curriculum.json` and checked by
`tools/CourseVerifier`.

**A unit becomes trackable only when its content exists.** The adapter refuses to
register a unit the plan still marks as planned, and the verifier fails if content
lands without a plan update. Treat anything not registered in `course.toml` as
not yet taught: do not route a learner to it, and do not record progress for it.

## Adapter

Run every command from the repository root.

```bash
python3 .agents/skills/azure-learning-path/scripts/course_adapter.py validate
python3 .agents/skills/azure-learning-path/scripts/course_adapter.py state-projection
```

`validate` prints one compact JSON object containing `"status":"valid"` and
exits zero, or writes a categorized diagnostic to standard error and exits
nonzero. `state-projection` prints the neutral acyclic graph the shared state
helper consumes. Parse standard output only after exit status zero.

Python 3.11 or newer is **Learning Mentor infrastructure**, not a requirement of
the Azure course. Nothing a learner writes in this repository uses Python; the
course is C# on .NET 10.

## Course shape

| role | path |
| --- | --- |
| learner entry point | `README.md` |
| setup and troubleshooting | `docs/SETUP.md` |
| validation gates | `docs/QUALITY.md` |
| cost awareness and cleanup | `docs/COST-AND-CLEANUP.md` |
| curriculum design and evidence | `docs/architecture/` |
| local emulators | `compose.yaml`, `infra/local/` |
| solution and central build config | `LearningAzure.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` |
| module narratives | `lessons/<NN>-<slug>/README.md` — *modules 1-12 built* |
| runnable companions | `lessons/<NN>-<slug>/<companion>/` — *modules 1-12 built* |
| practice, references | `exercises/<NN>-<slug>/{starter,solution,tests}` — *modules 1-12 built* |
| paired management labs | `infra/azure-cli/<slug>.sh`, `infra/powershell/<slug>.ps1` — *modules 3-12 built* |
| shared offline test doubles | `support/AzureFakes/` |
| required project | `projects/field-station/{README.md,starter,solution,tests}` — *built* |
| capstone | `capstones/cloud-expedition-journal/{README.md,starter,solution,tests}` — *built* |

Every path above exists. The curriculum plan records the same set and the
verifier fails if the two disagree in either direction.

Each module README is the primary narrative and its lesson project is the
runnable companion; a concept's `run_command` in the manifest is the exact way to
observe it.

## Module and milestone commands

Module exercises and project/capstone milestones are graded by focused `dotnet`
commands declared in `course.toml` and documented verbatim in this file. The
untouched starter's baseline (`fails` or `passes`) is declared per unit in the
manifest so a focused command's exit status is never read out of context.

Every module and milestone below declares `untouched_starter_result = "fails"`:
the starter trees raise `NotImplementedException` at each numbered gap, so a green
run is real evidence rather than a vacuous scaffold pass.

| unit | validation command | companion |
| --- | --- | --- |
| `module.azure-data-map` | `dotnet test exercises/01-azure-data-map/tests` | `dotnet run --project lessons/01-azure-data-map/PrimitiveTour` |
| `module.azure-sdk-foundations` | `dotnet test exercises/02-azure-sdk-foundations/tests` | `dotnet run --project lessons/02-azure-sdk-foundations/ClientSeams` |
| `module.storage-account` | `dotnet test exercises/03-storage-account/tests` | `dotnet run --project lessons/03-storage-account/AccountBoundary` |
| `module.blob-storage` | `dotnet test exercises/04-blob-storage/tests` | `dotnet run --project lessons/04-blob-storage/ArtifactVault` |
| `module.blob-lifecycle` | `dotnet test exercises/05-blob-lifecycle/tests` | `dotnet run --project lessons/05-blob-lifecycle/PreconditionArena` |
| `module.queue-storage` | `dotnet test exercises/06-queue-storage/tests` | `dotnet run --project lessons/06-queue-storage/DispatchYard` |
| `module.table-storage` | `dotnet test exercises/07-table-storage/tests` | `dotnet run --project lessons/07-table-storage/ObservationIndex` |
| `module.event-hubs-model` | `dotnet test exercises/08-event-hubs-model/tests` | `dotnet run --project lessons/08-event-hubs-model/TelemetryStream` |
| `module.event-hubs-processing` | `dotnet test exercises/09-event-hubs-processing/tests` | `dotnet run --project lessons/09-event-hubs-processing/CheckpointYard` |
| `module.cosmos-modeling` | `dotnet test exercises/10-cosmos-modeling/tests` | `dotnet run --project lessons/10-cosmos-modeling/RequestUnits` |
| `module.cosmos-development` | `dotnet test exercises/11-cosmos-development/tests` | `dotnet run --project lessons/11-cosmos-development/DataPlane` |
| `module.secure-operable-cloud` | `dotnet test exercises/12-secure-operable-cloud/tests` | `dotnet run --project lessons/12-secure-operable-cloud/AccessBoundary` |

### `project.field-station`

The project is graded milestone by milestone, in ordinal order. Unlike the
modules, its commands name the learner tree explicitly, because the project
selects an implementation by repository-relative path rather than by the
`Implementation` shorthand:

| milestone | test command |
| --- | --- |
| `milestone.field-station.domain-ports` | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=domain-ports` |
| `milestone.field-station.artifact-storage` | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=artifact-storage` |
| `milestone.field-station.work-dispatch` | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=work-dispatch` |
| `milestone.field-station.status-index` | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=status-index` |
| `milestone.field-station.failure-recovery` | `dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter --filter Milestone=failure-recovery` |

The whole project is validated with
`dotnet test projects/field-station/tests -p:ImplementationRoot=projects/field-station/starter`,
which fails 63 of 85 tests on an untouched starter. Dropping the property runs the
same evaluator against the reference solution. The project's end-to-end run needs
Azurite: `docker compose up -d azurite`.

### `capstone.cloud-expedition-journal`

The capstone is graded the same way, by milestone in ordinal order, against the
learner tree named by path:

| milestone | test command |
| --- | --- |
| `milestone.cloud-expedition-journal.domain-ports` | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=domain-ports` |
| `milestone.cloud-expedition-journal.storage-workflow` | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=storage-workflow` |
| `milestone.cloud-expedition-journal.telemetry-pipeline` | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=telemetry-pipeline` |
| `milestone.cloud-expedition-journal.cosmos-projection` | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=cosmos-projection` |
| `milestone.cloud-expedition-journal.live-operations` | `dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter --filter Milestone=live-operations` |

The whole capstone is validated with
`dotnet test capstones/cloud-expedition-journal/tests -p:ImplementationRoot=capstones/cloud-expedition-journal/starter`,
which fails 84 of 112 tests on an untouched starter. The evaluator itself is
offline. The capstone's end-to-end run needs all three emulators
(`ACCEPT_EULA=Y docker compose up -d`), and its live checkpoint is opt-in and
costs money, so never run it without an explicit request.

A validation command runs the evaluator against the **reference** implementation
by default. The learner's own work is graded by adding the implementation
selector, for example `dotnet test exercises/04-blob-storage/tests -p:Implementation=starter`,
which is the command that must go from red to green.

Companions need emulators from module 3 onwards: modules 3-7 need Azurite
(`docker compose up -d azurite`); module 8 needs the Event Hubs emulator and
module 9 needs it alongside Azurite for its checkpoint store
(`ACCEPT_EULA=Y docker compose up -d eventhubs`, which starts Azurite as a
dependency); modules 10 and 11 need the Cosmos DB emulator
(`docker compose up -d cosmos`). Modules 8 through 11 each carry a **required
live checkpoint** driven by their management labs — those cost money and create
real Azure resources, so never run them on a learner's behalf without an
explicit request.

## Diagnostics

| symptom | reading |
| --- | --- |
| adapter exits nonzero | course state is unavailable; repair the manifest before recording progress |
| `unsupported schema_version` / `manifest_version` | the manifest and adapter protocol disagree; do not record progress |
| `... does not exist` | a declared path is missing; the manifest and repository disagree |
| `target_framework must be net10.0` | the manifest and `Directory.Build.props` disagree about the .NET target |
| `... is not declared in the curriculum plan` | a unit was registered without being designed; fix the plan first |
| `... still marks its artifacts as planned` | a unit was registered before its content exists; do not record progress |

## Solution locks

`course.toml` names one lock group per built module, project, and capstone.
A locked reference path must not be read, searched, executed, or summarized
before its unlock condition is met: module references unlock after unit
validation, project and capstone references after the matching milestone
validation, and both only once the recorded attempt count reaches the group's
`solution_unlock_after`. Learner starter trees and module narratives are never
locked.

One group exists per built module — `solutions.azure-data-map` through
`solutions.secure-operable-cloud` — and each covers exactly that module's
`exercises/<NN>-<slug>/solution` and `exercises/<NN>-<slug>/tests` trees. The
evaluator is locked alongside the reference because reading the assertions gives
away the same answers the solution does.
