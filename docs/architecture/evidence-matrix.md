# 🧭 Curriculum evidence matrix

<!--
  GENERATED FILE — do not edit by hand.
  Source: docs/architecture/curriculum.json
  Regenerate: dotnet run --project tools/CourseVerifier/CourseVerifier -- matrix --write
-->

Every promised outcome is classified against the quality contract's coverage
progression — **named → explained → demonstrated → practiced → applied**.

| status | meaning |
| --- | --- |
| `covered` | the cited artifact exists, resolves, and the evidence note states the behavior it demonstrates |
| `planned` | the artifact does not exist yet; the unit is not trackable |
| `deferred` | satisfied by a later unit that is not built yet |
| `partial` | some evidence exists, but the stage is incomplete and blocks completion |
| `missing` | required evidence is absent and blocks completion |
| `not-applicable` | the stage does not apply, with a recorded rationale |

A unit is registered for Learning Mentor tracking only once every stage has left
`planned`, `partial`, and `missing`, so this matrix cannot claim completion
before the required evidence exists.

## Summary

| measure | value |
| --- | --- |
| units | 14 |
| modules | 12 |
| applied projects | 1 |
| capstones | 1 |
| declared outcomes | 42 |
| milestones | 9 |
| units with artifacts present | 14 |
| evidence records `planned` | 0 |
| evidence records `deferred` | 0 |
| evidence records `covered` | 70 |
| evidence records `partial` | 0 |
| evidence records `missing` | 0 |
| evidence records `not-applicable` | 0 |

## Repository roles

| role | path | status |
| --- | --- | --- |
| `learner-entry-point` | `README.md` | `present` |
| `setup-and-troubleshooting` | `docs/SETUP.md` | `present` |
| `sequenced-instructional-units` | `lessons` | `present` |
| `practice-starter-and-solution` | `exercises` | `present` |
| `applied-projects-and-capstones` | `projects` | `present` |
| `reference-and-recall` | `docs/CHEATSHEET.md` | `planned` |
| `environment-manifests` | `compose.yaml` | `present` |
| `automated-validation` | `tools/CourseVerifier` | `present` |
| `learning-mentor-integration` | `.agents/skills/azure-learning-path` | `present` |

## `module.azure-data-map` — Choose the right Azure data primitive

- **kind:** module, sequence 1
- **artifacts:** `present`
- **environments:** local
- **prerequisites:** none

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.azure-data-map.select-primitive` | Choose the Azure data primitive that fits a stated expedition requirement and justify it against the adjacent service. | `exercise_tests` |
| `outcome.azure-data-map.compare-characteristics` | Compare durability, ordering, partitioning, replay, and cost characteristics across blob, queue, table, event stream, and document storage. | `exercise_tests` |
| `outcome.azure-data-map.apply-naming-discipline` | Apply resource-group scoping, safe naming, and teardown rules to a planned expedition deployment so every later unit stays reproducible. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Module objectives name each primitive and the selection criteria. |
| explained | `covered` | `lesson_readme` | Narrative derives the selection criteria from expedition requirements and includes a decision table plus authentic C# client-type fragments. |
| demonstrated | `covered` | `lesson_projects_root` | A runnable comparison companion prints the shape of each primitive against the same expedition record. |
| practiced | `covered` | `exercise_tests` | The shared evaluator scores selection decisions and their justifications against fixture requirements. |
| applied | `covered` | `project_starter` | The Field Station project requires an unaided primitive choice for each stage of its pipeline. |

## `module.azure-sdk-foundations` — Build a testable C# Azure client

- **kind:** module, sequence 2
- **artifacts:** `present`
- **environments:** local
- **prerequisites:** `module.azure-data-map`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.azure-sdk-foundations.build-testable-client` | Build an Azure SDK client behind an application-owned interface so behavior can be verified without a live service. | `exercise_tests` |
| `outcome.azure-sdk-foundations.configure-credentials` | Configure DefaultAzureCredential for live services and emulator credentials for local runs without placing secrets in source. | `exercise_tests` |
| `outcome.azure-sdk-foundations.diagnose-transients` | Diagnose transient failures using the SDK retry policy, cancellation tokens, and client diagnostics. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name credential chain, options, retries, cancellation, and diagnostics. |
| explained | `covered` | `lesson_readme` | Narrative explains why the SDK exposes these seams, with an annotated request/retry trace and excerpted client construction. |
| demonstrated | `covered` | `lesson_projects_root` | Companions show a retried call, an honored cancellation, and captured diagnostic output. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects swallowed cancellation, missing retry bounds, and untestable client construction. |
| applied | `covered` | `project_starter` | Every project adapter is written against the learner's own client seam. |

## `module.storage-account` — Operate the shared storage boundary

- **kind:** module, sequence 3
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `module.azure-sdk-foundations`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.storage-account.manage-lifecycle` | Create, inspect, configure, and delete a storage account with behaviorally equivalent Azure CLI and Azure PowerShell workflows. | `exercise_tests` |
| `outcome.storage-account.explain-boundaries` | Explain how endpoints, redundancy, access tiers, encryption, and the network and auth boundary constrain the services hosted in one account. | `exercise_tests` |
| `outcome.storage-account.compare-emulator-parity` | Compare Azurite behavior with a live storage account and record which differences change a design decision. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name account scope, endpoints, redundancy, tiers, encryption, and the auth boundary. |
| explained | `covered` | `lesson_readme` | Narrative explains why the account is the unit of billing, endpoint, and access control, with an account/endpoint diagram. |
| demonstrated | `covered` | `cli_lab` | Paired CLI and PowerShell labs create, inspect, reconfigure, and delete a real account with captured output. |
| practiced | `covered` | `exercise_tests` | The evaluator checks account-configuration reasoning and endpoint resolution against fixtures. |
| applied | `covered` | `project_starter` | The project run creates, uses, and tears down its own account-scoped resources. |

## `module.blob-storage` — Preserve expedition artifacts

- **kind:** module, sequence 4
- **artifacts:** `present`
- **environments:** emulator
- **prerequisites:** `module.storage-account`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.blob-storage.stream-artifacts` | Implement streaming upload and download of large expedition artifacts without buffering whole payloads in memory. | `exercise_tests` |
| `outcome.blob-storage.organize-artifacts` | Organize artifacts with containers, virtual directories, metadata, and tags, and list them with pagination and cancellation. | `exercise_tests` |
| `outcome.blob-storage.measure-transfer-cost` | Measure the memory and request cost of buffered versus streamed transfers and choose the appropriate transfer option. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name containers, blobs, streaming, metadata, tags, and listing. |
| explained | `covered` | `lesson_readme` | Narrative explains the flat namespace behind virtual directories, with an excerpted upload fragment and a captured listing observation. |
| demonstrated | `covered` | `lesson_projects_root` | Companions upload, stream back, tag, and page through artifacts against Azurite. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects buffered uploads, unpaged listing, and lost cancellation. |
| applied | `covered` | `project_starter` | The Field Station project stores every artifact through the learner's own blob adapter. |

## `module.blob-lifecycle` — Control artifact versions and deletion

- **kind:** module, sequence 5
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `module.blob-storage`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.blob-lifecycle.conditional-writes` | Implement conditional writes with ETag preconditions so two field uploads cannot silently overwrite one another. | `exercise_tests` |
| `outcome.blob-lifecycle.configure-retention` | Configure versioning, soft delete, and access-tier lifecycle rules that match the expedition's retention promise. | `exercise_tests` |
| `outcome.blob-lifecycle.diagnose-precondition-failures` | Diagnose precondition-failed and conflict responses and recover from them deterministically instead of retrying blindly. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name ETags, conditional headers, versioning, soft delete, and lifecycle rules. |
| explained | `covered` | `lesson_readme` | Narrative explains lost-update prevention with a state table of two concurrent writers and an excerpted conditional-write fragment. |
| demonstrated | `covered` | `lesson_projects_root` | A companion reproduces a precondition failure and its captured error response against Azurite. |
| practiced | `covered` | `exercise_tests` | The evaluator injects a competing writer and rejects unconditional overwrite and blind retry. |
| applied | `covered` | `project_starter` | The Field Station project must keep artifact updates safe under duplicate processing. |

## `module.queue-storage` — Dispatch processing work

- **kind:** module, sequence 6
- **artifacts:** `present`
- **environments:** emulator
- **prerequisites:** `module.storage-account`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.queue-storage.idempotent-consumer` | Implement a queue consumer that stays correct under at-least-once delivery and repeated redelivery of the same work order. | `exercise_tests` |
| `outcome.queue-storage.configure-delivery` | Configure visibility timeout, dequeue count, and poison-message routing so stuck work is quarantined rather than replayed forever. | `exercise_tests` |
| `outcome.queue-storage.compare-with-streams` | Compare competing-consumer work dispatch with a partitioned event stream and justify which one a given expedition workload needs. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name visibility timeout, dequeue count, at-least-once delivery, idempotency, and poison handling. |
| explained | `covered` | `lesson_readme` | Narrative explains message lifecycle with a state table from enqueue through invisibility, redelivery, and quarantine. |
| demonstrated | `covered` | `lesson_projects_root` | A companion forces a redelivery and shows the captured dequeue-count progression. |
| practiced | `covered` | `exercise_tests` | The evaluator replays a duplicate message and rejects handlers with observable double effects. |
| applied | `covered` | `project_starter` | The Field Station project dispatches all artifact processing through the learner's queue adapter. |

## `module.table-storage` — Index station observations

- **kind:** module, sequence 7
- **artifacts:** `present`
- **environments:** emulator
- **prerequisites:** `module.storage-account`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.table-storage.design-keys` | Design PartitionKey and RowKey values that turn the expedition's dominant lookups into point reads instead of scans. | `exercise_tests` |
| `outcome.table-storage.concurrent-updates` | Implement optimistic concurrency with entity ETags and group related writes into a transactional batch. | `exercise_tests` |
| `outcome.table-storage.measure-query-cost` | Measure the request cost difference between a point read, a partition scan, and a table scan on the same data set. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name partition and row keys, entity shape, filters, ETags, and batches. |
| explained | `covered` | `lesson_readme` | Narrative explains why key design decides cost, with a key-layout table and an excerpted entity definition. |
| demonstrated | `covered` | `lesson_projects_root` | A companion contrasts a point read with a scan and shows captured timing and request counts. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects scan-based lookups, stale-ETag writes, and cross-partition batches. |
| applied | `covered` | `project_starter` | The Field Station project tracks station status through the learner's table adapter. |

## `project.field-station` — Applied Storage field station

- **kind:** project, sequence 8
- **artifacts:** `present`
- **environments:** emulator
- **prerequisites:** `module.blob-lifecycle`, `module.queue-storage`, `module.table-storage`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.field-station.build-pipeline` | Build a worker that stores artifacts in Blob Storage, dispatches processing through Queue Storage, and tracks station status in Table Storage behind application-owned ports. | `project_tests` |
| `outcome.field-station.survive-duplicates` | Implement processing that stays correct across duplicate delivery, restart, and partial failure. | `project_tests` |
| `outcome.field-station.verify-locally` | Verify the whole flow deterministically against local emulators and fakes with no live Azure dependency. | `project_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `project_guide#objectives` | The project guide names the milestones and the Storage concepts each one applies. |
| explained | `covered` | `project_guide` | The guide explains the architecture and data flow without reteaching module material. |
| demonstrated | `covered` | `project_solution` | The reference implementation shows the taught approach end to end. |
| practiced | `covered` | `project_tests` | One shared contract suite judges the starter and the reference solution identically, including adversarial duplicate and stale-ETag fixtures. |
| applied | `covered` | `project_starter` | The learner implements every milestone unaided in the starter tree. |

| milestone | required outcome | depends on |
| --- | --- | --- |
| `milestone.field-station.domain-ports` | Define the artifact, work-order, and station-status contracts as application-owned interfaces with no SDK types leaking into the domain. | none |
| `milestone.field-station.artifact-storage` | Stream artifacts into a container and update them under an ETag precondition. | `milestone.field-station.domain-ports` |
| `milestone.field-station.work-dispatch` | Enqueue a work order per stored artifact and process it idempotently under redelivery. | `milestone.field-station.artifact-storage` |
| `milestone.field-station.status-index` | Record per-station processing status as point-readable entities with concurrency-safe updates. | `milestone.field-station.work-dispatch` |
| `milestone.field-station.failure-recovery` | Quarantine poison work, recover after restart with an idempotent or deduplicated effect, and remove every resource the run created. | `milestone.field-station.status-index` |

## `module.event-hubs-model` — Stream expedition telemetry

- **kind:** module, sequence 9
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `project.field-station`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.event-hubs-model.produce-batches` | Produce partitioned telemetry batches with an explicit partition-key strategy and bounded batch sizes. | `exercise_tests` |
| `outcome.event-hubs-model.compare-with-queues` | Compare an event stream with a work queue and justify retention, replay, and ordering tradeoffs for sensor telemetry. | `exercise_tests` |
| `outcome.event-hubs-model.explain-capacity` | Explain how namespace, hub, partition count, and throughput limits bound ingest, and what cannot be changed after creation. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name namespace, hub, partition, partition key, batch, retention, and throughput units. |
| explained | `covered` | `lesson_readme` | Narrative explains partition assignment with a diagram mapping station keys to partitions and an excerpted batch producer fragment. |
| demonstrated | `covered` | `lesson_projects_root` | A companion publishes keyed batches against the Event Hubs emulator with captured partition distribution. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects unkeyed publishing, unbounded batches, and ordering claims the service does not make. |
| applied | `covered` | `capstone_starter` | The capstone requires an independently chosen partition-key strategy for live telemetry. Resolved in capstone.cloud-expedition-journal#milestone-1-the-domain-and-the-ports. |

## `module.event-hubs-processing` — Consume, checkpoint, and recover

- **kind:** module, sequence 10
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `module.event-hubs-model`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.event-hubs-processing.consume-with-checkpoints` | Consume events with a processor client across consumer groups and persist checkpoints in Blob Storage. | `exercise_tests` |
| `outcome.event-hubs-processing.recover-from-failure` | Implement recovery from restart, partition rebalance, and duplicate delivery without losing or double-applying events. | `exercise_tests` |
| `outcome.event-hubs-processing.diagnose-lag` | Diagnose ownership churn, consumer lag, and checkpoint failures from processor diagnostics rather than from guesswork. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name consumer groups, ownership, checkpoints, replay, and rebalancing. |
| explained | `covered` | `lesson_readme` | Narrative explains checkpoint semantics with an annotated offset trace across a restart. |
| demonstrated | `covered` | `lesson_projects_root` | A companion restarts a processor mid-stream and shows the captured replay window. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects checkpointing before successful handling, swallowed cancellation, and non-idempotent handlers. |
| applied | `covered` | `capstone_starter` | The capstone consumes live telemetry with the learner's own checkpointing strategy. Resolved in capstone.cloud-expedition-journal#milestone-3-the-telemetry-pipeline. |

## `module.cosmos-modeling` — Design the global journal

- **kind:** module, sequence 11
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `project.field-station`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.cosmos-modeling.design-partition-key` | Design a container partition key and item shape that keep the journal's dominant queries single-partition. | `exercise_tests` |
| `outcome.cosmos-modeling.measure-request-units` | Measure the request-unit cost of representative reads, queries, and writes and size throughput from that evidence. | `exercise_tests` |
| `outcome.cosmos-modeling.compare-with-tables` | Compare Cosmos DB for NoSQL with Table Storage on query capability, cost model, and distribution, and justify which the journal needs. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name containers, items, partition keys, request units, consistency, and indexing. |
| explained | `covered` | `lesson_readme` | Narrative explains logical versus physical partitions with a diagram and an excerpted item model. |
| demonstrated | `covered` | `lesson_projects_root` | A companion runs the same query under two key designs and shows captured request-unit charges. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects cross-partition fan-out for the dominant query and unjustified consistency downgrades. |
| applied | `covered` | `capstone_starter` | The capstone journal projection is modeled by the learner without a prescribed key. Resolved in capstone.cloud-expedition-journal#milestone-4-the-journal-projection. |

## `module.cosmos-development` — Query and update with C#

- **kind:** module, sequence 12
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `module.cosmos-modeling`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.cosmos-development.implement-data-access` | Implement point reads, parameterized queries, pagination, patch, and transactional batch against a Cosmos container. | `exercise_tests` |
| `outcome.cosmos-development.handle-throttling` | Handle throttling responses and ETag conflicts deterministically instead of hiding them behind unbounded retries. | `exercise_tests` |
| `outcome.cosmos-development.diagnose-query-cost` | Diagnose query cost and index usage from Cosmos diagnostics and reduce the charge of an expensive query. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name client lifetime, point reads, queries, pagination, patch, batch, and throttling. |
| explained | `covered` | `lesson_readme` | Narrative explains why a single long-lived client and parameterized queries matter, with excerpted query code. |
| demonstrated | `covered` | `lesson_projects_root` | A companion pages a query and shows the captured continuation and request charge per page. |
| practiced | `covered` | `exercise_tests` | The evaluator rejects string-concatenated queries, per-call client construction, and swallowed throttling. |
| applied | `covered` | `capstone_starter` | The capstone projection and query surface are written unaided. Resolved in capstone.cloud-expedition-journal#milestone-4-the-journal-projection. |

## `module.secure-operable-cloud` — Prove the live architecture

- **kind:** module, sequence 13
- **artifacts:** `present`
- **environments:** live-checkpoint
- **prerequisites:** `module.event-hubs-processing`, `module.cosmos-development`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.secure-operable-cloud.assign-least-privilege` | Assign least-privilege Entra ID roles for every service the expedition uses and prove that a removed role breaks exactly the expected call. | `exercise_tests` |
| `outcome.secure-operable-cloud.verify-identity-boundary` | Verify how DefaultAzureCredential resolves in local, developer, and managed-identity contexts and where the emulator boundary ends. | `exercise_tests` |
| `outcome.secure-operable-cloud.prove-cleanup` | Measure the cost shape of a live run and prove complete teardown with behaviorally equivalent Azure CLI and Azure PowerShell cleanup. | `exercise_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `lesson_readme#objectives` | Objectives name Entra roles, managed identity, diagnostics, cost controls, and teardown. |
| explained | `covered` | `lesson_readme` | Narrative explains the credential chain and role-assignment scope with a resolution diagram. |
| demonstrated | `covered` | `cli_lab` | Paired labs assign, test, and revoke a role and show the captured authorization failure. |
| practiced | `covered` | `exercise_tests` | The evaluator checks role scoping, preflight fail-closed behavior, and post-cleanup verification. |
| applied | `covered` | `capstone_starter` | The capstone applies identity, diagnostics, and cleanup through offline-graded adapters; an actual live run is an optional extension. |

## `capstone.cloud-expedition-journal` — Cloud Expedition Field Journal

- **kind:** capstone, sequence 14
- **artifacts:** `present`
- **environments:** emulator, live-checkpoint
- **prerequisites:** `module.secure-operable-cloud`

| outcome | statement | measured by |
| --- | --- | --- |
| `outcome.cloud-expedition-journal.build-end-to-end` | Build the end-to-end journal across Event Hubs, Blob, Queue, Table, and Cosmos DB behind application-owned ports. | `capstone_tests` |
| `outcome.cloud-expedition-journal.verify-failure-behavior` | Verify normal, boundary, and failure behavior — duplicates, restarts, throttling, and poison work — with deterministic local tests. | `capstone_tests` |
| `outcome.cloud-expedition-journal.verify-operational-boundary` | Verify the journal's identity, retry, diagnostics, and cleanup boundary with deterministic tests. | `capstone_tests` |

| stage | status | evidence | note |
| --- | --- | --- | --- |
| named | `covered` | `capstone_guide#objectives` | The capstone guide names each milestone and the services it integrates. |
| explained | `covered` | `capstone_guide#architecture` | The guide explains the target architecture, data flow, and acceptance criteria without reteaching modules. |
| demonstrated | `covered` | `capstone_solution` | The reference implementation runs end to end against the local service emulators; the live Azure extension is optional. |
| practiced | `covered` | `capstone_tests` | One shared acceptance suite judges the starter and the reference solution identically across normal, boundary, and failure cases. |
| applied | `covered` | `capstone_starter` | The learner completes every milestone unaided in the starter tree. |

| milestone | required outcome | depends on |
| --- | --- | --- |
| `milestone.cloud-expedition-journal.domain-ports` | Define telemetry, artifact, station, and journal-entry contracts independent of any Azure SDK type. | none |
| `milestone.cloud-expedition-journal.storage-workflow` | Store reports in Blob Storage under preconditions, queue artifact work, and track station state in Table Storage. | `milestone.cloud-expedition-journal.domain-ports` |
| `milestone.cloud-expedition-journal.telemetry-pipeline` | Produce keyed telemetry batches and consume them with Blob checkpointing that survives restart and duplicates. | `milestone.cloud-expedition-journal.storage-workflow` |
| `milestone.cloud-expedition-journal.projection-and-operations` | Project telemetry into a single-partition query model and implement the identity, retry, diagnostics, and cleanup boundary. | `milestone.cloud-expedition-journal.telemetry-pipeline` |

