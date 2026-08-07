# 💻 Exercise 11: query and update Cosmos DB

Practice [chapter 11](../../lessons/11-cosmos-development/README.md): paging,
optimistic concurrency, transactional batches, throttle budgets, retry safety,
and cleanup planning.

## 🧩 Tasks

- Complete the page reader, concurrency guard, throttle policy, writer, batch planner, and cleanup planner.
- Distinguish immutable from mutable upserts after unknown completion.
- Make every retry preserve the original business intent.

## ▶️ Check

```bash
dotnet test exercises/11-cosmos-development/tests -p:Implementation=starter
```
