# 🔁 Exercise 9: checkpoint and recover

Practice [chapter 9](../../lessons/09-event-hubs-processing/README.md):
checkpoint policy, ownership diagnosis, lag, idempotent projection, and bounded
processor shutdown.

## 🧩 Tasks

- Complete the checkpoint ledger and policy.
- Diagnose ownership without assuming one processor owns the whole hub.
- Resume safely across replay, cancellation, and restart.

## ▶️ Check

```bash
dotnet test exercises/09-event-hubs-processing/tests -p:Implementation=starter
```
