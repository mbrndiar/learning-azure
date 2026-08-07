# Curriculum plan schema 1

`docs/architecture/curriculum.json` is the **planning authority** for the course
curriculum. It exists because the curriculum graph has to be inspectable,
acyclic, and reviewable *before* any lesson, exercise, project, or capstone
content exists — and because a plan must never be able to claim that content
exists when it does not.

Two authorities, one graph:

| authority | owns | consumed by |
| --- | --- | --- |
| `docs/architecture/curriculum.json` | the full planned graph, outcomes, evidence records, and repository role map | `tools/CourseVerifier` |
| `.agents/skills/azure-learning-path/course.toml` | the **built** subset the Learning Mentor may track | the course adapter |

`course.toml` never contains a unit that is not in the plan, and the plan never
marks a unit as built unless its artifacts are on disk. The verifier enforces
both directions, so a unit becomes trackable only when its content is real.

## Document fields

| field | meaning |
| --- | --- |
| `plan_version` | schema version of this instance; must be `1` |
| `schema_document` | path to this document |
| `course_id` | must equal `[course].id` in `course.toml` |
| `narrative_document` | human-readable curriculum, which must mention every unit ID verbatim |
| `evidence_matrix_document` | rendered evidence matrix; must match `CourseVerifier matrix` byte for byte |
| `manifest_document` | the mentor manifest cross-checked against this plan |
| `conventions` | the starter/solution/shared-evaluator path convention, recorded so it is enforced rather than remembered |
| `roles` | the repository role map required by the quality contract |
| `units` | every module, project, and capstone |

## Path convention

Paths are **derived**, never hand-written, so a unit cannot drift from the
convention:

| role key | derivation |
| --- | --- |
| `lesson_readme` | `lessons/{ordinal:00}-{slug}/README.md` |
| `lesson_projects_root` | `lessons/{ordinal:00}-{slug}` |
| `exercise_starter` | `exercises/{ordinal:00}-{slug}/starter` |
| `exercise_solution` | `exercises/{ordinal:00}-{slug}/solution` |
| `exercise_tests` | `exercises/{ordinal:00}-{slug}/tests` |
| `cli_lab` | `infra/azure-cli/{slug}.sh` |
| `powershell_lab` | `infra/powershell/{slug}.ps1` |
| `project_guide` / `project_starter` / `project_solution` / `project_tests` | `projects/{slug}/README.md`, `.../starter`, `.../solution`, `.../tests` |
| `capstone_guide` / `capstone_starter` / `capstone_solution` / `capstone_tests` | `capstones/{slug}/README.md`, `.../starter`, `.../solution`, `.../tests` |

`{ordinal}` is the module ordinal zero-padded to two digits. Ordinals order
presentation only; **prerequisites**, not ordinals, define the graph.

`project_*` and `capstone_*` role keys resolve against the single project and the
single capstone, so a module may cite them for its applied-stage evidence.

## Unit records

| field | required | meaning |
| --- | --- | --- |
| `id` | yes | semantic identifier: `module.<slug>`, `project.<slug>`, or `capstone.<slug>`. Never contains an ordinal, a file name, or a commit hash, and is never reassigned. |
| `kind` | yes | `module`, `project`, or `capstone`; must match the ID prefix |
| `slug` | yes | lowercase kebab-case; must match the ID suffix |
| `ordinal` | yes | presentation order within the kind; unique and gap-free from 1 |
| `sequence` | yes | global teaching position; unique and gap-free from 1 across all units |
| `title`, `summary` | yes | learner-facing naming |
| `prerequisites` | yes | declared entry prerequisites, by ID |
| `environments` | yes | any of `local`, `emulator`, `live-checkpoint` |
| `management_labs` | yes | whether the unit ships paired Azure CLI and PowerShell labs |
| `artifact_status` | yes | `planned` or `present` — see below |
| `split_rationale` | when split | why a unit was split out of another rather than merged |
| `final_destination` | capstone | must be `true` on the required capstone |
| `outcomes` | yes | measurable outcomes |
| `milestones` | project, capstone | staged milestones |
| `evidence` | yes | exactly one record per progression stage |

### Graph rules

- Every prerequisite names another declared unit; no unit requires itself.
- The graph is acyclic, and a prerequisite's `sequence` is always lower than the
  dependent's, so the declared teaching order is a valid topological order.
- Prerequisite lists are **transitively reduced**: a prerequisite that is already
  reachable through another prerequisite is rejected, so every edge is a real,
  reviewable claim rather than defensive noise.
- A project or capstone appears after all of its prerequisites and before any
  unit that treats its applied experience as a prerequisite.
- Milestone prerequisites are local to their owner and are acyclic under the same
  rules.

### Outcomes

Each outcome has a stable `outcome.<slug>.<name>` ID, a statement, and
`measured_by` naming the role that provides its feedback path. Statements must
start with an approved measurable verb. Unmeasurable verbs — *understand*,
*know*, *learn*, *appreciate*, *be familiar with*, *explore*, *review* — are
rejected, because an outcome that cannot be observed cannot be validated.

### Evidence records

Every unit declares exactly one record for each stage of the quality contract's
progression: **named → explained → demonstrated → practiced → applied**. A
record has a `status`, an `artifact` (a `role` plus optional heading `anchor`, or
an explicit repository `path`), and a `note` describing what satisfies the stage.

| status | meaning | verifier rule |
| --- | --- | --- |
| `planned` | the evidence does not exist yet | permitted only while the unit's `artifact_status` is `planned` |
| `covered` | the evidence exists and the note states the behavior it demonstrates | the resolved artifact must exist; a declared `anchor` must match a real heading |
| `partial` | some evidence exists, but the stage is incomplete | always blocks completion |
| `missing` | required evidence is absent | always blocks completion |
| `deferred` | the stage is satisfied by a later unit that is not built yet | only for `applied`; requires `deferred_to` naming a later-sequence unit that is still `planned` |
| `not-applicable` | the stage does not apply | requires a `rationale`; no artifact |

`artifact_status` is the fail-closed hinge:

- `planned` — every derived path for the unit **must not exist**, and every
  evidence stage must be `planned`. Content that lands silently is an error.
- `present` — every derived path **must exist**, and no stage may remain
  `planned`, `partial`, or `missing`.

A unit may appear in `course.toml` only when its `artifact_status` is `present`.
That is what prevents the course from advertising coverage, or the mentor from
recording progress, against content that has not been written.
