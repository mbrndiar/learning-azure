#!/usr/bin/env bash
# =============================================================================
# module.storage-account — live checkpoint, Azure CLI
# =============================================================================
#
# Creates ONE storage account, inspects it, reconfigures it, proves the Entra ID
# auth boundary, and deletes everything it made.
#
#   bash infra/azure-cli/storage-account.sh
#
# The PowerShell twin infra/powershell/storage-account.ps1 performs the same
# steps in the same order with the same names, so the two can be read side by
# side.
#
# COST: a general-purpose v2 account with a few kilobytes of data and a handful
# of transactions costs well under USD 0.01 for the minutes this script runs.
# The account is deleted at the end; step 9 is not optional. If the script is
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
ACCOUNT_NAME="${ACCOUNT_NAME:-stexpedition$RANDOM$RANDOM}"
CONTAINER_NAME="artifacts"

# The account name becomes a DNS label: 3-24 lowercase letters and digits.
ACCOUNT_NAME="$(printf '%s' "$ACCOUNT_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9' | cut -c1-24)"

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
# Everything this script creates lands here, so step 9 removes all of it with
# one command. A resource created outside this group survives the teardown and
# keeps billing.
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "2. Create the storage account on the security baseline"
# -----------------------------------------------------------------------------
# Every flag below is a decision, not a default:
#   --sku Standard_ZRS            three copies across three availability zones
#   --allow-shared-key-access     false: data-plane access requires Entra ID
#   --allow-blob-public-access    false: no container can be made anonymous
#   --https-only                  true:  plain HTTP is refused
#   --min-tls-version TLS1_2      no downgrade to TLS 1.0/1.1
#   --require-infrastructure-encryption   a second encryption layer at rest
az storage account create \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --kind StorageV2 \
  --sku Standard_ZRS \
  --access-tier Hot \
  --allow-shared-key-access false \
  --allow-blob-public-access false \
  --https-only true \
  --min-tls-version TLS1_2 \
  --require-infrastructure-encryption \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "3. Inspect the account: endpoints, redundancy, and the auth boundary"
# -----------------------------------------------------------------------------
# The account name is the leftmost DNS label of every service endpoint. This is
# the live counterpart of StorageEndpoints.For in the exercise.
az storage account show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{blob:primaryEndpoints.blob, queue:primaryEndpoints.queue, table:primaryEndpoints.table, sku:sku.name, tier:accessTier, sharedKey:allowSharedKeyAccess, publicBlob:allowBlobPublicAccess, tls:minimumTlsVersion}" \
  --output json

# -----------------------------------------------------------------------------
step "4. Grant this identity a data-plane role"
# -----------------------------------------------------------------------------
# Control-plane rights (Owner, Contributor) do NOT grant data-plane access when
# shared-key access is disabled. Without this assignment, step 6 fails with 403 —
# which is the single most useful thing this checkpoint demonstrates.
PRINCIPAL_ID="$(az ad signed-in-user show --query id --output tsv)"
SCOPE="$(az storage account show --name "$ACCOUNT_NAME" --resource-group "$RESOURCE_GROUP" --query id --output tsv)"

az role assignment create \
  --assignee-object-id "$PRINCIPAL_ID" \
  --assignee-principal-type User \
  --role "Storage Blob Data Contributor" \
  --scope "$SCOPE" \
  --output table

echo "Role assignments can take up to five minutes to propagate."
sleep 60

# -----------------------------------------------------------------------------
step "5. Create a container using Entra ID, not a key"
# -----------------------------------------------------------------------------
# --auth-mode login is what makes the CLI use your Entra identity. Omit it and
# the CLI reaches for an account key that no longer exists.
az storage container create \
  --name "$CONTAINER_NAME" \
  --account-name "$ACCOUNT_NAME" \
  --auth-mode login \
  --output table

# -----------------------------------------------------------------------------
step "6. Write and read one artifact over the data plane"
# -----------------------------------------------------------------------------
printf 'station-bravo observed ice shelf calving\n' > ./observation.txt

az storage blob upload \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo.txt" \
  --file ./observation.txt \
  --auth-mode login \
  --overwrite \
  --output table

az storage blob list \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --auth-mode login \
  --query "[].{name:name, tier:properties.blobTier, size:properties.contentLength}" \
  --output table

rm -f ./observation.txt

# -----------------------------------------------------------------------------
step "7. Reconfigure: move the account default tier to Cool"
# -----------------------------------------------------------------------------
# Azurite has no tiers at all, so this behavior cannot be observed locally. Note
# that the change applies to blobs with no explicit tier — existing blobs that
# inherited Hot move with it.
az storage account update \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --access-tier Cool \
  --output table

az storage account show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{tier:accessTier, sku:sku.name}" \
  --output json

# -----------------------------------------------------------------------------
step "8. Observe what the emulator cannot show you"
# -----------------------------------------------------------------------------
# Redundancy, the geo-replication state, and the last sync time have no Azurite
# equivalent. For a ZRS account there is no secondary; switch --sku to
# Standard_GRS in step 2 to see geoReplicationStats populated instead.
az storage account show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --expand geoReplicationStats \
  --query "{sku:sku.name, replication:sku.name, geo:geoReplicationStats}" \
  --output json

# -----------------------------------------------------------------------------
step "9. Delete everything (NOT optional)"
# -----------------------------------------------------------------------------
# One command, because step 1 put everything in one group. Drop --no-wait if you
# want to watch the deletion finish.
az group delete --name "$RESOURCE_GROUP" --yes --output table

echo
echo "Teardown requested. Confirm nothing survived:"
echo "  az resource list --tag managed-by=learning-azure --output table"
