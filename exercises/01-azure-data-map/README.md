# 🧭 Exercise 1: choose the data primitive

Practice the decisions from [chapter 1](../../lessons/01-azure-data-map/README.md):
service characteristics, workload routing, and deterministic Azure naming.

## 🧩 Tasks

- Complete `PrimitiveCharacteristics`, `PrimitiveSelector`, and `ExpeditionNaming`.
- Keep service ceilings separate from application codec policies.
- Justify the selected primitive against its nearest alternative.

## ▶️ Check

```bash
dotnet test exercises/01-azure-data-map/tests -p:Implementation=starter
```

You are done when the evaluator passes and you can explain why each rejected
primitive fails the workload. Reference code remains locked until then.
