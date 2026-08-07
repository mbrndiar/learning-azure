# 🗄️ Exercise 4: preserve artifacts

Practice [chapter 4](../../lessons/04-blob-storage/README.md): deterministic blob
paths, bounded-memory transfer, paged listing, and workload-aware transfer plans.

## 🧩 Tasks

- Complete `ArtifactPath`, `BlockStreamingUploader`, `ArtifactCatalog`, and `TransferPlanner`.
- Preserve cancellation and stream ownership.
- Treat listing as paged and lazy rather than as one in-memory result.

## ▶️ Check

```bash
dotnet test exercises/04-blob-storage/tests -p:Implementation=starter
```
