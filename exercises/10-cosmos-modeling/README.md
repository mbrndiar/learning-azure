# 🌌 Exercise 10: model the global journal

Practice [chapter 10](../../lessons/10-cosmos-modeling/README.md): partition
distribution, synthetic keys, illustrative query cost, hourly autoscale billing,
and indexing trade-offs.

## 🧩 Tasks

- Complete the partition-key, synthetic-key, throughput, query, and indexing planners.
- Apply the autoscale floor to each billed hour before summing.
- Treat synthetic query arithmetic as comparison guidance, not Azure's meter.

## ▶️ Check

```bash
dotnet test exercises/10-cosmos-modeling/tests -p:Implementation=starter
```
