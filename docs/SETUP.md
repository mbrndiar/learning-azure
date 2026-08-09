# 🛠️ Setting up your environment

This guide installs the toolchain, gets the code, and starts the local
emulators. The course is verified on Linux; macOS and Windows via WSL run the
same workflow.

## Recommended native setup with mise

The shortest reproducible native setup uses
[`mise`](https://mise.jdx.dev/installing-mise.html). After cloning the course in
section 2, run this from the repository root:

```bash
mise install
mise exec -- dotnet --version
mise exec -- az version
mise exec -- pwsh -NoProfile -Command '$PSVersionTable.PSVersion'
```

`mise.toml` declares the supported .NET, Azure CLI, PowerShell, and optional
Learning Mentor Python lines. `mise.lock` selects the exact versions validated
for this course. Shell activation makes them available directly; without it,
prefix commands with `mise exec --`.

The lock is intentionally not auto-updated whenever a patch appears. Maintainers
refresh it with `mise lock --bump`, inspect the resolution, and run the complete
course validation before committing it. Put personal overrides in the ignored
`mise.local.toml`, not in the shared config.

Mise does not install Git, Docker, the PowerShell Az modules, local emulators,
or an Azure subscription. Follow sections 3 and 4 for those prerequisites. If
you prefer operating-system or vendor installers, use the complete manual path
below instead.

## 1. Install the .NET 10 SDK

For the manual path, install the **.NET 10 SDK** from
<https://dotnet.microsoft.com/download/dotnet/10.0> and verify it:

```bash
dotnet --version
```

.NET 10 is the current Long-Term Support (LTS) release. This repository pins the
SDK band in [`global.json`](../global.json) with `rollForward: latestFeature`, so
any installed `10.0.x` SDK at or above the pinned feature band is used. The
target framework `net10.0` is set centrally in
[`Directory.Build.props`](../Directory.Build.props).

## 2. Get the code

The course ships one Git submodule, `.learning-mentor`, so clone recursively:

```bash
git clone --recurse-submodules <REPOSITORY_URL>
cd learning-azure
```

If you already cloned without submodules, repair the checkout with:

```bash
git submodule update --init --recursive
```

The submodule is only needed for the optional Learning Mentor described in
[section 8](#8-optional-learning-mentor); nothing in the Azure curriculum depends
on it. An uninitialized submodule makes the mentor fail closed with the repair
command above rather than silently guessing at your progress.

## 3. Install Docker and start the local emulators

Ordinary lessons and most tests run against local emulators, so install
[Docker](https://docs.docker.com/get-docker/) with the Compose plugin and verify:

```bash
docker --version
docker compose version
```

Validate the emulator configuration and start the stack from the repository root:

```bash
docker compose config
docker compose up -d
```

[`compose.yaml`](../compose.yaml) provides:

| service | emulates | endpoints |
| --- | --- | --- |
| `azurite` | Blob, Queue, Table Storage | `10000` / `10001` / `10002` |
| `eventhubs` | Event Hubs (AMQP + Kafka) | `5672` / `9092` |
| `cosmos` | Cosmos DB for NoSQL (gateway) | `8081` (gateway), `8080` (health), `1234` (Data Explorer) |

Starting the Event Hubs emulator requires accepting its EULA. Compose reads
`ACCEPT_EULA` from your shell or a local `.env` file — set `ACCEPT_EULA=Y` only
if you accept the
[Event Hubs emulator terms](https://github.com/Azure/azure-event-hubs-emulator-installer).
Emulators are for development and are **not** production-parity; their boundaries
are documented in [`docs/COST-AND-CLEANUP.md`](COST-AND-CLEANUP.md).

Stop and remove the stack with:

```bash
docker compose down
```

## 4. Install the Azure management tools

Live checkpoints use a real subscription and mirrored Azure CLI and Azure
PowerShell workflows. Install both:

- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
- [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell)
  with the [Az module](https://learn.microsoft.com/en-us/powershell/azure/install-azure-powershell)

Verify and sign in only when a unit reaches a live checkpoint:

```bash
az version
pwsh -Command "Get-Module -ListAvailable Az.Accounts | Select-Object Version"
```

Live sign-in, resource creation, and cost are covered per checkpoint and in
[`docs/COST-AND-CLEANUP.md`](COST-AND-CLEANUP.md). Never store cloud credentials
in the repository.

## 5. Choose an editor

- [Visual Studio Code](https://code.visualstudio.com/) with the
  [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit)
- [Visual Studio 2022+](https://visualstudio.microsoft.com/) or
  [JetBrains Rider](https://www.jetbrains.com/rider/)

Open the repository root — the directory containing `LearningAzure.slnx` — rather
than a single lesson subdirectory.

## 6. Essential commands

Run these from the repository root. See [`QUALITY.md`](QUALITY.md) for the full
validation gate.

```bash
dotnet restore                 # restore central-managed packages
dotnet build                   # build with warnings-as-errors
dotnet test                    # run tests
dotnet format --verify-no-changes   # verify formatting

# curriculum design gate
dotnet run --project tools/CourseVerifier/CourseVerifier -- verify
```

### Working through a module

```bash
# 1. read lessons/<NN>-<slug>/README.md, then run its companion
docker compose up -d azurite                                   # modules 3-7
ACCEPT_EULA=Y docker compose up -d eventhubs                   # modules 8-9
docker compose up -d cosmos                                    # modules 10-11
dotnet run --project lessons/04-blob-storage/ArtifactVault

# 2. run the paired management labs (modules 3-12)
bash infra/azure-cli/blob-storage.sh
pwsh infra/powershell/blob-storage.ps1

# 3. work the exercise; this fails until you fill the gaps
dotnet test exercises/04-blob-storage/tests -p:Implementation=starter

# 4. the same evaluator against the reference solution
dotnet test exercises/04-blob-storage/tests
```

A repository-wide `dotnet test` runs every evaluator against its reference
implementation and must be green; it is a health check, not your progress. Your
progress is the starter command in step 3 going from red to green.

### Working through the project or the capstone

```bash
# grade one milestone against your own tree
dotnet test capstones/cloud-expedition-journal/tests \
  -p:ImplementationRoot=capstones/cloud-expedition-journal/starter \
  --filter Milestone=domain-ports

# the capstone's end-to-end host needs all three emulators
ACCEPT_EULA=Y docker compose up -d
dotnet run --project capstones/cloud-expedition-journal/solution
```

`LearningAzure.slnx` contains the course verifier and its tests, the twelve built
lesson companions, the thirty-six exercise projects of modules 1-12, the three
Field Station projects, the three capstone projects, and the shared
`support/AzureFakes` doubles — every unit the curriculum designs and checks in
[`architecture/curriculum.md`](architecture/curriculum.md).

## 7. Supported environment

The course targets `net10.0` and is verified on Linux. macOS and Windows via WSL
run the same workflow. A native Windows checkout with `core.symlinks=false` turns
the Learning Mentor discovery links into plain-text files and is not supported —
use WSL.

## 8. Optional: Learning Mentor

The repository ships an optional interactive mentor that tracks which objectives
you have practiced, schedules reviews, and keeps reference solutions out of sight
until you have made a genuine attempt. Every lesson, exercise, project, and
capstone works without it.

### Python 3 is mentor tooling, not an Azure requirement

The mentor's shared state engine and this course's manifest adapter are written
in Python, so using the mentor requires **Python 3.11 or newer** on your `PATH`:

```bash
python3 --version
```

This is a requirement of the mentor tooling only. Nothing you write in this
course is Python — the course is C# on .NET 10 — and skipping the mentor removes
the requirement entirely.

### What is installed where

| path | role |
| --- | --- |
| `.learning-mentor/` | pinned submodule holding the shared agent, skill, and state engine |
| `.learning-mentor.toml` | this course's integration descriptor |
| `.agents/skills/azure-learning-path/` | course-owned map, manifest, and adapter |
| `.github/agents/`, `.claude/`, `.codex/` | thin discovery links for supported tools |

The discovery entries are relative Git symlinks. Linux, macOS, and WSL are the
supported environments.

### Verify the integration

```bash
python3 .agents/skills/azure-learning-path/scripts/course_adapter.py validate
```

A healthy course prints one JSON object containing `"status":"valid"` and exits
zero. Any manifest, path, command, or lock error exits nonzero with a diagnostic
on standard error and records nothing.

### Where your progress is stored

Progress lives outside this repository, in
`$XDG_DATA_HOME/learning-mentor/state.sqlite3` (falling back to
`~/.local/share/learning-mentor/state.sqlite3`). It is never committed, so
cloning fresh or resetting the working tree does not erase it, and pushing never
publishes it.

## Troubleshooting

### The installed .NET SDK is too old

Install a current .NET 10 SDK. `global.json` uses `rollForward: latestFeature`,
so any `10.0.x` SDK at or above the pinned band is accepted; older majors are
not.

### The submodule directory is empty

Run `git submodule update --init --recursive`. The mentor fails closed on a
missing submodule rather than guessing.

### Docker Compose cannot pull an emulator image

Check network and registry access to `mcr.microsoft.com`. Validate the file
without pulling using `docker compose config`.

### The Event Hubs emulator exits immediately

It requires `ACCEPT_EULA=Y` and a healthy `azurite` dependency. Confirm the
variable is set and that `azurite` reports healthy with `docker compose ps`.
