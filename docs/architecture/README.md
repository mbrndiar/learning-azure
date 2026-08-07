# 🏛️ Architecture and curriculum design

Design records for the course itself, as opposed to the Azure architecture the
course teaches.

| document | role |
| --- | --- |
| [`curriculum.md`](curriculum.md) | the reviewable curriculum: graph, split decisions, project placement, role map, and the starter/solution/shared-evaluator convention |
| [`curriculum.json`](curriculum.json) | the machine-readable authority the verifier checks |
| [`curriculum-plan-schema.md`](curriculum-plan-schema.md) | the contract `curriculum.json` must satisfy |
| [`evidence-matrix.md`](evidence-matrix.md) | generated coverage record, one row per outcome stage |

Regenerate the matrix and re-check the plan from the repository root:

```bash
dotnet run --project tools/CourseVerifier/CourseVerifier -- matrix --write
dotnet run --project tools/CourseVerifier/CourseVerifier -- verify
```

The Azure architecture records for the applied units live with the units that own
them: [`projects/field-station/README.md`](../../projects/field-station/README.md)
and [`capstones/cloud-expedition-journal/README.md`](../../capstones/cloud-expedition-journal/README.md),
whose *Architecture* and *Data flow* sections are the end-to-end diagrams for the
Storage half and for the whole course respectively.
