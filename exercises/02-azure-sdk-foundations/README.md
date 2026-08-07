# 🧰 Exercise 2: build testable Azure clients

Practice the seams from
[chapter 2](../../lessons/02-azure-sdk-foundations/README.md): endpoint
resolution, client options, cancellation, retry ownership, and injected clients.

## 🧩 Tasks

- Complete `StorageConnectionResolver` and `BlobStationDirectory`.
- Preserve cancellation and classify failures without string matching.
- Keep Azure SDK construction at the adapter boundary.

## ▶️ Check

```bash
dotnet test exercises/02-azure-sdk-foundations/tests -p:Implementation=starter
```

The evaluator must pass without a network connection.
