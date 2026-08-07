# ✅ Course verifier

Repository automation, not course content. It validates the curriculum design in
[`docs/architecture/curriculum.json`](../../docs/architecture/curriculum.json)
against the contract in
[`curriculum-plan-schema.md`](../../docs/architecture/curriculum-plan-schema.md),
against the repository as it actually is, and against the Learning Mentor
manifest.

```bash
dotnet run --project tools/CourseVerifier/CourseVerifier -- verify
dotnet run --project tools/CourseVerifier/CourseVerifier -- matrix --write
dotnet test tools/CourseVerifier/CourseVerifier.Tests
```

## What `verify` rejects

| area | rejected |
| --- | --- |
| identity | non-semantic IDs, IDs derived from order or file name, embedded commit hashes, duplicates |
| graph | unknown or self prerequisites, cycles, a prerequisite taught later, a redundant edge that is already reachable |
| shape | more or fewer than one project or capstone, a capstone that is not the final destination or not taught last, module ordinals or global sequences with gaps |
| outcomes | unmeasurable verbs, statements that are not sentences, outcomes measured by something that is not the unit's evaluator |
| milestones | milestones on a module, fewer than two on a project or capstone, prerequisites outside the owner, out-of-order or cyclic staging |
| evidence | a missing or duplicated stage, coverage claimed for an artifact or heading that does not exist, `not-applicable` without a rationale, a deferral that is not for the applied stage or points at an already-built unit |
| honesty | content that exists while the plan calls it planned, content that is missing while the plan calls it present, a mentor manifest that registers an unbuilt or undeclared unit, a built unit that is not registered |
| documents | a narrative that omits a unit or misquotes an outcome, a stale evidence matrix |

Every one of those rejections is proven by a fixture test, so the gate is known
to fail closed today — before there is any course content for it to check.

## Design notes

- **Paths are derived, not declared.** A unit's lesson, exercise, lab, and
  practice paths come from its kind, ordinal, and slug, so the
  starter/solution/shared-evaluator convention is enforced rather than
  remembered.
- **The plan and the mentor manifest are separate authorities with one rule
  between them.** The plan owns the whole curriculum; `course.toml` owns the
  built subset. A unit crosses over only when its artifacts exist.
- **The evidence matrix is generated.** `matrix --write` renders it and `verify`
  fails if the checked-in copy has drifted, so the published coverage record
  cannot disagree with the plan.
