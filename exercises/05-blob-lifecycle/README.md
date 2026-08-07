# ♻️ Exercise 5: coordinate versions and retention

Practice [chapter 5](../../lessons/05-blob-lifecycle/README.md): conditional
writes, bounded read-modify-write, failure triage, and recoverability planning.

## 🧩 Tasks

- Complete the conditional store and artifact update loop.
- Classify conflicts separately from transient service pressure.
- Model versioning, flat-namespace soft delete, and lifecycle expiry accurately.

## ▶️ Check

```bash
dotnet test exercises/05-blob-lifecycle/tests -p:Implementation=starter
```
