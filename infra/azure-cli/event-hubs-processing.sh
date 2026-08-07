#!/usr/bin/env bash
# =============================================================================
# module.event-hubs-processing — live checkpoint, Azure CLI
# =============================================================================
#
# Creates the two resources a real consumer needs — an Event Hubs namespace and
# the storage account its checkpoints live in — grants the split roles a
# processor actually requires, reads the runtime information that tells you how
# far behind a consumer is, and deletes it all.
#
#   bash infra/azure-cli/event-hubs-processing.sh
#
# The PowerShell twin infra/powershell/event-hubs-processing.ps1 performs the
# same steps in the same order with the same names, so the two can be read side
# by side.
#
# WHY THIS CHECKPOINT IS REQUIRED. Three things this module teaches cannot be
# observed locally at all:
#
#   * Consumer groups are declared in infra/local/eventhubs/config.json and read
#     once at container start. The emulator cannot add or remove one, so the
#     cost of a new consumer group — a second full read of the stream — is
#     invisible until you create one here (step 4).
#   * The roles a processor needs are split across two services: it reads from
#     Event Hubs AND writes to Blob Storage. A processor with only the Event
#     Hubs role starts, claims nothing, and reports errors from a component
#     nobody was looking at (step 5).
#   * Consumer lag is a platform metric, not an SDK call. The emulator emits no
#     metrics whatsoever (step 6).
#
# COST: a Standard namespace at one throughput unit is roughly USD 0.03 per
# TU-hour; a Standard_LRS storage account holding a handful of checkpoint blobs
# is a fraction of a cent. Ten minutes of both is well under USD 0.02, and step
# 8 deletes them. If the script is interrupted, delete the group by hand:
#
#   az group delete --name rg-expedition-checkpoint --yes --no-wait
#
# PREREQUISITES: Azure CLI 2.60+ and an authenticated session. This script never
# calls 'az login' for you — sign in yourself so you can see which identity and
# subscription you are about to spend money in.
# =============================================================================

set -euo pipefail

LOCATION="${LOCATION:-westeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-expedition-checkpoint}"
NAMESPACE_NAME="${NAMESPACE_NAME:-ehexpedition$RANDOM$RANDOM}"
STORAGE_ACCOUNT="${STORAGE_ACCOUNT:-stexpedition$RANDOM$RANDOM}"
HUB_NAME="telemetry"
CONSUMER_GROUP="field-journal"
SECOND_CONSUMER_GROUP="cold-archive"
CHECKPOINT_CONTAINER="checkpoints"

# A namespace name becomes a DNS label: 6-50 characters, letters, digits, and
# hyphens, starting with a letter. A storage account name is stricter still:
# 3-24 characters, lower-case letters and digits only.
NAMESPACE_NAME="$(printf '%s' "$NAMESPACE_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-' | cut -c1-50)"
STORAGE_ACCOUNT="$(printf '%s' "$STORAGE_ACCOUNT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9' | cut -c1-24)"

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

# -----------------------------------------------------------------------------
step "0. Confirm the identity and subscription that will be billed"
# -----------------------------------------------------------------------------
az account show --output table

read -r -p "Create resources in the subscription above? [y/N] " reply
[[ "$reply" == "y" || "$reply" == "Y" ]] || { echo "Aborted."; exit 1; }

# -----------------------------------------------------------------------------
step "1. Create the resource group (the teardown handle)"
# -----------------------------------------------------------------------------
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "2. Create the namespace and the hub"
# -----------------------------------------------------------------------------
# Four partitions, matching infra/local/eventhubs/config.json, so the number of
# concurrent consumer instances that can do useful work is the same locally and
# here: four. A fifth instance would own nothing.
az eventhubs namespace create \
  --name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard \
  --capacity 1 \
  --minimum-tls-version 1.2 \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

az eventhubs eventhub create \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --partition-count 4 \
  --cleanup-policy Delete \
  --retention-time 1 \
  --output table

# -----------------------------------------------------------------------------
step "3. Create the checkpoint store"
# -----------------------------------------------------------------------------
# The checkpoint store is a separate service with a separate availability
# record, a separate bill, and separate permissions. A processor whose blob
# container is unreachable does not read events slowly — it does not read them
# at all, because it cannot claim a partition without writing an ownership blob.
#
# Standard_LRS is right here: the blobs are tiny, they are rewritten constantly,
# and losing them costs a replay rather than data.
az storage account create \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --min-tls-version TLS1_2 \
  --allow-blob-public-access false \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

az storage container create \
  --name "$CHECKPOINT_CONTAINER" \
  --account-name "$STORAGE_ACCOUNT" \
  --auth-mode login \
  --output table

# -----------------------------------------------------------------------------
step "4. Add a consumer group, and see what it costs"
# -----------------------------------------------------------------------------
# A consumer group is a cursor, not a copy. Adding one does not duplicate a
# single byte of storage — and it does add a full second read of every event to
# the namespace's egress, which is charged against the same throughput units the
# producers are using. Standard allows 20 per hub; the reason to stop well short
# of that is egress, not the quota.
for group in "$CONSUMER_GROUP" "$SECOND_CONSUMER_GROUP"; do
  az eventhubs eventhub consumer-group create \
    --name "$group" \
    --eventhub-name "$HUB_NAME" \
    --namespace-name "$NAMESPACE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --output none
  printf 'created consumer group: %s\n' "$group"
done

az eventhubs eventhub consumer-group list \
  --eventhub-name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "[].{name:name, created:createdAt}" \
  --output table

# -----------------------------------------------------------------------------
step "5. Grant the TWO roles a processor needs"
# -----------------------------------------------------------------------------
# This is the step that catches people. A processor is a client of two services,
# so it needs a role in each:
#
#   Azure Event Hubs Data Receiver   read events
#   Storage Blob Data Contributor    write ownership and checkpoint blobs
#
# With only the first, the processor starts cleanly, logs a storage failure
# through ProcessErrorAsync, claims no partitions, and reads nothing. It looks
# like an Event Hubs problem and is not one.
PRINCIPAL_ID="$(az ad signed-in-user show --query id --output tsv)"

NAMESPACE_SCOPE="$(az eventhubs namespace show \
  --name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id \
  --output tsv)"

STORAGE_SCOPE="$(az storage account show \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --query id \
  --output tsv)"

az role assignment create \
  --assignee-object-id "$PRINCIPAL_ID" \
  --assignee-principal-type User \
  --role "Azure Event Hubs Data Receiver" \
  --scope "$NAMESPACE_SCOPE" \
  --output none
echo "assigned: Azure Event Hubs Data Receiver on the namespace"

az role assignment create \
  --assignee-object-id "$PRINCIPAL_ID" \
  --assignee-principal-type User \
  --role "Storage Blob Data Contributor" \
  --scope "$STORAGE_SCOPE/blobServices/default/containers/$CHECKPOINT_CONTAINER" \
  --output none
echo "assigned: Storage Blob Data Contributor on the checkpoint container only"

# -----------------------------------------------------------------------------
step "6. Read the runtime information a consumer is judged against"
# -----------------------------------------------------------------------------
# lastEnqueuedSequenceNumber is the top of the log. Subtract the sequence number
# your consumer group last checkpointed and you have its lag — the same
# subtraction LagCalculator.Measure performs in the exercise.
#
# Nothing here reports the checkpoint: that number lives in YOUR storage
# account, in the blob metadata, and no Event Hubs API knows about it. Lag is a
# join between two services that only your code can perform.
for partition in 0 1 2 3; do
  az eventhubs eventhub partition show \
    --eventhub-name "$HUB_NAME" \
    --namespace-name "$NAMESPACE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --partition-id "$partition" \
    --query "{partition:partitionId, begin:beginSequenceNumber, lastEnqueued:lastEnqueuedSequenceNumber, lastEnqueuedTime:lastEnqueuedTimeUtc, empty:isEmpty}" \
    --output json
done

echo "-- the platform's own view, which the emulator does not emit at all"
az monitor metrics list \
  --resource "$NAMESPACE_SCOPE" \
  --metric IncomingMessages OutgoingMessages \
  --interval PT1M \
  --output table \
  || echo "(no metrics yet: a namespace under a few minutes old has nothing to report)"

# -----------------------------------------------------------------------------
step "7. Inspect the checkpoint container the way an operator would"
# -----------------------------------------------------------------------------
# After a processor has run against this namespace, the container holds one
# ownership blob and one checkpoint blob per partition per consumer group, under
# the path <namespace>/<hub>/<consumer-group>/. The position is in the METADATA;
# the blobs themselves are empty, which is why a naive 'list blobs, look at
# sizes' inspection concludes that nothing is there.
az storage blob list \
  --container-name "$CHECKPOINT_CONTAINER" \
  --account-name "$STORAGE_ACCOUNT" \
  --auth-mode login \
  --include m \
  --query "[].{name:name, bytes:properties.contentLength, metadata:metadata}" \
  --output json

echo
echo "To run the lesson companion against THIS namespace instead of the emulator:"
printf '  EVENTHUBS_CONNECTION_STRING="$(az eventhubs namespace authorization-rule keys list --resource-group %s --namespace-name %s --name RootManageSharedAccessKey --query primaryConnectionString --output tsv)"\n' \
  "$RESOURCE_GROUP" "$NAMESPACE_NAME"
printf '  STORAGE_CONNECTION_STRING="$(az storage account show-connection-string --resource-group %s --name %s --query connectionString --output tsv)"\n' \
  "$RESOURCE_GROUP" "$STORAGE_ACCOUNT"
echo "  EVENTHUBS_NAME=$HUB_NAME"

# -----------------------------------------------------------------------------
step "8. Delete everything"
# -----------------------------------------------------------------------------
# Both meters stop here: the namespace's hourly throughput-unit charge and the
# storage account's capacity and transaction charges. A checkpoint container is
# cheap to keep and expensive to forget, because a stale checkpoint pointing
# into an expired retention window is a consumer that will not start.
az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --output none

echo
echo "Deleted resource group $RESOURCE_GROUP. Verify with:"
echo "  az group exists --name $RESOURCE_GROUP"
