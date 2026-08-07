# `infra/powershell` — management labs

The Azure PowerShell twin of [`infra/azure-cli`](../azure-cli/). Each script does
behaviorally equivalent work to the `.sh` file of the same name, step for step,
so you can compare the two surfaces on identical tasks.

| script | module | environment |
| --- | --- | --- |
| `storage-account.ps1` | [3 — Operate the shared storage boundary](../../lessons/03-storage-account/README.md) | **live Azure** (required checkpoint) |
| `blob-storage.ps1` | [4 — Preserve expedition artifacts](../../lessons/04-blob-storage/README.md) | Azurite |
| `blob-lifecycle.ps1` | [5 — Control artifact versions and deletion](../../lessons/05-blob-lifecycle/README.md) | **live Azure** (required checkpoint) |
| `queue-storage.ps1` | [6 — Dispatch processing work](../../lessons/06-queue-storage/README.md) | Azurite |
| `table-storage.ps1` | [7 — Index station observations](../../lessons/07-table-storage/README.md) | Azurite |
| `event-hubs-model.ps1` | [8 — Stream expedition telemetry](../../lessons/08-event-hubs-model/README.md) | **live Azure** (required checkpoint) |
| `event-hubs-processing.ps1` | [9 — Consume, checkpoint, and recover](../../lessons/09-event-hubs-processing/README.md) | **live Azure** (required checkpoint) |
| `cosmos-modeling.ps1` | [10 — Design the global journal](../../lessons/10-cosmos-modeling/README.md) | **live Azure** (required checkpoint) |
| `cosmos-development.ps1` | [11 — Query and update with C#](../../lessons/11-cosmos-development/README.md) | **live Azure** (required checkpoint) |
| `secure-operable-cloud.ps1` | [12 — Prove the live architecture](../../lessons/12-secure-operable-cloud/README.md) | **live Azure** (required checkpoint) |
| `cloud-expedition-journal.ps1` | [capstone — Cloud Expedition Field Journal](../../capstones/cloud-expedition-journal/README.md) | **live Azure** (opt-in milestone 5) |

## Running one

```bash
docker compose up -d azurite               # the Azurite labs only
pwsh infra/powershell/queue-storage.ps1
```

PowerShell 7 and the `Az` modules are required; installation is in
[`docs/SETUP.md`](../../docs/SETUP.md#4-install-the-azure-management-tools). The
live labs additionally need `Connect-AzAccount` and end by removing the resource
group they created.

## Why both surfaces

The `az` CLI returns JSON that you slice with `--query` and a JMESPath
expression. Azure PowerShell returns **objects** that you slice with the
pipeline and property access. The same task therefore reads very differently, and
which one is pleasant depends on what you are doing:

| task | reads better in |
| --- | --- |
| one value out of one resource, inside a shell script | `az ... --query` |
| filtering, sorting, or joining across many resources | PowerShell pipeline |
| structured input (a lifecycle policy, a batch of entities) | PowerShell objects |
| copy-pasting from Microsoft Learn | usually `az` |

## Conventions

- `#Requires -Version 7.0` and `$ErrorActionPreference = 'Stop'`, so a failed
  step stops the lab.
- Progress is written with `Write-Information` and `$InformationPreference =
  'Continue'`, never `Write-Host`, so output stays redirectable.
- Files are ASCII with no BOM and pass PSScriptAnalyzer with no Error or Warning
  diagnostics:

  ```bash
  pwsh -NoProfile -Command "Invoke-ScriptAnalyzer -Path infra/powershell -Recurse -Severity Error,Warning"
  ```

- Helper functions that only shape data are named with approved read-only verbs
  (`ConvertTo-*`, `Get-*`), because a `New-*` helper that does not change state
  is a lie PSScriptAnalyzer will correctly complain about.
