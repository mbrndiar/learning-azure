# `infra/local` — emulator configuration

Configuration consumed by the local emulator stack in [`compose.yaml`](../../compose.yaml).
These files describe **local, free, non-parity** approximations of Azure
services used while working through the course offline. They never provision
anything in a real Azure subscription.

## Contents

| Path | Emulator | Purpose |
| --- | --- | --- |
| `eventhubs/config.json` | Azure Event Hubs emulator | Declares the namespace, event hub, partition count, and consumer group the emulator creates at startup. Mounted read-only at `/Eventhubs_Emulator/ConfigFiles/Config.json`. |

The Cosmos DB Linux emulator and Azurite take their whole configuration from
[`compose.yaml`](../../compose.yaml) and need no file here.

## Azurite and the built modules

Modules 3 through 7 run against Azurite, which needs no configuration file.
Module 9 needs it too: the Event Hubs processor keeps its checkpoints and its
partition ownership in Blob Storage, and the Event Hubs emulator itself stores
its metadata there, which is why Compose starts Azurite before it.

```bash
docker compose up -d azurite
```

Every lesson companion and management lab for those modules connects with the
well-known emulator development connection string. That credential is public,
documented by Microsoft, identical in every Azurite installation, and grants
access to nothing outside the container — which is exactly why it is the only
place in this course where a key appears in a command instead of
`DefaultAzureCredential`.

Each lesson records where Azurite **diverges** from the service, because a lesson
that teaches emulator behavior as Azure behavior is teaching a bug. Two examples
found while writing these modules: Azurite reports no blob `VersionId` and no
delete-retention policy, and it accepts a cross-partition table transaction that
Azure rejects with `InvalidInput`.

### `eventhubs/config.json`

Seeds one event hub (`telemetry`, 4 partitions) with one consumer group
(`field-journal`) — the ingestion path used by the Field Journal expedition
telemetry theme. The emulator also auto-adds `$default` to every entity.

The namespace name is **`emulatorNs1`, and it cannot be anything else.** The
emulator supports a single namespace whose name is non-modifiable; any other
value is logged as a recoverable validation warning and silently replaced, which
is why this file uses the required name rather than an expedition-themed one.
Module 8 documents the divergence, because a config file the service quietly
overrides is a config file you have stopped reading.

Hub names, partition counts, and consumer groups here *are* honoured. The
emulator reads the file only at container start, so restart the `eventhubs`
service after edits:

```bash
ACCEPT_EULA=Y docker compose restart eventhubs
docker compose logs eventhubs | tail -n 20
```

## Live infrastructure

The **required live checkpoints** in modules 3, 5, and 8 through 11 are driven by
the paired management labs in [`infra/azure-cli`](../azure-cli/) and
[`infra/powershell`](../powershell/) rather than by infrastructure-as-code;
declarative IaC is an explicit non-goal of this course.
Costs, teardown, and the local-vs-live boundary are documented in
[`docs/COST-AND-CLEANUP.md`](../../docs/COST-AND-CLEANUP.md).
