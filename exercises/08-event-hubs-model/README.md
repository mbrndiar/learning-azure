# 🌊 Exercise 8: model the telemetry stream

Practice [chapter 8](../../lessons/08-event-hubs-model/README.md): partition-key
bytes, keyed batching, capacity limits, and stream-versus-queue decisions.

## 🧩 Tasks

- Complete `PartitionKeyPlanner`, `TelemetryBatcher`, `StreamOrQueueSelector`, and `CapacityPlanner`.
- Count partition-key length in UTF-8 bytes.
- Preserve every event when a batch fills or rejects an oversized item.

## ▶️ Check

```bash
dotnet test exercises/08-event-hubs-model/tests -p:Implementation=starter
```
