# 🧰 Shared test support

[`AzureFakes/`](AzureFakes/) provides deterministic Azure SDK transports and
application-owned doubles used by exercise, project, and capstone evaluators.
They preserve real SDK request construction without opening a socket.

These helpers are course infrastructure, not learner shortcuts: assertions stay
in the visible evaluator and production behavior stays behind explicit ports.
