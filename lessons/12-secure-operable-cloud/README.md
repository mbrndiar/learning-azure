# 12. Prove the live architecture

Eleven modules built an architecture. Blobs hold artifacts, queues hand work
between processes, tables index observations, Event Hubs carries telemetry, and
Cosmos holds the journal. Almost all of it ran against an emulator, and every
connection string that was not an emulator's was a shared key in an environment
variable.

That is a working architecture and an unshippable one, because three questions
have never been asked:

> **Who is this process, what is it allowed to do, and what does it cost to
> leave running?**

This module answers all three, and then answers a fourth that only exists
because the first three were answered honestly: *what is still here after you
delete it?*

## Objectives

By the end of this module you can:

- name the least-privilege Entra ID role for a given service and intent, and say
  which of the two role systems in this course it belongs to;
- choose the narrowest scope that covers a piece of work, and explain what a
  scope one level higher additionally grants;
- explain why an Owner cannot read a blob, recognise the exact refusal each
  service produces, and say where to read it;
- trace `DefaultAzureCredential` through its fixed order on four different
  hosts, name which identity wins, and name the ones it shadowed;
- state where the emulator boundary is in authentication terms, and why a
  managed identity cannot be tested against Azurite;
- write a preflight that refuses to run rather than run somewhere unintended,
  covering sign-in, subscription ambiguity, tenant, region, and role;
- wait out role-assignment propagation within a bounded budget, and say what the
  wrong response to a timeout is;
- compose a resource name that a globally unique namespace will accept and a
  teardown can find again, and tag it so that deleting it is safe;
- separate the cost of running an architecture from the cost of forgetting it;
- write a teardown that proves ownership before it deletes, and a verification
  that knows "deleted" is a state rather than an absence;
- run behaviourally equivalent Azure CLI and Azure PowerShell versions of all of
  the above.

## The question this module answers

A live checkpoint costs money and can be pointed at the wrong subscription. Both
of those are ordinary; neither is the interesting failure. The interesting
failure is subtler: a script that works because you are Owner, deployed to a
host where nothing is Owner, and debugged by making something Owner.

Everything below exists to make that sequence impossible to write by accident.

## A role is an answer to a question you have to ask first

An Azure role assignment is three things, and getting any of them wrong produces
a different failure:

| part | what it answers | what a mistake looks like |
| --- | --- | --- |
| role | what may be done | 403, or far too much permitted |
| scope | where it may be done | 403 on a sibling resource, or the whole subscription exposed |
| principal | who may do it | an assignment that exists and applies to nobody |

The role is chosen from the intent, not from the service. "This process reads
reports" is a different role from "this process writes reports", and both are
different from "this process manages the container they live in":

| service | intent | role | system |
| --- | --- | --- | --- |
| Blob | read | Storage Blob Data Reader | Azure RBAC |
| Blob | write | Storage Blob Data Contributor | Azure RBAC |
| Blob | administer | Storage Blob Data Owner | Azure RBAC |
| Queue | read | Storage Queue Data Reader | Azure RBAC |
| Queue | send | Storage Queue Data Message Sender | Azure RBAC |
| Queue | process | Storage Queue Data Message Processor | Azure RBAC |
| Table | read / write | Storage Table Data Reader / Contributor | Azure RBAC |
| Event Hubs | send | Azure Event Hubs Data Sender | Azure RBAC |
| Event Hubs | receive | Azure Event Hubs Data Receiver | Azure RBAC |
| Cosmos DB for NoSQL | read / write | Cosmos DB Built-in Data Reader / Contributor | **Cosmos data plane** |

Two rows in that table are worth more than the others.

**The queue has four data roles rather than two**, because a queue has two
different users. A producer needs to add a message and nothing else; a consumer
needs to peek, dequeue, and delete. Message Sender and Message Processor exist
so that neither has to be Queue Data Contributor, which can do both — including
deleting the message another consumer is halfway through handling.

**Cosmos DB for NoSQL is not in Azure RBAC at all.** Its data plane has its own
role definitions, scoped to the account, assigned with a different command:

```bash
# Everything else in this course:
az role assignment create --role "Storage Blob Data Reader" --assignee <id> --scope <resource-id>

# Cosmos DB for NoSQL data access:
az cosmosdb sql role assignment create \
  --account-name <account> --resource-group <rg> \
  --role-definition-name "Cosmos DB Built-in Data Reader" \
  --principal-id <id> --scope "/"
```

Use the first command with a Cosmos data role name and you get an assignment
that is created successfully, appears in the portal, reads correctly in `az role
assignment list`, and grants nothing whatsoever. It is the most convincing
non-functional configuration in this course.

## Scope is the other half of the grant

Scopes nest, and an assignment applies to everything below it:

```text
/subscriptions/{id}                                                     everything you own
  └── /resourceGroups/rg-expedition-checkpoint                          this run
        └── /providers/Microsoft.Storage/storageAccounts/stexp…         one account
              └── /blobServices/default/containers/reports              one container
```

The same role name is a wildly different grant at each level. "Storage Blob Data
Contributor" at the container is permission to write expedition reports; at the
subscription it is permission to write every blob in every account you own,
including the ones another team is depending on.

Two rules follow, and the exercise enforces both.

**Pick the deepest scope that covers the work.** Two containers in one account
share the account, not the resource group. Two resources of different types
share the resource group.

**Not every path is a scope.** A blob container's resource id runs through
`/blobServices/default/` on the way down, and that intermediate path is not
somewhere a role can be assigned — it exists so the container has somewhere to
live. An implementation that finds the common prefix of two container ids and
stops at the longest shared path lands exactly there, and produces a scope Azure
will reject.

`ResourceScope.Covers` is deliberately not a `StartsWith` on the raw path:

```text
/…/containers/reports          does NOT cover  /…/containers/reports-archive
/…/containers/reports          does     cover  /…/containers/reports
/…/storageAccounts/stexp001    does     cover  /…/containers/reports
```

A prefix test says yes to the first line. That is a silent grant on somebody
else's container.

## Owner is not a data role

This is the single most expensive misunderstanding in Azure, and it is
completely reasonable. Owner is the highest role in the portal. It can delete
the storage account. It cannot list a container inside it.

The reason is that Azure has two planes. The control plane manages resources —
create, configure, delete, and read the keys. The data plane reads and writes
the bytes. Owner, Contributor, Reader, Storage Account Contributor, and
DocumentDB Account Contributor are all control-plane roles, and none of them
carries a single data action.

What that costs you is exactly one thing worth internalising: **Owner can rotate
the keys it cannot use.** With `allowSharedKeyAccess` set to false, that door is
closed too, and there is no path to the data at all without a data role.

The refusal looks different in each service, which matters because you will be
searching for the string you saw:

| service | what a missing data role produces |
| --- | --- |
| Blob / Queue / Table | HTTP **403** with error code **`AuthorizationPermissionMismatch`** |
| Event Hubs | the AMQP link is refused; there is no HTTP status and no documented error code to match on |
| Cosmos DB for NoSQL | HTTP **403** with a substatus (for example 5300); there is no separate error-code field |

The Event Hubs row is why `DenialSignature` in the exercise carries nullable
fields. Inventing a status code so that every service looks the same would send
you looking for a string that does not exist.

## The chain is a fixed order, not a negotiation

`DefaultAzureCredential` is what makes one binary authenticate as a service
principal in CI, as a managed identity in Azure, and as you on your laptop. It
does that by trying a fixed sequence of credentials and stopping at the first one
that produces a token:

```text
1  EnvironmentCredential           deployment   AZURE_CLIENT_ID + secret/certificate
2  WorkloadIdentityCredential      deployment   federated token file (AKS)
3  ManagedIdentityCredential       deployment   IMDS endpoint on the host
4  VisualStudioCredential          developer    signed-in Visual Studio account
5  VisualStudioCodeCredential      developer    Azure Resources extension sign-in
6  AzureCliCredential              developer    az login
7  AzurePowerShellCredential       developer    Connect-AzAccount
8  AzureDeveloperCliCredential     developer    azd auth login
```

Every deployment source is above every developer tool, which is the only reason
a host with a managed identity never quietly runs as whoever last signed in on
it. `InteractiveBrowserCredential` belongs to the same family and is excluded
from the chain by default: a server that opens a browser is a server that hangs.

The order is also the failure mode. A resolution is not just "which credential
won" but "which credentials would have won somewhere else":

| host | resolves to | also configured |
| --- | --- | --- |
| your laptop | `AzureCliCredential` | `AzureDeveloperCliCredential` |
| GitHub Actions with OIDC | `EnvironmentCredential` | — |
| App Service with a system-assigned identity | `ManagedIdentityCredential` | — |
| AKS pod with workload identity | `WorkloadIdentityCredential` | `ManagedIdentityCredential` |

The last row is the one to stare at. The node has an identity and the pod has
one. The chain picks the pod's — and a deployment change that removes the
federated token silently promotes the node's identity, which has different role
assignments, and the application keeps running with different permissions and no
error anywhere.

The exercise models this as two separate lists, and keeping them separate is the
point: **skipped** sources are the ones ahead of the winner that had nothing
configured, and **shadowed** sources are the ones behind it that did. Merging
them loses the only distinction the audit is about.

## Where the emulator boundary actually is

Every emulator in this course accepts a well-known development credential that
is published in Microsoft's documentation, identical on every machine, and
worthless outside localhost. Azurite has one. The Cosmos emulator has one. You
have been using them for eleven modules.

They are not "less secure versions" of the real thing. They are a different
mechanism:

| | emulator | live account |
| --- | --- | --- |
| issues tokens | no | yes, via Entra ID |
| validates tokens | no | yes |
| accepts a shared key | yes, the published one | only while `allowSharedKeyAccess` is true |
| enforces RBAC | **no** | yes |

The last row is the one that costs people a day. An emulator has no concept of a
role assignment, so **every RBAC bug you can write is invisible locally.** The
code path that reads a blob works identically whether the identity has Storage
Blob Data Reader or nothing at all, because Azurite never asks.

This is why the module's checkpoint is live, and why the exercise's evaluator is
offline: the reasoning about roles can be practised deterministically, and the
behaviour of roles cannot be observed anywhere but Azure.

## One credential in production, not a chain

Microsoft's own guidance is to replace `DefaultAzureCredential` with a specific
`TokenCredential` implementation in production. The argument is the shadowing
table above: a chain with more than one usable source is a chain whose answer
depends on the machine.

```csharp
// Development: convenient, and correct here.
var credential = new DefaultAzureCredential();

// Production: this cannot resolve to anything else, and it fails loudly
// on a laptop, which is the behaviour you want.
var credential = new ManagedIdentityCredential(userAssignedClientId);
```

The second form also fails *fast*. The chain, given a misconfigured host, walks
eight credentials before it gives up, and several of those attempts have their
own timeouts — which is why "the app takes 30 seconds to start and then throws"
is a credential-chain symptom rather than a networking one.

## A preflight that cannot prove where it is must refuse

The management labs create resources and delete a resource group. Both are
irreversible in the way that matters — money spent, and other people's work
gone — so every check in their step 0 fails closed.

The order is not arbitrary. Each check is only meaningful once the one before it
has passed:

```text
signed in?          no  -> stop. "Not signed in" is a different instruction
                            from "wrong subscription", and there are no
                            subscriptions to be wrong about yet.
   |
   v
one subscription?   no  -> stop. A display name is not unique: "Visual Studio
                            Enterprise" names a great many subscriptions, and
                            two in one tenant is ordinary. Two matches is an
                            error, never "the first one".
   |
   v
right tenant?       no  -> stop. Checking the tenant of the *resolved*
                            subscription is the only version of this check
                            that means anything.
   |
   v
allowed region?     no  -> stop. A resource in the wrong region is not wrong
                            in a way anything reports. It is simply somewhere
                            else, on a different bill.
   |
   v
roles held?         no  -> stop, and name them. Contributor can build the whole
                            architecture and cannot grant anyone access to it.
   |
   v
proceed
```

Neither lab ever calls `az login` or `Connect-AzAccount` for you. Signing in is
your decision, made deliberately, in the tenant you meant — and a script that
signs you in is a script that can spend money in a subscription you have never
looked at.

## A fresh grant is not a fast grant

Microsoft documents role assignment changes as taking **up to 10 minutes** to
take effect. So:

```text
t+0s    az role assignment create ...        succeeds
t+2s    az storage blob list --auth-mode login   403  <- means nothing
t+40s   az storage blob list --auth-mode login   403  <- still means nothing
t+95s   az storage blob list --auth-mode login   200
```

The first 403 after a grant is not evidence of anything. Two responses to it are
wrong in opposite directions: polling forever, which produces a lab step nobody
interrupts, and widening the role, which is how least privilege actually dies.
"It worked when I gave it Contributor" is a sentence with a mechanism behind it
— Contributor does not grant data access either, so if that fixed it, what
actually happened is that ten minutes passed.

When the budget expires, the diagnosis is a checklist, not a bigger role:

1. Is the assignment's **scope** the one you think? An assignment on a sibling
   container looks identical in a list.
2. Is the **principal id** the identity that is actually calling? A user's own
   object id is not their application's.
3. Is your **token** older than the assignment? Tokens carry claims from when
   they were issued; a fresh token is sometimes the whole fix.

## A name is a teardown handle

Several Azure names live in a global namespace — a storage account becomes
`<name>.blob.core.windows.net`, and an Event Hubs namespace and a Cosmos account
are the same idea. A name that reads beautifully and is already taken fails at
creation, and a name that is unique but anonymous survives forever because
nobody dares delete it.

| resource | length | characters | globally unique |
| --- | --- | --- | --- |
| storage account | 3–24 | lower-case letters and digits only | yes |
| Event Hubs namespace | 6–50 | letters, digits, hyphens; starts with a letter | yes |
| Cosmos DB account | 3–44 | lower-case letters, digits, hyphens | yes |
| resource group | 1–90 | wide | no |

Composition therefore has a trap in it. A name is a prefix a human recognises
plus a run id that keeps two people in one subscription apart, and when the
result is too long, **the prefix is what gets cut.** Truncating the tail deletes
exactly the characters that were doing the work:

```text
prefix "expeditionfieldstationcheckpointstorage" + run "a7f39c"

cut the prefix   ->  expeditionfieldsta  + a7f39c   = expeditionfieldstaa7f39c
cut the tail     ->  expeditionfieldstationch                (run id gone)
                     expeditionfieldstationch                (someone else's run, same name)
```

Tags are the other half of the same job. Four of them, on everything:

| tag | question it answers |
| --- | --- |
| `owner` | who do I ask before deleting this? |
| `managed-by` | did automation make this, and which automation? |
| `purpose` | what is this line on the bill? |
| `expires-on` | when did this stop being justified? |

`managed-by=learning-azure` is not decoration. It is the thing the teardown
checks before it acts, and it is what turns a mistyped resource-group name from
an incident into a refusal.

## The bill has two numbers

Every resource in this course has one of three billing shapes, and the shape —
not the price — decides what happens when everybody goes home:

| shape | billed for | idle cost |
| --- | --- | --- |
| provisioned | existing, per hour | **unchanged** |
| storage | bytes held | **unchanged** |
| consumption | work performed | zero |

So an architecture has two totals. The run cost, which is what a checkpoint asks
you to spend, and the idle cost, which is what forgetting it asks you to spend
every day thereafter. For the module's own checkpoint they differ by more than
two orders of magnitude:

```text
a 1.5 hour checkpoint       ~ $0.092
the same thing, forgotten   ~ $1.13 per day, ~ $33.86 per month
```

The resource that dominates the second number is rarely the largest line on the
first. Consumption is where the money goes during a run and contributes exactly
nothing to the bill for forgetting.

None of the figures above are quoted from a price list, and no lab in this
course reads one. Prices change by region, currency, offer, and reserved-capacity
commitment; the shape does not. Learn the shape, and read the current number
from the pricing calculator when it matters.

## Teardown is the only code that cannot be re-run

Everything else in this course is idempotent under a retry. A delete is not, and
a delete aimed at the wrong scope is not recoverable by aiming again.

So the teardown proves ownership before it acts, in this order:

| what the platform reports | decision | why not simply delete |
| --- | --- | --- |
| the scope is a subscription | refuse | that is not a teardown, it is an incident |
| the group carries no tags | refuse | nothing proves this run created it |
| `managed-by` is something else | refuse | another tool owns it and will recreate it |
| `owner` is another person | refuse | not yours to delete |
| the group also holds foreign resources | delete only the tagged resources | the group delete would take their work too |
| the group is entirely this run's | delete the resource group | cheapest, atomic, complete |

The last row is the reason every lab in this course creates its own resource
group. A group that contains exactly one run is a group that can be deleted
without an inventory.

Note that the tags are re-read from the platform rather than trusted from a
variable. The variable says what you meant; the tag says what is actually there.

## Deleted is not gone

`az group delete` returns before the delete finishes, and several services keep
a recoverable copy on purpose. A cleanup that stops at "the command succeeded"
leaves four categories of remnant behind:

| remnant | how long | what it still costs you |
| --- | --- | --- |
| the resource group itself | seconds to minutes | nothing; the delete is asynchronous |
| storage account | recoverable for 14 days | creating a new account with the same name silently forfeits the recovery |
| Log Analytics workspace | soft-deleted 14 days, purged within 30 | it still holds the ingested data, and the name stays reserved |
| key vault | soft-deleted 7–90 days | with purge protection it cannot be purged early at all, so the name is unavailable until retention expires |
| role assignment whose principal was deleted | indefinitely | it shows as "Identity not found" at a scope that still exists, and nobody audits it |

The last row is the one that accumulates. Deleting a scope removes the
assignments inside it; deleting a *principal* removes nothing, and every
short-lived identity anybody ever granted access to leaves an entry behind.

## Run the companion

```bash
dotnet run --project lessons/12-secure-operable-cloud/AccessBoundary
```

It needs nothing: no emulator, no container, no Azure session. Every identity,
endpoint, scope, and cleanup state in it is a value, so the output is identical
on a laptop with `az login` and on a build agent with nothing configured. Output
in full:

```text
1. The chain is an order, not a negotiation
===========================================

DefaultAzureCredential tries these in order and stops at the first one that
produces a token. InteractiveBrowserCredential is in the family but excluded
by default: a server that opens a browser is a server that hangs.

  #  credential                          kind        signal it looks for
  -  ----------------------------------  ----------  --------------------------------
  1  EnvironmentCredential               deployment  AZURE_CLIENT_ID + secret/certificate
  2  WorkloadIdentityCredential          deployment  federated token file (AKS)
  3  ManagedIdentityCredential           deployment  IMDS endpoint on the host
  4  VisualStudioCredential              developer   signed-in Visual Studio account
  5  VisualStudioCodeCredential          developer   Azure Resources extension sign-in
  6  AzureCliCredential                  developer   az login
  7  AzurePowerShellCredential           developer   Connect-AzAccount
  8  AzureDeveloperCliCredential         developer   azd auth login

Every deployment source sits above every developer tool. That ordering is the
only reason a host with a managed identity never runs as whoever last signed in
on it.

2. The same binary, four hosts
==============================

  host                                    resolves to                     also configured
  --------------------------------------  -----------------------------  --------------------------
  your laptop                             AzureCliCredential             AzureDeveloperCliCredential
  GitHub Actions (OIDC)                   EnvironmentCredential          -
  App Service + system-assigned identity  ManagedIdentityCredential      -
  AKS pod with workload identity          WorkloadIdentityCredential     ManagedIdentityCredential

The last row is the one worth staring at. The node has an identity and the pod
has one; the chain picks the pod's, and the node's is one deployment change away
from becoming the answer instead. That is why production code pins a single
credential: `new ManagedIdentityCredential(clientId)` cannot resolve to anything
else, and it fails loudly on a laptop, which is the correct behaviour.

3. The grant plan for the live checkpoint
=========================================

  read expedition reports
    role   Storage Blob Data Reader  (Azure RBAC)
    scope  .../resourceGroups/rg-expedition-checkpoint/providers/Microsoft.Storage/storageAccounts/stexpedition9f2a1c/blobServices/default/containers/reports
  write expedition reports
    role   Storage Blob Data Contributor  (Azure RBAC)
    scope  .../resourceGroups/rg-expedition-checkpoint/providers/Microsoft.Storage/storageAccounts/stexpedition9f2a1c/blobServices/default/containers/reports
  publish telemetry
    role   Azure Event Hubs Data Sender  (Azure RBAC)
    scope  .../resourceGroups/rg-expedition-checkpoint/providers/Microsoft.EventHub/namespaces/ehns-expedition/eventhubs/telemetry
  consume telemetry
    role   Azure Event Hubs Data Receiver  (Azure RBAC)
    scope  .../resourceGroups/rg-expedition-checkpoint/providers/Microsoft.EventHub/namespaces/ehns-expedition/eventhubs/telemetry
  write journal documents
    role   Cosmos DB Built-in Data Contributor  (Cosmos data plane)
    scope  .../resourceGroups/rg-expedition-checkpoint/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-expedition

The last row is assigned with a different command from the four above it:
  az cosmosdb sql role assignment create ...   (Cosmos data plane)
  az role assignment create ...                (everything else)
Using the second command for a Cosmos data role produces an assignment that
exists, reads correctly in the portal, and grants nothing.

4. Owner is not a data role
===========================

  identity holds : Owner at /subscriptions/00000000-0000-0000-0000-000000000000
  identity wants : read a blob in .../resourceGroups/rg-expedition-checkpoint/providers/Microsoft.Storage/storageAccounts/stexpedition9f2a1c/blobServices/default/containers/reports

  result         : denied
  storage        : 403 AuthorizationPermissionMismatch
  event hubs     : the AMQP link is refused; there is no HTTP status to read
  cosmos         : 403 with a substatus, and no separate error-code field

Owner, Contributor, and Storage Account Contributor are control-plane roles.
They can delete the account and rotate its keys; they carry no data action at
all. Reading the bytes needs a data role, and the two hierarchies only meet in
the portal's left-hand menu.

5. Names and tags are teardown handles
======================================

  resource               name                        rule
  ---------------------  --------------------------  -----------------------------------------
  storage account        stexpedition9f2a1c          3-24, lower-case letters and digits, global
  event hubs namespace   ehns-expedition-9f2a1c      6-50, starts with a letter, global
  cosmos account         cosmos-expedition-9f2a1c    3-44, lower-case, digits, hyphens, global
  resource group         rg-expedition-checkpoint    1-90, not globally unique

  The run id '9f2a1c' is the part that keeps two people in one subscription apart.
  When a name is too long, cut the prefix. Cutting the tail is how two runs end
  up asking for the same globally unique name, and the failure arrives as a 409
  on somebody else's resource.

  Every resource carries the same four tags:
    owner=field-team  managed-by=learning-azure
    purpose=module-12-checkpoint  expires-on=2026-12-31
  managed-by is not decoration: the teardown refuses to delete a group without
  it, which is what stops a wrong RESOURCE_GROUP from becoming an incident.

6. The bill has two numbers
===========================

  resource                          shape        USD/hour
  --------------------------------  -----------  --------
  Cosmos DB, 400 RU/s provisioned   provisioned   0.03200
  Event Hubs namespace, Basic       provisioned   0.01500
  Blob storage, ~1 GiB              storage       0.00003
  Storage + Cosmos requests         consumption   0.00400
  Log Analytics ingestion           consumption   0.01000

  a 1.5 hour checkpoint    ~ $0.092
  the same thing, forgotten  ~ $1.13 per day, ~ $33.86 per month

  Consumption lines fall to zero the moment nobody calls anything. Provisioned
  throughput and stored bytes do not: they are billed for existing. The second
  number is what a missing `az group delete` actually costs, and it is roughly
  370x the run itself.

7. Teardown, and what survives it
=================================

  what the platform reports          what the teardown does
  ---------------------------------  ----------------------------------------------------
  scope is a subscription            refuse - a teardown deletes a group, never anything above one
  group has no tags                  refuse - nothing proves this run created it
  managed-by is 'terraform'          refuse - somebody else's automation owns it
  owner is another person            refuse - not this run's to delete
  group holds foreign resources      delete only the tagged resources
  group is entirely this run's       delete the resource group

  Then verify, because "deleted" is a state and not an absence:
    - the group delete is asynchronous; it returns before it finishes
    - a deleted storage account is recoverable for 14 days, and creating a new
      account with the same name silently forfeits that recovery
    - a deleted Log Analytics workspace keeps its data, and its name, for 14 days
    - a deleted key vault is soft-deleted for 7-90 days, and purge protection
      means it cannot be purged early at all
    - role assignments whose principal was deleted survive as 'Identity not
      found' entries at a scope that still exists

8. What this companion cannot tell you
======================================

  Nothing above touched Azure, so nothing above can prove:
    - that a role assignment takes effect (documented as up to 10 minutes)
    - what your subscription's policy assignments will refuse to create
    - what a 403 looks like in your own terminal, with your own principal id
    - whether a name you like is still free in the global namespace
    - what Cost Management reports, which lags and needs a settled subscription

  Those five are exactly what the management labs are for:
    infra/azure-cli/secure-operable-cloud.sh
    infra/powershell/secure-operable-cloud.ps1
```

### Where this stops being provable offline

Section 8 is the honest part of the companion. Everything above it is reasoning
you can check; none of it is evidence. In particular:

- **No emulator enforces RBAC.** Azurite will happily serve a client that holds
  no role at all, so every authorization bug in this module is invisible against
  it. This is the one module where the local environment cannot even
  *approximate* the behaviour being taught.
- **Propagation delay does not exist locally**, so a retry loop written against
  an emulator is a loop that has never once gone round twice.
- **The refusal messages** in section 4 are quoted from documentation. Reading
  one produced by your own subscription, with your own principal id in it, is a
  different kind of knowing.

## The management labs

```bash
bash infra/azure-cli/secure-operable-cloud.sh
```

```powershell
pwsh infra/powershell/secure-operable-cloud.ps1
```

The two are behaviourally equivalent: same ten steps, same order, same names,
same refusals. Read them side by side once — the CLI version resolves a
subscription with `az account list --query`, the PowerShell version with
`Get-AzSubscription | Where-Object`, and the ambiguity check is identical in
both.

| step | what it proves |
| --- | --- |
| 0 | the preflight refuses: no session, ambiguous subscription, wrong tenant, disallowed region, missing role |
| 1 | a tagged resource group, created before anything else, as the teardown handle |
| 2 | a storage account with `allowSharedKeyAccess=false` — the keys still exist and no longer work |
| 3 | **the identity that just created the account cannot read inside it**, with the exact 403 |
| 4 | the narrowest role that does the job, at the account rather than the group |
| 5 | a bounded wait for propagation, then a token-authenticated write and read |
| 6 | revocation, and the same call failing again |
| 7 | the activity log entry for the grant, and the data-plane transaction metrics |
| 8 | the resources this run created, by tag, and what usage the offer exposes |
| 9 | a teardown that re-reads the tags and refuses a group it cannot prove it owns |
| 10 | post-cleanup verification: soft-deleted accounts, soft-deleted workspaces, orphaned assignments |

Step 3 is worth the whole run. You will have just created a storage account, so
you are Owner or Contributor over it, and the very next command fails.

Budget roughly USD 0.01 and forty minutes, most of it waiting for propagation in
steps 5 and 6. Step 9 deletes everything; if you interrupt the script, the
resource-group name is printed at every step so the manual teardown is one
command.

## A bounded experiment

Fifteen minutes, inside the lab, between steps 4 and 5. It answers a question
the offline evaluator cannot: **how long does propagation actually take, and is
it the same for a grant and a revoke?**

1. Run the lab to the end of step 4, then interrupt it (Ctrl-C). The resource
   group survives; so does the assignment.
2. Time the grant taking effect:

   ```bash
   START=$SECONDS
   until az storage container list --account-name "$ACCOUNT_NAME" --auth-mode login --output none 2>/dev/null; do
     printf 'refused at %ss\n' "$((SECONDS - START))"; sleep 5
   done
   printf 'authorized after %ss\n' "$((SECONDS - START))"
   ```

3. Now revoke it and time the reverse:

   ```bash
   az role assignment delete --assignee "$PRINCIPAL_ID" \
     --role "Storage Blob Data Contributor" --scope "$ACCOUNT_SCOPE" --yes
   START=$SECONDS
   while az storage container list --account-name "$ACCOUNT_NAME" --auth-mode login --output none 2>/dev/null; do
     printf 'still allowed at %ss\n' "$((SECONDS - START))"; sleep 5
   done
   printf 'refused again after %ss\n' "$((SECONDS - START))"
   ```

4. Repeat the grant, but this time force a fresh token first with
   `az account get-access-token --output none` and see whether the number moves.

What to expect: both directions typically complete in well under the documented
ten-minute ceiling, and they are usually *not* symmetric. What matters is not the
number you get — it will differ by tenant, by day, and by scope — but that you
now know it is neither zero nor unbounded, and that the correct handling is a
budget rather than a single attempt.

Then delete the group: `az group delete --name "$RESOURCE_GROUP" --yes`.

## Common mistakes and how to diagnose them

| symptom | actual cause | how to tell |
| --- | --- | --- |
| 403 `AuthorizationPermissionMismatch`, and you are Owner | Owner is a control-plane role | `az role assignment list --assignee <id> --scope <resource> --include-inherited` shows no `*Data*` role |
| 403 immediately after granting the role | propagation, not permission | wait and retry; if it works in a minute, it was never a permissions bug |
| a Cosmos role assignment that grants nothing | assigned in Azure RBAC instead of the Cosmos data plane | it appears in `az role assignment list` and not in `az cosmosdb sql role assignment list` |
| "the correct key is rejected" | `allowSharedKeyAccess` is false | `az storage account show --query allowSharedKeyAccess`; the response says key-based authorization is not permitted |
| the CLI works and the app does not | the CLI is you, the app is a managed identity | compare `az ad signed-in-user show --query id` with the identity's principal id |
| a deployment that worked yesterday now has different permissions | the credential chain resolved to a different source | log `resolution.Selected`; a shadowed source became the winner |
| a role assignment reading "Identity not found" | the principal was deleted, the scope was not | `az role assignment list --all --query "[?principalName==null]"` |
| `az group create` fails with a name conflict on a name you have never used | a globally unique name, truncated so two runs collide | check whether the run id survived the length limit |
| the subscription is right in the portal and wrong in the script | a display name matched more than one subscription | `az account list --query "[?name=='<name>'].id"` returns two rows |
| Cost Management shows nothing for a new subscription | it can take up to 48 hours to become available | the tags are the fallback: `az resource list --tag managed-by=learning-azure` |

## Practice

```bash
# Your work. Expected to FAIL until you implement the gaps.
dotnet test exercises/12-secure-operable-cloud/tests -p:Implementation=starter

# The reference implementation, judged by exactly the same evaluator.
dotnet test exercises/12-secure-operable-cloud/tests
```

Fourteen gaps across six files, every one of them offline and deterministic —
identities, subscriptions, scopes, probes, and cleanup state are all values, and
nothing in the evaluator touches Azure, a token endpoint, or an emulator:

| gap | file | what it decides |
| --- | --- | --- |
| 1 | `RoleCatalog.cs` | the least-privilege role for one intent, and the system it lives in |
| 2 | `RoleCatalog.cs` | the deepest common scope that is still an assignable scope |
| 3 | `RoleCatalog.cs` | role, scope, and role system all lining up — or the refusal that says which did not |
| 4 | `CredentialChain.cs` | first available source wins, and what it skipped versus shadowed |
| 5 | `CredentialChain.cs` | the emulator boundary: well-known key, token, or nothing at all |
| 6 | `CredentialChain.cs` | which resolutions are findings, and which are only findings in production |
| 7 | `SubscriptionPreflight.cs` | exactly one subscription, or a named refusal |
| 8 | `SubscriptionPreflight.cs` | the fail-closed checks, in the order that produces the useful message |
| 9 | `SubscriptionPreflight.cs` | a bounded wait for propagation that never suggests a broader role |
| 10 | `ResourceNaming.cs` | sanitising and truncating a name without discarding the run id |
| 11 | `ResourceNaming.cs` | the tag contract a teardown depends on |
| 12 | `CostEnvelope.cs` | the run cost, the idle cost, and which resource drives the second |
| 13 | `TeardownPlan.cs` | proving ownership before deleting, and narrowing when it cannot |
| 14 | `TeardownPlan.cs` | what is still there after the delete returned |

The untouched starter fails **139 of 161 checks**. Every failure names its gap
and this file:

```text
System.NotImplementedException : GAP 1: implement RoleCatalog.RoleFor.
See lessons/12-secure-operable-cloud/README.md#a-role-is-an-answer-to-a-question-you-have-to-ask-first.
```

The reference implementation passes all 161.

### How this evaluator is known to be strong

A reference implementation that passes proves nothing about the evaluator. These
are real runs against the reference solution with one fault introduced, then
reverted:

| fault introduced | evaluator response |
| --- | --- |
| control-plane roles no longer recognised as such | 1 failure: `Evaluate_SaysWhyTheControlPlaneRoleDidNotHelp` |
| scope containment written as a string prefix | 1 failure: `Evaluate_DoesNotMistakeAPrefixForAScope` |
| the role system ignored when matching an assignment | 1 failure: `Evaluate_RefusesACosmosDataRoleRecordedAsAnAzureRbacAssignment` |
| a queue producer given Queue Data Contributor | 2 failures, including `RoleFor_NamesTheLeastPrivilegeRole(service: QueueStorage, intent: SendMessages, expected: "Storage Queue Data Message Sender")` |
| `blobServices/default` treated as an assignable scope | 3 failures, including `NarrowestScope_ClimbsToTheAccountForTwoContainersInIt` |
| shadowed sources merged into skipped | 7 failures, including `Resolve_KeepsSkippedAndShadowedApart` |
| the emulator check made second, after the chain | 2 failures: `AuthenticateAgainst_UsesTheWellKnownKeyForAnEmulator` and `AuthenticateAgainst_DoesNotSendATokenToAnEmulatorThatIssuesNone` |
| a production host on a developer credential not treated as a finding | 1 failure: `Audit_FlagsAProductionHostRunningAsASignedInHuman` |
| an ambiguous subscription name resolved to the first match | 2 failures, including `ResolveSubscription_RefusesADisplayNameThatMatchesTwice` |
| the propagation budget not enforced | 1 failure: `ConfirmRoleReady_IgnoresProbesTakenAfterTheBudget` |
| a too-long name truncated from the end | 3 failures, including `Compose_ProducesDifferentNamesForDifferentRunsOfTheSameLongPrefix` — *Expected: Not "expeditionfieldstationch", Actual: "expeditionfieldstationch"* |
| tag validation stopping at the first problem | 1 failure: `ValidateTags_ReportsEveryProblemAtOnce` — *Expected: 4, Actual: 1* |
| consumption resources billed while idle | 4 failures, including `Estimate_ExcludesConsumptionResourcesFromTheIdleCost` |
| the most expensive resource reported as the idle driver | 1 failure: `Estimate_DoesNotNameAConsumptionResourceAsDominantJustBecauseItIsExpensive` |
| teardown skipping the "are there tags at all" check | 1 failure: `Plan_RefusesAnUntaggedGroup` |
| foreign resources in the group ignored | 1 failure: `Plan_NarrowsToTaggedResourcesWhenSomebodyElsePutSomethingInTheGroup` |
| a returned delete treated as a finished delete | 2 failures, including `Verify_ReportsAGroupThatIsStillListed` |
| soft-deleted remnants not reported | 3 failures, including `Verify_DoesNotCallACleanupCompleteBecauseTheGroupIsGone` |

Eighteen faults, eighteen caught. Three are worth a second look.

**The truncation fault is the one that would have shipped.** Cutting a name to
its maximum length is the obvious implementation, it produces a valid name, and
it passes every check that only asks "is this name legal". It fails exactly one
kind of test: two runs, one long prefix, and an assertion that the results
differ. The failure message is the bug stated plainly — *Expected: Not
"expeditionfieldstationch"* — and in production it arrives as a 409 on somebody
else's resource.

**Merging skipped and shadowed broke seven checks, and none of them is about
merging.** They are about the audit: what the chain shadowed is the entire input
to "is this resolution safe to deploy". A model that loses the distinction
cannot answer the question it exists to answer, which is why the fault
propagates so far from where it was introduced.

**The `blobServices/default` fault needed a test that looks pedantic.** Asserting
that a scope's path does not contain a particular literal reads like
implementation-coupling until you remember that Azure rejects an assignment
aimed there. Counting path segments is enough to produce something that parses,
looks like a scope in a log line, and cannot be assigned.

## Environments

- **Local: everything.** The companion and the evaluator both run with nothing
  installed and nothing signed in. There is no emulator for this module, and
  that is not an omission: no emulator in existence enforces an Azure role
  assignment.
- **Live checkpoint: required.** Run one of the two management labs end to end.
  Steps 3, 5, 6, and 10 are the module's core content and have no local
  equivalent in any form — a control-plane role that grants nothing, a grant
  that takes minutes to arrive, a revocation that takes minutes to leave, and a
  delete that leaves recoverable copies behind. Budget roughly USD 0.01 and
  forty minutes; step 9 deletes the resource group and step 10 checks it.

## Review questions

1. A colleague has Owner on the subscription and gets 403
   `AuthorizationPermissionMismatch` reading a blob. They ask to be made Owner
   "properly, at the account". Explain what will happen and what they actually
   need.
2. Give the least-privilege role for each of: a service that only publishes
   telemetry to one event hub; a worker that consumes from a queue and deletes
   what it processed; a dashboard that reads one blob container. State the scope
   for each as a path shape.
3. An assignment of "Cosmos DB Built-in Data Contributor" was created with
   `az role assignment create` and the application still gets 403. Explain the
   mechanism, and give the command that shows the assignment is absent.
4. A grant is made at `/…/containers/reports`. Which of these does it cover:
   `/…/containers/reports`, `/…/containers/reports-archive`,
   `/…/containers/reports/blobs/day-1.json`? Justify each with a rule, not an
   example.
5. Two blob containers in one storage account, and one queue in the same
   account, all need to be reachable by one identity with one assignment. Give
   the scope, and say what that assignment additionally grants that a
   per-container assignment would not.
6. The same container image runs on your laptop, in GitHub Actions, and in an
   AKS pod with workload identity enabled on a node that also has a system
   identity. Name the credential that wins in each, and the one that is shadowed
   in the third.
7. An application takes 30 seconds to start on a new host and then throws an
   authentication error. Give the mechanism and the one-line change that would
   both fix the latency and improve the diagnostics.
8. Your team's deployment script signs in with `az login` if no session exists.
   Describe the failure this eventually causes, and what the script should do
   instead.
9. A preflight checks tenant before it resolves the subscription. Construct a
   case where this passes and the run still creates resources in the wrong
   place.
10. A lab grants a role and the next call returns 403. Give the three things you
    would check, in order, and say why "assign Contributor as well" is not one
    of them — including what actually happened when somebody reports that it
    worked.
11. A run composes `st` + a 14-character prefix + a 6-character run id for a
    storage account. Give the resulting name, then explain what a naive
    truncation would produce and how the failure would present.
12. An architecture costs USD 0.09 to run for ninety minutes and USD 34 a month
    to leave in place. Explain how those two numbers can both be true, and name
    the property of each resource that decides which total it contributes to.
13. `az group delete` returned successfully an hour ago. Name four things that
    may still exist, how long each survives, and which of them prevents you from
    reusing a name.
14. A teardown is handed a resource group that carries `managed-by=terraform`
    and contains one resource your run created. State the correct decision and
    the reasoning, then state what a teardown that deleted the group would have
    cost.

## What you can now assume

You can now put this architecture somewhere real. You can say which identity a
process will be, which role that identity needs, at which scope, in which of the
two role systems, and what the refusal will look like when you get it wrong. You
can write a script that refuses to run in the wrong place rather than one that
apologises afterwards, and you can delete what you made and then prove it.

That is the last mechanic. What remains is not a new service but a demand: the
capstone asks you to build one system out of all twelve modules — artifacts,
work, observations, telemetry, journal, and a live checkpoint you operate and
tear down yourself, with the identity and cost story you have just learned
attached to it rather than added afterwards.

## References

- [Credential chains in the Azure Identity library for .NET](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/credential-chains)
- [Authentication best practices with the Azure Identity library](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/best-practices)
- [Azure built-in roles for Storage](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage)
- [Authorize access to blobs using Microsoft Entra ID](https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory)
- [Prevent Shared Key authorization for an Azure Storage account](https://learn.microsoft.com/en-us/azure/storage/common/shared-key-authorization-prevent)
- [Authorize access to Event Hubs resources using Microsoft Entra ID](https://learn.microsoft.com/en-us/azure/event-hubs/authorize-access-azure-active-directory)
- [Configure role-based access control for Azure Cosmos DB for NoSQL](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/how-to-grant-data-plane-role-based-access)
- [Troubleshoot Azure RBAC](https://learn.microsoft.com/en-us/azure/role-based-access-control/troubleshooting)
- [Understand scope for Azure RBAC](https://learn.microsoft.com/en-us/azure/role-based-access-control/scope-overview)
- [Naming rules and restrictions for Azure resources](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-name-rules)
- [Recover a deleted storage account](https://learn.microsoft.com/en-us/azure/storage/common/storage-account-recover)
- [Delete and recover a Log Analytics workspace](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/delete-workspace)
- [Azure Key Vault soft-delete overview](https://learn.microsoft.com/en-us/azure/key-vault/general/soft-delete-overview)
