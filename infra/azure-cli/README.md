# ⌨️ `infra/azure-cli` — management labs

One script per module that has management work to do, plus one for the capstone. Each is a **step-by-step
lab**, not a provisioning tool: it prints what it is about to do, does it, and
shows you the answer the service gave. Read it before you run it.

Every script here has a behaviorally equivalent twin in
[`infra/powershell`](../powershell/). The pair is deliberate — you will meet both
in the field, and the differences between them are part of the lesson.

| script | module | environment |
| --- | --- | --- |
| `storage-account.sh` | [3 — Operate the shared storage boundary](../../lessons/03-storage-account/README.md) | **live Azure** (required checkpoint) |
| `blob-storage.sh` | [4 — Preserve expedition artifacts](../../lessons/04-blob-storage/README.md) | Azurite |
| `blob-lifecycle.sh` | [5 — Control artifact versions and deletion](../../lessons/05-blob-lifecycle/README.md) | **live Azure** (required checkpoint) |
| `queue-storage.sh` | [6 — Dispatch processing work](../../lessons/06-queue-storage/README.md) | Azurite |
| `table-storage.sh` | [7 — Index station observations](../../lessons/07-table-storage/README.md) | Azurite |
| `event-hubs-model.sh` | [8 — Stream expedition telemetry](../../lessons/08-event-hubs-model/README.md) | **live Azure** (required checkpoint) |
| `event-hubs-processing.sh` | [9 — Consume, checkpoint, and recover](../../lessons/09-event-hubs-processing/README.md) | **live Azure** (required checkpoint) |
| `cosmos-modeling.sh` | [10 — Design the global journal](../../lessons/10-cosmos-modeling/README.md) | **live Azure** (required checkpoint) |
| `cosmos-development.sh` | [11 — Query and update with C#](../../lessons/11-cosmos-development/README.md) | **live Azure** (required checkpoint) |
| `secure-operable-cloud.sh` | [12 — Prove the live architecture](../../lessons/12-secure-operable-cloud/README.md) | **live Azure** (required checkpoint) |
| `cloud-expedition-journal.sh` | [capstone — Cloud Expedition Field Journal](../../capstones/cloud-expedition-journal/README.md) | **live Azure** (opt-in milestone 5) |

## Running one

```bash
docker compose up -d azurite          # the Azurite labs only
bash infra/azure-cli/queue-storage.sh
```

The live labs additionally need `az login` and a subscription you are willing to
spend a few cents in. They are the only scripts here that create billable
resources, they say so before they do it, and **each one ends by deleting the
resource group it created.** If you interrupt one, run the teardown command it
printed at the start. See [`docs/COST-AND-CLEANUP.md`](../../docs/COST-AND-CLEANUP.md).

## Conventions

- `set -euo pipefail`, so the lab stops at the first failing step instead of
  reporting a cheerful lie.
- Every resource name is derived from one `PREFIX` variable at the top, so two
  learners in one subscription do not collide and teardown is a single command.
- Data-plane calls authenticate with `--auth-mode login` (your Entra identity)
  against live Azure. Account keys appear only against Azurite, where the
  well-known emulator credential is public by design.
- Steps are numbered and each one names the lesson section that explains it.

## What these labs are not

They are not infrastructure-as-code. Bicep, Terraform, and idempotent
provisioning are explicit non-goals of this course — these scripts teach the
management surface of a service, and a declarative template hides exactly the
thing being taught.
