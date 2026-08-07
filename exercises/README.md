# 🧠 Exercises

Every chapter has a matching `starter`, locked `solution`, and visible
deterministic evaluator. The starter fails at named gaps until the required
behavior is implemented; the tests remain visible so feedback never depends on
guessing what the course expects.

## ▶️ Workflow

```bash
dotnet test exercises/01-azure-data-map/tests -p:Implementation=starter
```

1. Read the matching chapter and this exercise guide.
2. Implement one numbered gap in `starter/`.
3. Run the smallest evaluator again.
4. Add or explain a boundary case.
5. Compare with `solution/` only after deterministic success or an explicit
   post-attempt unlock request.

## 🗂️ Modules

1. [`01-azure-data-map/`](01-azure-data-map/)
2. [`02-azure-sdk-foundations/`](02-azure-sdk-foundations/)
3. [`03-storage-account/`](03-storage-account/)
4. [`04-blob-storage/`](04-blob-storage/)
5. [`05-blob-lifecycle/`](05-blob-lifecycle/)
6. [`06-queue-storage/`](06-queue-storage/)
7. [`07-table-storage/`](07-table-storage/)
8. [`08-event-hubs-model/`](08-event-hubs-model/)
9. [`09-event-hubs-processing/`](09-event-hubs-processing/)
10. [`10-cosmos-modeling/`](10-cosmos-modeling/)
11. [`11-cosmos-development/`](11-cosmos-development/)
12. [`12-secure-operable-cloud/`](12-secure-operable-cloud/)

After module 7, apply Storage unaided in the
[`Field Station`](../projects/field-station/README.md). After module 12, finish
the [`Cloud Expedition Field Journal`](../capstones/cloud-expedition-journal/README.md).
