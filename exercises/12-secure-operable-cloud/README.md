# 🔐 Exercise 12: secure and operate the cloud boundary

Practice [chapter 12](../../lessons/12-secure-operable-cloud/README.md):
credential resolution, least-privilege roles and scope, subscription preflight,
naming, cost envelopes, and safe teardown.

## 🧩 Tasks

- Complete the credential, role, preflight, naming, cost, and teardown planners.
- Keep control-plane and data-plane authorization distinct.
- Refuse deletion when ownership and scope cannot be proven.

## ▶️ Check

```bash
dotnet test exercises/12-secure-operable-cloud/tests -p:Implementation=starter
```

The evaluator is offline; live Azure remains a separate, explicit checkpoint.
