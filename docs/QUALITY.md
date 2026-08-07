# ✅ Quality and validation

This course enforces one coherent quality gate locally and in continuous
integration. Local and CI behavior use the same underlying configuration and must
not disagree about success criteria.

> **Status.** The solution contains the course verifier and its tests, the twelve
> built lesson companions, the thirty-six exercise projects of modules 1-12, the
> three Field Station projects (starter, reference solution, and shared
> evaluator), and the three Cloud Expedition Journal capstone projects, on top of
> the shared `support/AzureFakes` doubles. Every project inherits the same central
> configuration
> ([`Directory.Build.props`](../Directory.Build.props),
> [`Directory.Packages.props`](../Directory.Packages.props),
> [`global.json`](../global.json)). The curriculum-plan, Learning Mentor, build,
> test, and formatting gates are all active now, locally and in CI.

## Build configuration

[`Directory.Build.props`](../Directory.Build.props) applies to every project:

| setting | value | why |
| --- | --- | --- |
| `TargetFramework` | `net10.0` | single supported runtime |
| `Nullable` | `enable` | null-safety is taught and enforced |
| `ImplicitUsings` | `enable` | idiomatic .NET 10 |
| `TreatWarningsAsErrors` | `true` | warnings are defects, not noise |
| `EnableNETAnalyzers` + `AnalysisLevel` | `latest-recommended` | static analysis in the build |
| `Deterministic` | `true` | reproducible builds |

Package versions are managed centrally through
[`Directory.Packages.props`](../Directory.Packages.props); projects reference
packages without a `Version` attribute. Every version there is an exact pin, not
a range or a floating version, and transitive pinning is enabled
(`CentralPackageTransitivePinningEnabled`), so a restore resolves the same graph
on every machine without a per-project lock file.

### Where a build writes, and why it matters

By default a project builds into the conventional `bin/Debug/net10.0` and
`obj/Debug/net10.0`. Exercise, project, and capstone evaluators, however, choose
which implementation they grade through the `Implementation` (or
`ImplementationRoot`) property:

```bash
dotnet test exercises/04-blob-storage/tests -p:Implementation=starter
```

Those are MSBuild global properties, so they reach
[`Directory.Build.props`](../Directory.Build.props) and every referenced project.
When one is set, the build output is slotted into `bin/impl-<slot>/` and
`obj/impl-<slot>/` rather than the default location. Without that slotting, a
starter run and a solution run of the same evaluator overwrite each other's
output, and a later incremental run that skips the build grades whichever tree
happened to be compiled last. The starter-red gate and the solution-green gate
can therefore be run in any order, and in parallel, and each still grades the
implementation it names.

## Local validation commands

Run from the repository root:

```bash
dotnet format --verify-no-changes    # formatting and code style
dotnet build                         # compile with warnings-as-errors
dotnet test                          # unit, contract, and evaluator tests
```

A repository-wide `dotnet test` runs every evaluator against its **reference**
implementation, so it is a health signal rather than an expected failure. Every
test in it is deterministic and offline: evaluators drive real Azure SDK clients
over scripted `HttpMessageHandler` doubles or application-owned ports, never over
a socket. Emulator and live work lives in the lesson companions and the
`infra/azure-cli` and `infra/powershell` labs, which CI does not run.

Coverage is collected with the .NET test SDK's built-in data collector:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

### Learner starters are expected to fail

Each exercise declares an untouched-starter baseline in `course.toml`; every
built module declares `"fails"`. A focused starter command

```bash
dotnet test exercises/04-blob-storage/tests -p:Implementation=starter
```

is expected to **fail** at the first intended gap; that failure is the learner's
next action, not a repository defect. Repository health checks compile starters
and separately run every completed lesson, solution, project, capstone, and
evaluator — they never run raw starter tests as a pass/fail health signal.

### Evaluators are proven, not assumed

A passing evaluator only proves the reference implementation satisfies it. Each
built module's lesson therefore carries a **"How this evaluator is known to be
strong"** section recording real mutations applied to the reference solution, the
exact assertions that caught each one, and the observed failure output. A
plausible wrong implementation that no test rejects is a defect in the evaluator,
not a feature of the solution.

## Curriculum plan gate

The curriculum design is machine-checked, not merely written down:

```bash
dotnet run --project tools/CourseVerifier/CourseVerifier -- verify
dotnet run --project tools/CourseVerifier/CourseVerifier -- matrix --write
```

`verify` fails when the prerequisite graph is cyclic, redundant, or out of
sequence; when an outcome is not measurable; when a unit claims evidence for an
artifact that does not exist; when content lands without a plan update; when the
Learning Mentor manifest registers a unit whose artifacts do not exist; or when
[`docs/architecture/evidence-matrix.md`](architecture/evidence-matrix.md) is
stale.

Its own tests prove each of those rejections against fixtures, so the gate is
known to fail closed before there is any content to check:

```bash
dotnet test tools/CourseVerifier/CourseVerifier.Tests
```

## Learning Mentor adapter gate (active now)

The course-owned adapter must validate before the mentor records any progress:

```bash
python3 .agents/skills/azure-learning-path/scripts/course_adapter.py validate
python3 .agents/skills/azure-learning-path/scripts/course_adapter.py state-projection
```

`validate` prints `"status":"valid"` and exits zero, or writes a categorized
diagnostic to standard error and exits nonzero. The adapter's own tests prove it
fails closed on malformed manifests:

```bash
python3 -m unittest discover -s .agents/skills/azure-learning-path/tests -v
```

## Emulator configuration check

```bash
docker compose config
```

This parses [`compose.yaml`](../compose.yaml) and resolves variables without
pulling images, so it is safe to run in any environment.

## Continuous integration

[`.github/workflows/course.yml`](../.github/workflows/course.yml) runs on Ubuntu
against the SDK band pinned in `global.json` and executes exactly the gates above:
restore, `dotnet format --verify-no-changes`, build, test with coverage, the
curriculum-plan verifier, the mentor adapter and its tests, and the Compose
definition check.

CI additionally lints the PowerShell management labs with PSScriptAnalyzer and
parses the Azure CLI labs with `bash -n`, because those two files per module must
stay behaviorally equivalent and neither is exercised by `dotnet test`.

CI also asserts that every untouched learner tree is red — the twelve exercise
starters, the Field Station starter, and the capstone starter — because
`untouched_starter_result = "fails"` in `course.toml` is only worth something if
something checks it.

Emulator-backed integration jobs are added with the modules that need them; today
the emulator paths are learner-run through the lesson companions, the management
labs, and the capstone's end-to-end host. Live Azure smoke commands stay manual and documented because
they incur external side effects and cost: **CI never creates cloud resources and
needs no cloud secret.**
