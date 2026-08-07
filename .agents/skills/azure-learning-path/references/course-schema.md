# Course manifest schema 1.0

`course.toml` is parsed with Python 3.11+'s `tomllib`; adapters must not require a
third-party TOML package. Paths are repository-root-relative, and commands run
from the repository root.

This manifest is the single authority for the Learning Mentor integration:
semantic IDs, prerequisites, outcomes, learner and reference paths, focused
commands, implementation selectors, and solution-lock ownership. The mentor never
re-derives the curriculum graph from directory order — it reads this manifest
through the course adapter.

## Relationship to the curriculum plan

This manifest is the **built** subset of the curriculum — the units the Learning
Mentor may track. The whole planned curriculum, including units nobody has
written yet, lives in `docs/architecture/curriculum.json` and is validated by
`tools/CourseVerifier`.

`curriculum_plan` is a required top-level key naming that document. The adapter
checks, after every structural rule below has passed, that:

- the plan exists, parses, and declares the same `course.id`;
- every module, project, and capstone registered here is declared in the plan;
- their prerequisites, and the milestone IDs and milestone prerequisites of
  projects and capstones, agree with the plan; and
- the plan marks each registered unit's artifacts as `present`.

The last rule is the one that matters most: a unit whose content does not exist
cannot be registered, so the mentor can never record progress against an
objective the repository has not built. The verifier enforces the other
direction — content that lands without a plan update fails the build.

## Partial registration

`learning-azure` was built in stages, so the manifest is allowed to register a
*prefix* of the curriculum rather than all of it. It no longer needs to: today
`[[modules]]` covers modules 1 through 12, `[[projects]]` covers the Field
Station, `[[capstones]]` covers the Cloud Expedition Field Journal, and
`course.final_destinations` names that capstone as required.

The rule survives the completion because it is what keeps a future addition
honest. The adapter treats an absent trackable collection as an empty one, so a
partially registered curriculum validates cleanly; `course.final_destinations`
may be empty only while no capstone is built. Every rule below applies in full to
whatever *is* registered. Fixture-based adapter tests exercise the rules against
a synthetic curriculum, including projects and capstones, so the evaluator is
proven to fail closed for units a repository has not built.

Nothing in this manifest may point at a path that does not exist. Planning ahead
belongs in the curriculum plan, which records intended paths as `planned` and
fails if they materialize without review; it never asserts that they are present.

## Stability

- `manifest_version` versions the instance; `schema_version` versions this
  contract.
- Unit, concept, project, capstone, and milestone IDs are semantic, opaque
  identifiers. They must not contain commit hashes and must not depend on
  display order or file names.
- Existing IDs are never reassigned. Renames change titles or paths, not IDs. A
  material change to an objective's meaning or boundary introduces a new ID and
  a course version change; historical learner-state rows stay intact.
- Every prerequisite names another declared trackable ID. Concept prerequisites
  apply in addition to their parent module prerequisites. The graph is acyclic.

## Records

- `[course]` carries course identity, the supported .NET boundary, entry and
  setup documents, the command working directory, and the required final
  destinations. `language` must be `csharp`, `target_framework` must be
  `net10.0`, and `dotnet_sdk_minimum` must be `10.0`. Those claims must agree
  with `global.json` and `Directory.Build.props`; the adapter rejects a support
  claim that neither backs. `final_destinations` lists the required capstones and
  may be empty only during the bootstrap baseline.
- `[[modules]]` is one ordered teaching module declaring its prerequisites,
  required outcomes, narrative README, review-question source, exercise starter
  and reference, focused validation command(s), and solution-lock group. The
  lock covers the reference implementation only; deterministic evaluators remain
  visible so they can grade learner work.
- `[[modules.concepts]]` maps one stable concept ID to a runnable .NET **project
  directory** and its documented command. A lesson companion is a directory
  containing a `.csproj`, so `lesson_project` names the directory and
  `run_command` must invoke the `dotnet` CLI against it. Concepts are ordered by
  explicit prerequisites, not array position.
- `[[projects]]` and `[[capstones]]` use the same prerequisite, outcome,
  validation, and solution-lock conventions.
- `[[projects.milestones]]` and `[[capstones.milestones]]` provide stable
  milestone IDs, prerequisites, one required outcome, and a focused learner test
  command that must target the owner's `starter_root`.
- Every module and every milestone must declare `untouched_starter_result`,
  either `"fails"` or `"passes"`. A focused command that exits zero on an
  untouched starter is a legitimate scaffold only when it cannot be mistaken for
  completion, so a `"passes"` baseline additionally requires an
  `untouched_starter_note` explaining why the pass is **not completion
  evidence**. The adapter rejects a passing baseline without that explanation, so
  a vacuous `ok` can never be consumed as a finished objective.
- `[[solution_lock_groups]]` declares repository paths hidden until the group's
  policy permits comparison. `solution_unlock_after` is the minimum number of
  recorded attempts before an explicit post-attempt unlock request may succeed;
  a deterministic success may unlock earlier. A lock group must never cover a
  learner starter tree or a module narrative.

## Implementation selector

.NET selects an implementation by **project path**, not by an environment
variable, so `implementation_selector` records `kind = "project-path"` with
`learner_value = "starter"` and `reference_value = "solution"`. The adapter
checks that `starter_root` and `solution_root` end with those segments and that
every milestone command targets the learner tree. Adapters must not invent an
environment-variable selector for this ecosystem.

## Commands

Every focused command is a single line, contains no shell operators
(`&& || | ; > < $(  )` backticks), parses as a POSIX argument vector, and invokes
the `dotnet` CLI. Commands are documented verbatim in `SKILL.md` so the learner
runs exactly what the manifest declares.

## Tutor state projection

`[course].id` and `[course].version` identify adapter content. The shared state
helper uses the normalized Git remote (or an explicit local fallback) as course
identity and the observed commit SHA as version identity. Adapters must not
invent unsupported course-ID or course-version CLI flags.

The adapter, not the state helper, parses this TOML. It projects trackable
concepts and milestones into the helper's flat `concepts` initialization JSON,
preserving IDs, assigning deterministic order, inheriting container
prerequisites, and copying `solution_unlock_after` from the referenced lock
group. Container completion is derived from its required children rather than
from a second unstable identifier.

All declared path fields must exist at manifest-validation time. A future adapter
may declare a command-created path only with a record shaped like
`{ path = "...", availability = "command-created", created_by = "..." }`; plain
strings always mean `availability = "repository"`.

Unknown keys may be ignored by schema-compatible readers. Missing required
records, duplicate IDs, invalid references, cycles, nonexistent repository paths,
shell operators inside commands, or unsupported selector values are errors that
fail closed before any learner state is written.
