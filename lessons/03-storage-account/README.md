# 3. Operate the shared storage boundary

> **Read** this page, **run** the companion in
> [`AccountBoundary/`](AccountBoundary/) against Azurite, **practise** in
> [`exercises/03-storage-account/`](../../exercises/03-storage-account/), then
> complete the **required live checkpoint** with the paired
> [CLI](../../infra/azure-cli/storage-account.sh) and
> [PowerShell](../../infra/powershell/storage-account.ps1) labs.
> Prerequisites: [module 2](../02-azure-sdk-foundations/README.md), Docker, and —
> for the checkpoint only — an Azure subscription.

## Objectives

By the end of this module you can:

- **create, inspect, configure, and delete** a storage account with behaviorally
  equivalent Azure CLI and Azure PowerShell workflows;
- **explain** how endpoints, redundancy, access tiers, encryption, and the
  network and auth boundary constrain the services hosted in one account; and
- **compare** Azurite behavior with a live storage account and record which
  differences change a design decision.

## The question this module answers

Modules 1 and 2 were pure local reasoning. Blob, queue, and table all live inside
one Azure resource, and the next four modules all point at it. So:

> **What decisions does the storage account make on behalf of every service
> inside it, and which of those decisions cannot be changed later?**

The answer is uncomfortable: the account fixes its name, its region, and its
redundancy family at creation. Everything else is adjustable, and everything else
is shared — one throttling limit, one firewall, one auth boundary, one bill, one
delete.

## The account is the DNS name

A storage account is not a container of services in the way a folder is a
container of files. It is a **name in DNS**, and every service is a subdomain of
it:

| service | live endpoint | Azurite |
| --- | --- | --- |
| Blob | `https://stexpedition.blob.core.windows.net/` | `http://127.0.0.1:10000/devstoreaccount1` |
| Queue | `https://stexpedition.queue.core.windows.net/` | `http://127.0.0.1:10001/devstoreaccount1` |
| Table | `https://stexpedition.table.core.windows.net/` | `http://127.0.0.1:10002/devstoreaccount1` |
| File | `https://stexpedition.file.core.windows.net/` | **not emulated** |

Three consequences follow directly from "it is a DNS label", and every one of them
is a rule you have to obey:

1. **The name is globally unique**, across every subscription and tenant on
   earth. Module 1's discriminator existed for this reason.
2. **The alphabet is narrower than resource groups allow**: 3–24 characters,
   lowercase letters and digits only. No hyphens, no underscores, no uppercase —
   a DNS label cannot carry them, and `st-expedition` is rejected by the service,
   not by a linter.
3. **The emulator addresses accounts by path**, because Azurite cannot own DNS.
   `devstoreaccount1` is the account name and it appears in the path, which is
   why emulator and live endpoints have genuinely different *shapes* and why the
   connection has to be resolved rather than string-formatted.

The exercise's `StorageEndpoints.For` encodes exactly this, including the case
Azurite has no answer for at all: `StorageService.File` in the emulator throws
`NotSupportedException` rather than returning a URI that nothing is listening on.

## Redundancy is a promise about failure

Redundancy is chosen at creation and it answers one question: **what failure can
this account survive without losing a write it acknowledged?**

| option | copies | survives a datacenter loss | survives a region loss | secondary readable | relative cost |
| --- | --- | --- | --- | --- | --- |
| LRS | 3, one datacenter | ✗ | ✗ | — | 1× |
| ZRS | 3, three zones | ✓ | ✗ | — | ~1.25× |
| GRS | 3 local + 3 remote | ✗ | ✓ | ✗ | ~2× |
| RA-GRS | 3 local + 3 remote | ✗ | ✓ | ✓ | ~2.5× |
| GZRS | 3 zonal + 3 remote | ✓ | ✓ | ✗ | ~2.3× |
| RA-GZRS | 3 zonal + 3 remote | ✓ | ✓ | ✓ | ~2.8× |

Two traps live in that table.

**Replication is not readability.** GRS and GZRS copy every write to the paired
region asynchronously, and that copy is *unreadable* until a failover happens.
"We have GRS" is therefore not an answer to "can we serve reads during a regional
outage" — only the RA- variants expose the
`https://{account}-secondary.blob.core.windows.net/` endpoint. The exercise's
`HasReadableSecondary` exists to make that distinction executable.

**Zones are not everywhere.** ZRS requires a region with availability zones. Ask
for zone survival in a region without them and there is no configuration that
delivers it. The exercise's `RedundancySelector` **throws** in that case, and
that is a deliberate design decision: silently returning LRS produces an account
that passes an architecture review and loses data on exactly the failure it was
provisioned to survive.

Redundancy can be changed later, but only within limits — LRS↔ZRS and adding or
removing geo-replication involve either a conversion or a migration, and the
zonal/non-zonal boundary in particular is not a checkbox. Treat it as a creation
decision.

## Tiers trade storage price for access price

Access tiers are the opposite trade from redundancy: cheaper at rest, dearer to
read.

| tier | storage price | read price | minimum retention | first-byte latency |
| --- | --- | --- | --- | --- |
| Hot | highest | lowest | none | milliseconds |
| Cool | lower | higher | 30 days | milliseconds |
| Cold | lower still | higher still | 90 days | milliseconds |
| Archive | lowest | highest | 180 days | **hours** (rehydration) |

The **minimum retention** column is where money is lost. Deleting a Cool blob on
day 3 is billed as 30 days of Cool storage; deleting an Archive blob on day 10 is
billed as 180. "We hardly ever read it, put it in Archive" is a cost *increase*
for anything short-lived.

The other trap is the read count. Access charges dominate long before storage
charges do — a blob read a few times a month is cheaper in Hot than in Cool, no
matter how long it is kept. The exercise's `TierAdvisor` puts the read test
*first* for that reason, and refuses Archive outright whenever a read has to
complete immediately, because rehydration is measured in hours and no retention
period changes that.

## The account is the auth boundary

Everything inside the account shares one authentication and network story, and
the course baseline turns six of those settings into non-negotiable decisions:

| setting | baseline | what goes wrong without it |
| --- | --- | --- |
| `allowSharedKeyAccess` | `false` | either account key grants full data-plane access to every container, queue, and table, with no role assignment, no expiry, and no per-identity audit trail |
| `allowBlobPublicAccess` | `false` | any container can be switched to anonymous read by anyone holding container-configuration rights |
| `supportsHttpsTrafficOnly` | `true` | credentials and artifact contents are readable by anything on the network path |
| `minimumTlsVersion` | `TLS1_2` | clients may negotiate a version with known downgrade and cipher weaknesses |
| `networkAcls.defaultAction` | `Deny` | a leaked credential is usable from anywhere on the internet rather than only from approved networks |
| `requireInfrastructureEncryption` | `true` | data is encrypted once rather than twice |

Disabling shared-key access is the one that changes daily work. With it off,
**control-plane rights do not grant data-plane access**: an Owner on the
subscription gets `403 AuthorizationPermissionMismatch` reading a blob until
somebody assigns a data role such as *Storage Blob Data Contributor*. That is not
a bug; it is the point. Management rights and data rights become separately
auditable.

The exercise's `AccountSecurityBaseline.Evaluate` reports **every** violation, not
the first. An audit that stops at the first finding turns a six-problem account
into six fix-and-re-audit cycles.

## Run the companion

Start the emulator, then run the tour. It creates a container, a queue, and a
table in Azurite, shows they share one account, and deletes them again.

```bash
docker compose up -d azurite
dotnet run --project lessons/03-storage-account/AccountBoundary
```

Captured output:

```text
1. ONE ACCOUNT, ONE ENDPOINT PER SERVICE
========================================================================
  account name : devstoreaccount1
  blob         : http://127.0.0.1:10000/devstoreaccount1
  queue        : http://127.0.0.1:10001/devstoreaccount1
  table        : http://127.0.0.1:10002/devstoreaccount1

  The account name is not a label. It is the DNS name every service
  endpoint is derived from, which is why it must be globally unique.

2. THREE SERVICES INSIDE ONE BOUNDARY
========================================================================
  container : /devstoreaccount1/artifacts-expedition-tour
  queue     : /devstoreaccount1/work-expedition-tour
  table     : /devstoreaccount1/observationsexpeditiontour

  this container listed from the account root : yes

  One credential reached all three. Deleting the account deletes all
  three. Throttling limits apply to all three together. The account is
  the unit of billing, naming, access control, and blast radius.

  cleaned up : container, queue, and table deleted

3. THE SAME ACCOUNT, LIVE
========================================================================
  account name : stexpeditiondev7k2m
  blob         : https://stexpeditiondev7k2m.blob.core.windows.net/
  queue        : https://stexpeditiondev7k2m.queue.core.windows.net/
  table        : https://stexpeditiondev7k2m.table.core.windows.net/

  Live, the constructor takes a Uri and a DefaultAzureCredential instead
  of a connection string. Nothing above the adapter changes.

4. EMULATOR PARITY: WHAT DOES NOT CARRY OVER
========================================================================
  feature           azurite                               live
  ------------------------------------------------------------------------------------------------------
  authentication    shared key only                       Entra ID + RBAC, shared key optionally disabled
  redundancy        single local copy, not configurable   LRS / ZRS / GRS / GZRS, chosen at creation
  access tiers      not enforced                          Hot / Cool / Cold / Archive, with rehydration latency
  lifecycle rules   not implemented                       management policy evaluated once per day
  network rules     none — anything on localhost          firewall, private endpoints, service endpoints
  throttling        none                                  per-account IOPS and ingress/egress limits
  cost              zero                                  storage GiB-months + transactions + egress

  Every row above is a reason the live checkpoint in this module is not
  optional: redundancy, tiers, and the auth boundary cannot be observed
  here at all.
```

Note the difference in **shape**, not just in host name: the emulator carries the
account in the path and uses three ports, while live Azure carries it in the host
and uses one port. Code that builds endpoints by string-replacing the host name
works locally and breaks in Azure.

## What Azurite cannot tell you

| capability | Azurite | evidence value |
| --- | --- | --- |
| blob / queue / table CRUD | implemented | trustworthy — use it for every exercise |
| shared-key auth | implemented | trustworthy for the mechanics, misleading as a pattern |
| Entra ID + RBAC | **absent** | live checkpoint only |
| redundancy | **absent** | live checkpoint only |
| access tiers | **absent** | live checkpoint only |
| lifecycle rules | **absent** | live checkpoint only (module 5) |
| network rules / firewall | **absent** | live checkpoint only |
| throttling | **absent** | live checkpoint only |
| Azure Files | **absent** | live checkpoint only |

The exercise's `EmulatorParity` makes this table executable, and it deliberately
throws on an unknown capability name rather than returning `false` — a silent
`false` would let a typo quietly reclassify something as "needs the live
checkpoint" and nobody would ever notice.

## Required live checkpoint

This is the first unit that provisions real Azure resources, and it is
**required**: the auth boundary, redundancy, and tiers have no local equivalent
at all, so skipping it means taking three of this module's claims on faith.

Run **one** of the two labs — they are behaviorally equivalent, same nine steps,
same order, same names:

```bash
bash infra/azure-cli/storage-account.sh
```

```bash
pwsh -File infra/powershell/storage-account.ps1
```

| step | what it proves |
| --- | --- |
| 0 | which identity and subscription is about to be billed |
| 1 | the resource group is the teardown handle |
| 2 | the six baseline settings are creation-time flags, not afterthoughts |
| 3 | the account name is the leftmost DNS label of all three endpoints |
| 4 | control-plane rights do **not** grant data-plane access |
| 5–6 | Entra ID (`--auth-mode login` / `-UseConnectedAccount`) reaches the data plane with no key |
| 7 | the account default tier is reconfigurable after creation |
| 8 | redundancy and geo-replication state exist, and Azurite shows neither |
| 9 | one `az group delete` / `Remove-AzResourceGroup` removes everything |

**Cost.** A general-purpose v2 account holding a few kilobytes for a few minutes
costs well under USD 0.01. **Step 9 is not optional.** If the script is
interrupted, delete the group by hand:

```bash
az group delete --name rg-expedition-checkpoint --yes --no-wait
```

```bash
pwsh -Command "Remove-AzResourceGroup -Name rg-expedition-checkpoint -Force -AsJob"
```

Then confirm nothing survived:

```bash
az resource list --tag managed-by=learning-azure --output table
```

Neither script calls `az login` or `Connect-AzAccount` for you. Signing in is
your decision, made while looking at which tenant and subscription you are about
to spend money in.

## A bounded experiment

Ten minutes, one flag, one prediction.

1. In [`infra/azure-cli/storage-account.sh`](../../infra/azure-cli/storage-account.sh),
   comment out the whole of step 4 (the `az role assignment create` block and the
   `sleep`).
2. **Predict before running:** you are the subscription Owner. Does step 5 —
   creating a container — succeed?
3. Run the script. It fails with
   `AuthorizationPermissionMismatch` and HTTP 403, because `--allow-shared-key-access false`
   in step 2 means the only way in is a **data**-plane role, and Owner is not one.
4. Restore step 4 and run again.

The point: "I have Owner" and "I can read the data" became two different
statements the moment shared-key access was disabled. That separation is the
whole reason the baseline disables it.

## Common mistakes and how to diagnose them

| symptom | what actually happened | how to tell |
| --- | --- | --- |
| `StorageAccountAlreadyTaken` | the name is globally unique and somebody else has it | the name is valid but unavailable; `az storage account check-name` distinguishes the two |
| `403 AuthorizationPermissionMismatch` as subscription Owner | shared-key access is disabled and no data-plane role is assigned | `az role assignment list --scope <account-id>` shows no `Storage Blob Data *` role |
| a role assignment "does not work" for five minutes | RBAC propagation is eventually consistent | retry; the same call succeeds later with no configuration change |
| the account was created but code cannot reach it | endpoints were built by replacing the host, so the emulator's path-style account leaked into the live URI | the failing URI contains `devstoreaccount1` or a port |
| the bill is higher after moving everything to Cool | access charges exceeded the storage saving, or blobs were deleted inside the 30-day minimum | compare transaction counts, not stored GiB |
| a "zone-redundant" account is actually LRS | the region has no availability zones and the deployment silently fell back | `az storage account show --query sku.name` returns `Standard_LRS` |
| resources still billing after the lab | something was created outside the resource group | `az resource list --tag managed-by=learning-azure` finds strays |

## Practice

```bash
# Your work. Expected to FAIL at GAP 1 until you implement it.
dotnet test exercises/03-storage-account/tests -p:Implementation=starter

# The reference implementation, judged by exactly the same evaluator.
dotnet test exercises/03-storage-account/tests -p:Implementation=solution
```

The starter has nine numbered gaps, in dependency order: endpoints and the naming
rule (GAPs 1–2), redundancy selection and readable secondaries (GAPs 3–4), tier
economics (GAPs 5–6), the security baseline (GAP 7), and the parity table
(GAPs 8–9). Each throws a `NotImplementedException` naming the section of this
page that derives it.

**Untouched-starter baseline: fails.** 84 of 85 checks fail, the first with:

```text
System.NotImplementedException : GAP 9: implement EmulatorParity.RequiresLiveCheckpoint.
See lessons/03-storage-account/README.md#what-azurite-cannot-tell-you.
```

That failure is your next action, not a repository defect. (The single passing
check is `TheHotReadThresholdIsSmall`, which reads a constant the starter already
declares.)

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| zone requirement in a zone-less region falls back to LRS instead of throwing | 1 failure: `AZoneRequirementInARegionWithoutZonesFailsLoudly` |
| `Evaluate` returns only the first violation | 1 failure: `EveryViolationIsReportedNotJustTheFirst` — *Assert.Equal() Failure: Expected 6, Actual 1* |
| Archive recommended on retention alone, ignoring the immediacy requirement | 1 failure: `ArchiveIsRefusedWhenAReadMustBeImmediate` — *Assert.NotEqual() Failure: Expected: Not Archive, Actual: Archive* |

Each fault produced exactly one intended failure and left the other 84 checks
passing, so the evaluator localises the defect rather than collapsing.

## Environments

- **Emulator.** `docker compose up -d azurite` for the companion. The exercise
  evaluator is pure and needs nothing running.
- **Live checkpoint: required.** See
  [Required live checkpoint](#required-live-checkpoint). Cost is under USD 0.01
  and the teardown is one command.

## Review questions

1. `st-expedition-dev` is a perfectly legal resource group name and an illegal
   storage account name. Explain why in one sentence, from first principles.
2. An account is GRS and the primary region has an outage. Can the application
   read data? What if it were RA-GRS, and what is the exact difference?
3. A workload must survive a datacenter loss and is deployed to a region without
   availability zones. What does the exercise do, and why is that better than
   returning LRS?
4. An artifact is read twice a month and kept for ten years. Which tier, and why
   is that not Archive?
5. You are the subscription Owner and a blob read returns 403. Name the account
   setting that made that possible and the exact fix.
6. Name three things a green Azurite test run does **not** prove about a live
   account, and say which module each one is finally proved in.

## What you can now assume

The rest of the course takes for granted that you can create an account on the
security baseline, resolve its endpoints in both environments, choose its
redundancy and tier from stated requirements, and delete every resource you
created in one command. [Module 4](../04-blob-storage/README.md) fills the first
of those services with expedition artifacts.
