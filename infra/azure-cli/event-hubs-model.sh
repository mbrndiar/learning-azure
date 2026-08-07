#!/usr/bin/env bash
# =============================================================================
# module.event-hubs-model — live checkpoint, Azure CLI
# =============================================================================
#
# Creates ONE Event Hubs namespace and one hub, inspects the partitions,
# reconfigures everything that is configurable, proves that the partition count
# is not, and deletes it all.
#
#   bash infra/azure-cli/event-hubs-model.sh
#
# The PowerShell twin infra/powershell/event-hubs-model.ps1 performs the same
# steps in the same order with the same names, so the two can be read side by
# side.
#
# WHY THIS CHECKPOINT IS REQUIRED. The Event Hubs emulator has no control plane
# at all: it reads infra/local/eventhubs/config.json at container start and
# exposes no way to create, resize, or reconfigure anything. Every capacity
# decision this module teaches — throughput units, retention, consumer groups,
# and above all the immutability of the partition count — is therefore invisible
# locally. Step 6 is the only place in this course where you watch Azure refuse
# a change.
#
# COST: a Standard-tier namespace is billed per throughput unit per hour, at
# roughly USD 0.03 per TU-hour, plus USD 0.028 per million ingress events. One
# TU for the ten minutes this script runs is well under USD 0.01, and the
# namespace is deleted at the end. Step 8 is not optional. If the script is
# interrupted, delete the resource group by hand:
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
HUB_NAME="telemetry"
CONSUMER_GROUP="field-journal"

# A namespace name becomes a DNS label: 6-50 characters, letters, digits, and
# hyphens, starting with a letter.
NAMESPACE_NAME="$(printf '%s' "$NAMESPACE_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-' | cut -c1-50)"

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
# Everything this script creates lands here, so step 8 removes all of it with
# one command. A resource created outside this group survives the teardown and
# keeps billing — and an Event Hubs namespace bills by the hour whether or not
# a single event is ever published to it.
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "2. Create the namespace: the unit of capacity and of billing"
# -----------------------------------------------------------------------------
# The namespace, not the hub, owns throughput. Every hub inside it shares these
# throughput units, so a noisy hub starves a quiet one.
#
#   --sku Standard        20 consumer groups per hub, up to 7 days of retention
#   --capacity 1          one throughput unit: 1 MB/s or 1,000 events/s ingress
#   --enable-auto-inflate raise capacity automatically instead of throttling
#   --maximum-throughput-units 3   the ceiling auto-inflate may not exceed
#
# Auto-inflate only ever scales UP. It is a throttling guard, not a cost
# control: the maximum is the number you are agreeing to pay for.
az eventhubs namespace create \
  --name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard \
  --capacity 1 \
  --enable-auto-inflate true \
  --maximum-throughput-units 3 \
  --minimum-tls-version 1.2 \
  --disable-local-auth false \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "3. Create the hub: the partition count is decided here, once"
# -----------------------------------------------------------------------------
# --partition-count is the only parameter on this command that cannot be
# changed afterwards on Basic or Standard. Four partitions matches
# infra/local/eventhubs/config.json so the emulator and the live hub agree.
#
# --retention-time 1 is the shortest window Standard allows. Retention decides
# how far a replay can reach and how much storage the namespace's throughput
# units have to cover (84 GB per TU).
az eventhubs eventhub create \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --partition-count 4 \
  --cleanup-policy Delete \
  --retention-time 1 \
  --output table

# -----------------------------------------------------------------------------
step "4. Inspect the hub: the partitions the SDK will report"
# -----------------------------------------------------------------------------
# These are the same partition ids that GetEventHubPropertiesAsync returns in
# lessons/08-event-hubs-model/TelemetryStream, and the same count the exercise's
# PartitionKeyPlanner.Spread is asked to place keys over.
az eventhubs eventhub show \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{partitions:partitionCount, partitionIds:partitionIds, retentionHours:retentionDescription.retentionTimeInHours, cleanup:retentionDescription.cleanupPolicy, status:status}" \
  --output json

# -----------------------------------------------------------------------------
step "5. Reconfigure everything that IS configurable"
# -----------------------------------------------------------------------------
# Retention, throughput units, and consumer groups are all live dials. None of
# them requires a restart, a migration, or a maintenance window.

echo "-- retention 1 day -> 3 days"
az eventhubs eventhub update \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --retention-time 3 \
  --query "{retentionHours:retentionDescription.retentionTimeInHours}" \
  --output json

echo "-- throughput units 1 -> 2"
az eventhubs namespace update \
  --name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --capacity 2 \
  --query "{sku:sku.name, capacity:sku.capacity}" \
  --output json

echo "-- add a consumer group (each one reads the WHOLE stream, so egress doubles)"
az eventhubs eventhub consumer-group create \
  --name "$CONSUMER_GROUP" \
  --eventhub-name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --output table

az eventhubs eventhub consumer-group list \
  --eventhub-name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "[].name" \
  --output tsv

# -----------------------------------------------------------------------------
step "6. Try to change the one thing that cannot be changed"
# -----------------------------------------------------------------------------
# On Basic and Standard the partition count is fixed at creation. There is no
# flag, no support request, and no scale operation: the only route to a
# different partition count is a NEW hub and a migration that re-reads the
# stream.
#
# The command below is expected to FAIL, or — worse and more instructive — to
# report success while leaving partitionCount unchanged. Step 6b reads the value
# back rather than trusting the response, which is the habit this step exists to
# build.
if az eventhubs eventhub update \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --partition-count 8 \
  --output none 2>/dev/null; then
  echo "the update call returned success — now check whether anything happened"
else
  echo "the update call was rejected outright"
fi

echo "-- 6b. read the partition count back"
ACTUAL_PARTITIONS="$(az eventhubs eventhub show \
  --name "$HUB_NAME" \
  --namespace-name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "partitionCount" \
  --output tsv)"

printf 'partition count is still: %s\n' "$ACTUAL_PARTITIONS"
[[ "$ACTUAL_PARTITIONS" == "4" ]] \
  && echo "confirmed: the number chosen in step 3 is the number you keep" \
  || echo "unexpected: this subscription's tier allowed the change"

# -----------------------------------------------------------------------------
step "7. Grant this identity data-plane roles"
# -----------------------------------------------------------------------------
# Control-plane rights (Owner, Contributor) let you create the hub above and do
# NOT let you publish a single event to it. Sending and receiving are two
# separate roles, which is what makes least privilege expressible here.
PRINCIPAL_ID="$(az ad signed-in-user show --query id --output tsv)"
SCOPE="$(az eventhubs namespace show \
  --name "$NAMESPACE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id \
  --output tsv)"

for role in "Azure Event Hubs Data Sender" "Azure Event Hubs Data Receiver"; do
  az role assignment create \
    --assignee-object-id "$PRINCIPAL_ID" \
    --assignee-principal-type User \
    --role "$role" \
    --scope "$SCOPE" \
    --output none
  printf 'assigned: %s\n' "$role"
done

echo
echo "To publish to this hub from the lesson companion, export:"
printf '  EVENTHUBS_CONNECTION_STRING="$(az eventhubs namespace authorization-rule keys list --resource-group %s --namespace-name %s --name RootManageSharedAccessKey --query primaryConnectionString --output tsv)"\n' \
  "$RESOURCE_GROUP" "$NAMESPACE_NAME"
echo "  EVENTHUBS_NAME=$HUB_NAME"
echo "(A connection string is the emulator's authentication model, not Azure's."
echo " Role assignments above are what a real workload uses; the exercise's"
echo " DefaultAzureCredential path is module 2.)"

# -----------------------------------------------------------------------------
step "8. Delete everything"
# -----------------------------------------------------------------------------
# A namespace bills per throughput unit per hour whether or not it carries
# traffic, so this step is the one that stops the meter. Deleting the resource
# group removes the namespace, the hub, the consumer group, the events still
# inside their retention window, and the role assignments scoped to them.
az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --output none

echo
echo "Deleted resource group $RESOURCE_GROUP. Verify with:"
echo "  az group exists --name $RESOURCE_GROUP"
