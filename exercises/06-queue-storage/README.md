# 📨 Exercise 6: dispatch idempotent work

Practice [chapter 6](../../lessons/06-queue-storage/README.md): explicit message
encoding, visibility renewal, duplicate delivery, and queue-versus-stream choice.

## 🧩 Tasks

- Complete `WorkOrderCodec`, `VisibilityPlanner`, `IdempotentDispatcher`, and `DispatchSelector`.
- Treat Base64 as the course codec policy, not the SDK default.
- Make the effect safe when the same logical work is delivered again.

## ▶️ Check

```bash
dotnet test exercises/06-queue-storage/tests -p:Implementation=starter
```
