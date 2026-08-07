#!/usr/bin/env bash
# =============================================================================
# capstone.cloud-expedition-journal — live checkpoint, Azure CLI
# =============================================================================
#
# Provisions the whole expedition estate — storage account, Event Hubs
# namespace, and Cosmos account — grants only data-plane roles to the signed-in
# identity, runs the capstone against it, shows the diagnostics and cost views
# that do not exist locally, and deletes everything.
#
#   bash infra/azure-cli/cloud-expedition-journal.sh
#
# The PowerShell twin infra/powershell/cloud-expedition-journal.ps1 performs the
# same steps in the same order with the same names, so the two can be read side
# by side.
#
# WHY THIS CHECKPOINT IS REQUIRED. Four things the capstone claims cannot be
# observed against emulators at all:
#
#   * Shared-key and SAS authentication can be switched OFF at the resource
#     (steps 2 and 3). No emulator can refuse a key, so "identity is the only
#     path" is an assertion locally and a fact here.
#   * Cosmos data-plane access is a SEPARATE RBAC system from the control plane
#     (step 5). A subscription Owner still cannot read a document. That is
#     surprising, expensive to discover in an incident, and invisible locally.
#   * Request charges and throttling are real numbers on a real throughput
#     budget (step 7), not a fake's arithmetic.
#   * Deleting the resource group is the only teardown that is actually
#     complete (step 8), and it is the only one that stops the meters.
#
# COST: a Standard Event Hubs namespace at one throughput unit is roughly USD
# 0.03 per TU-hour; a serverless Cosmos account bills per request unit; a
# Standard_LRS storage account holding a handful of blobs is a fraction of a
# cent. Fifteen minutes of all three is well under USD 0.05, and step 8 deletes
# them. If the script is interrupted, delete the group by hand:
#
#   az group delete --name rg-expedition-journal --yes --no-wait
#
# PREREQUISITES: Azure CLI 2.60+, an authenticated session, and the .NET SDK
# band in global.json if you run step 6. This script never calls 'az login' for
# you — sign in yourself so you can see which identity and subscription you are
# about to spend money in.
# =============================================================================

set -euo pipefail

LOCATION="${LOCATION:-westeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-expedition-journal}"
STORAGE_ACCOUNT="${STORAGE_ACCOUNT:-stexpedition$RANDOM$RANDOM}"
NAMESPACE_NAME="${NAMESPACE_NAME:-ehexpedition$RANDOM$RANDOM}"
COSMOS_ACCOUNT="${COSMOS_ACCOUNT:-cosmosexpedition$RANDOM$RANDOM}"
HUB_NAME="telemetry"
CONSUMER_GROUP="field-journal"
COSMOS_DATABASE="expedition"
COSMOS_CONTAINER="journal"

# A namespace name becomes a DNS label: 6-50 characters, letters, digits, and
# hyphens, starting with a letter. A storage account name is stricter still:
# 3-24 characters, lower-case letters and digits only. A Cosmos account name is
# 3-44 characters with the same alphabet plus hyphens.
STORAGE_ACCOUNT="$(printf '%s' "$STORAGE_ACCOUNT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9' | cut -c1-24)"
NAMESPACE_NAME="$(printf '%s' "$NAMESPACE_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-' | cut -c1-50)"
COSMOS_ACCOUNT="$(printf '%s' "$COSMOS_ACCOUNT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-' | cut -c1-44)"

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

# -----------------------------------------------------------------------------
step "0. Confirm the identity and subscription that will be billed"
# -----------------------------------------------------------------------------
az account show --output table

read -r -p "Create resources in the subscription above? [y/N] " reply
[[ "$reply" == "y" || "$reply" == "Y" ]] || { echo "Aborted."; exit 1; }

PRINCIPAL_ID="$(az ad signed-in-user show --query id --output tsv)"

# -----------------------------------------------------------------------------
step "1. Create the resource group (the teardown handle)"
# -----------------------------------------------------------------------------
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "2. Create the storage account with shared-key access switched off"
# -----------------------------------------------------------------------------
# --allow-shared-key-access false is the setting that makes the identity
# argument real: with it, an account key cannot be used even if one leaks, and
# every SDK path that silently prefers a key fails immediately instead of on the
# day somebody audits it.
az storage account create \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-shared-key-access false \
  --allow-blob-public-access false \
  --min-tls-version TLS1_2 \
  --output table

STORAGE_SCOPE="$(az storage account show --name "$STORAGE_ACCOUNT" --resource-group "$RESOURCE_GROUP" --query id --output tsv)"

# -----------------------------------------------------------------------------
step "3. Create the namespace and the hub with local auth switched off"
# -----------------------------------------------------------------------------
# --disable-local-auth true does for Event Hubs what --allow-shared-key-access
# false does for Storage: SAS policies stop working, including the
# RootManageSharedAccessKey rule that every quick-start uses.
#
# Four partitions, matching infra/local/eventhubs/config.json, so the number of
# concurrent consumer instances that can do useful work is the same locally and
# here: four.
az eventhubs namespace create \
  --name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard \
  --capacity 1 \
  --disable-local-auth true \
  --output table

az eventhubs eventhub create \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --partition-count 4 \
  --cleanup-policy Delete \
  --retention-time-in-hours 1 \
  --output table

az eventhubs eventhub consumer-group create \
  --name "$CONSUMER_GROUP" \
  --eventhub-name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --output table

NAMESPACE_SCOPE="$(az eventhubs namespace show --name "$NAMESPACE_NAME" --resource-group "$RESOURCE_GROUP" --query id --output tsv)"

# -----------------------------------------------------------------------------
step "4. Create the Cosmos account, database, and partitioned container"
# -----------------------------------------------------------------------------
# Serverless, because this workload is a few hundred requests in a burst and
# then nothing: provisioned throughput would bill for reserved capacity nobody
# uses. The partition key path matches CosmosJournalProjection.PartitionKeyPath
# — the container's physical layout and the application's dominant query have to
# agree, and this is the one place that agreement is declared.
az cosmosdb create \
  --name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --locations regionName="$LOCATION" failoverPriority=0 isZoneRedundant=false \
  --default-consistency-level Session \
  --capabilities EnableServerless \
  --output table

az cosmosdb sql database create \
  --account-name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$COSMOS_DATABASE" \
  --output table

az cosmosdb sql container create \
  --account-name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$COSMOS_DATABASE" \
  --name "$COSMOS_CONTAINER" \
  --partition-key-path /stationId \
  --output table

# -----------------------------------------------------------------------------
step "5. Grant data-plane roles only"
# -----------------------------------------------------------------------------
# Owner on a resource grants management rights, not data rights. Nothing below
# is a control-plane role, and granting one would not be least privilege even if
# it happened to work.
for role in "Storage Blob Data Contributor" \
            "Storage Queue Data Contributor" \
            "Storage Table Data Contributor"; do
  az role assignment create \
    --assignee-object-id "$PRINCIPAL_ID" \
    --assignee-principal-type User \
    --role "$role" \
    --scope "$STORAGE_SCOPE" \
    --output none
  echo "granted: $role"
done

for role in "Azure Event Hubs Data Sender" \
            "Azure Event Hubs Data Receiver"; do
  az role assignment create \
    --assignee-object-id "$PRINCIPAL_ID" \
    --assignee-principal-type User \
    --role "$role" \
    --scope "$NAMESPACE_SCOPE" \
    --output none
  echo "granted: $role"
done

# Cosmos data-plane RBAC is a separate system with its own assignment command
# and its own built-in definitions. 00000000-...-0002 is Cosmos DB Built-in Data
# Contributor. There is no portal blade for this and no 'az role assignment'
# equivalent; a Contributor who cannot read a document has usually hit exactly
# this.
az cosmosdb sql role assignment create \
  --account-name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --role-definition-id 00000000-0000-0000-0000-000000000002 \
  --principal-id "$PRINCIPAL_ID" \
  --scope "/" \
  --output none
echo "granted: Cosmos DB Built-in Data Contributor"

echo "-- what the identity can actually do, as the platform sees it"
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --scope "$STORAGE_SCOPE" \
  --query "[].{role:roleDefinitionName, scope:scope}" \
  --output table

# -----------------------------------------------------------------------------
step "6. Run the capstone against these resources"
# -----------------------------------------------------------------------------
# Role assignments take a minute or two to propagate. A 403 immediately after
# step 5 is usually that, not a wrong role.
echo "Waiting 90s for role assignments to propagate..."
sleep 90

COSMOS_ENDPOINT="$(az cosmosdb show --name "$COSMOS_ACCOUNT" --resource-group "$RESOURCE_GROUP" --query documentEndpoint --output tsv)"

echo "Run the capstone from the repository root with:"
echo
echo "  export EXPEDITION_ENVIRONMENT=live"
echo "  export EXPEDITION_STORAGE_ACCOUNT=$STORAGE_ACCOUNT"
echo "  export EXPEDITION_EVENTHUBS_NAMESPACE=$NAMESPACE_NAME"
echo "  export EXPEDITION_COSMOS_ENDPOINT=$COSMOS_ENDPOINT"
echo "  dotnet run --project capstones/cloud-expedition-journal/solution"
echo
echo "The run refuses to start while a key, connection string, or SAS token is"
echo "in the environment, so use a shell that never sourced emulator.env."

read -r -p "Run it now from this shell? [y/N] " run_reply
if [[ "$run_reply" == "y" || "$run_reply" == "Y" ]]; then
  REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
  env -u AZURITE_CONNECTION_STRING \
      -u EVENTHUBS_EMULATOR_CONNECTION_STRING \
      -u COSMOS_EMULATOR_ENDPOINT \
      -u COSMOS_EMULATOR_KEY \
      EXPEDITION_ENVIRONMENT=live \
      EXPEDITION_STORAGE_ACCOUNT="$STORAGE_ACCOUNT" \
      EXPEDITION_EVENTHUBS_NAMESPACE="$NAMESPACE_NAME" \
      EXPEDITION_COSMOS_ENDPOINT="$COSMOS_ENDPOINT" \
      dotnet run --project "$REPO_ROOT/capstones/cloud-expedition-journal/solution"
fi

# -----------------------------------------------------------------------------
step "7. Read the operator's view that no emulator emits"
# -----------------------------------------------------------------------------
echo "-- per-partition stream position; the checkpoint is NOT here, it is in your container"
for partition in 0 1 2 3; do
  az eventhubs eventhub partition show \
    --eventhub-name "$HUB_NAME" \
    --namespace-name "$NAMESPACE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --partition-id "$partition" \
    --query "{partition:partitionId, begin:beginSequenceNumber, lastEnqueued:lastEnqueuedSequenceNumber, empty:isEmpty}" \
    --output json
done

echo "-- namespace throughput, the metric a lagging consumer shows up in"
az monitor metrics list \
  --resource "$NAMESPACE_SCOPE" \
  --metric IncomingMessages OutgoingMessages \
  --interval PT1M \
  --output table \
  || echo "(no metrics yet: a namespace under a few minutes old has nothing to report)"

echo "-- Cosmos request units consumed, which is what the invoice is computed from"
COSMOS_SCOPE="$(az cosmosdb show --name "$COSMOS_ACCOUNT" --resource-group "$RESOURCE_GROUP" --query id --output tsv)"
az monitor metrics list \
  --resource "$COSMOS_SCOPE" \
  --metric TotalRequestUnits \
  --interval PT1M \
  --output table \
  || echo "(no metrics yet)"

echo "-- what is left in the container after the run's own teardown"
az storage blob list \
  --container-name expedition-journal \
  --account-name "$STORAGE_ACCOUNT" \
  --auth-mode login \
  --query "[].name" \
  --output tsv \
  || echo "(container already deleted by the run's teardown, which is the expected result)"

# -----------------------------------------------------------------------------
step "8. Delete everything"
# -----------------------------------------------------------------------------
# Three meters stop here: the namespace's hourly throughput-unit charge, the
# Cosmos account's request and storage charges, and the storage account's
# capacity and transaction charges. The application's own teardown removes the
# container, queues, table, and Cosmos database; the accounts themselves are
# only ever removed by this.
az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --output none

echo
echo "Deleted resource group $RESOURCE_GROUP. Verify with:"
echo "  az group exists --name $RESOURCE_GROUP"
