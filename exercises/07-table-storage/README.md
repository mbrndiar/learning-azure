# 🗂️ Exercise 7: design the observation index

Practice [chapter 7](../../lessons/07-table-storage/README.md): composite keys,
query shape, ETag updates, and same-partition transaction limits.

## 🧩 Tasks

- Complete `ObservationKeys`, `QueryPlanner`, `ObservationUpdater`, and `BatchValidator`.
- Make dominant reads point-addressable.
- Reject batches that cross a partition or exceed the service limit.

## ▶️ Check

```bash
dotnet test exercises/07-table-storage/tests -p:Implementation=starter
```
