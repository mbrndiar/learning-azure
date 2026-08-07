#!/usr/bin/env bash
# =============================================================================
# module.blob-lifecycle — live checkpoint, Azure CLI
# =============================================================================
#
# Proves the three retention mechanisms that Azurite cannot emulate: blob
# versioning, soft delete, and lifecycle management rules. Creates ONE storage
# account, exercises each mechanism, and deletes everything it made.
#
#   bash infra/azure-cli/blob-lifecycle.sh
#
# The PowerShell twin infra/powershell/blob-lifecycle.ps1 performs the same
# steps in the same order with the same names, so the two can be read side by
# side.
#
# COST: a general-purpose v2 account holding a few kilobytes for the minutes
# this script runs costs well under USD 0.01. The account is deleted at the end;
# step 9 is not optional. If the script is interrupted, delete the resource
# group by hand:
#
#   az group delete --name rg-expedition-lifecycle --yes --no-wait
#
# PREREQUISITES: Azure CLI 2.60+ and an authenticated session. This script never
# calls 'az login' for you -- sign in yourself so you can see which identity and
# subscription you are about to spend money in.
# =============================================================================

set -euo pipefail

LOCATION="${LOCATION:-westeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-expedition-lifecycle}"
ACCOUNT_NAME="${ACCOUNT_NAME:-stlifecycle$RANDOM$RANDOM}"
CONTAINER_NAME="artifacts"
BLOB_NAME="observations/station-bravo/notes.txt"

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
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

# -----------------------------------------------------------------------------
step "2. Create the storage account and grant this identity data access"
# -----------------------------------------------------------------------------
# Same security baseline as module 3: no shared keys, no anonymous access, TLS
# 1.2 minimum. Data-plane access therefore requires an Entra ID role.
az storage account create \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --kind StorageV2 \
  --sku Standard_LRS \
  --access-tier Hot \
  --allow-shared-key-access false \
  --allow-blob-public-access false \
  --https-only true \
  --min-tls-version TLS1_2 \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

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
step "3. Turn on the three retention mechanisms"
# -----------------------------------------------------------------------------
# These are three INDEPENDENT promises, and each covers a different loss:
#   --enable-versioning        an overwrite keeps the previous bytes
#   --enable-delete-retention  a deleted blob stays recoverable for N days
#   --enable-container-delete-retention  the same for a deleted container
# Soft delete does not cover overwrites. Versioning does not cover deletes.
az storage account blob-service-properties update \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --enable-versioning true \
  --enable-delete-retention true \
  --delete-retention-days 7 \
  --enable-container-delete-retention true \
  --container-delete-retention-days 7 \
  --output json

az storage container create \
  --name "$CONTAINER_NAME" \
  --account-name "$ACCOUNT_NAME" \
  --auth-mode login \
  --output table

# -----------------------------------------------------------------------------
step "4. Watch an overwrite create a version instead of destroying data"
# -----------------------------------------------------------------------------
printf 'temp=-3C\n' > ./notes.txt
az storage blob upload \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --file ./notes.txt \
  --auth-mode login \
  --overwrite \
  --output none

printf 'temp=-3C;ice=thin\n' > ./notes.txt
az storage blob upload \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --file ./notes.txt \
  --auth-mode login \
  --overwrite \
  --output none

# Every version, oldest first. This listing is the thing Azurite cannot produce.
az storage blob list \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --prefix "$BLOB_NAME" \
  --include v \
  --auth-mode login \
  --query "[].{name:name, versionId:versionId, current:isCurrentVersion, size:properties.contentLength}" \
  --output table

# -----------------------------------------------------------------------------
step "5. Write conditionally and watch the service refuse a stale write"
# -----------------------------------------------------------------------------
# --if-match is the CLI spelling of the header the exercise sends. The second
# upload uses a deliberately stale ETag and must fail with 412.
ETAG="$(az storage blob show \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --auth-mode login \
  --query properties.etag --output tsv)"

printf 'temp=-3C;ice=thin;wind=12kt\n' > ./notes.txt
az storage blob upload \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --file ./notes.txt \
  --auth-mode login \
  --if-match "$ETAG" \
  --overwrite \
  --output none
echo "conditional write with the current ETag: accepted"

printf 'temp=-3C;visibility=poor\n' > ./notes.txt
if az storage blob upload \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --file ./notes.txt \
  --auth-mode login \
  --if-match "$ETAG" \
  --overwrite \
  --output none 2>/dev/null
then
  echo "UNEXPECTED: the stale conditional write succeeded. Investigate before continuing."
else
  echo "conditional write with the STALE ETag: refused (412 ConditionNotMet), as designed"
fi

rm -f ./notes.txt

# -----------------------------------------------------------------------------
step "6. Delete the blob, then undelete it"
# -----------------------------------------------------------------------------
# This is what soft delete buys, and it is the only step in this script that
# cannot be rehearsed anywhere but a real account.
az storage blob delete \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --auth-mode login \
  --output none

az storage blob list \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --include d \
  --auth-mode login \
  --query "[].{name:name, deleted:deleted, remainingDays:properties.remainingRetentionDays}" \
  --output table

az storage blob undelete \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --auth-mode login \
  --output none

az storage blob show \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name "$BLOB_NAME" \
  --auth-mode login \
  --query "{name:name, deleted:deleted, size:properties.contentLength}" \
  --output json

# -----------------------------------------------------------------------------
step "7. Install a lifecycle management policy"
# -----------------------------------------------------------------------------
# The rule is data, not code: JSON evaluated by the service once a day. It never
# runs while you watch, which is exactly why the plan has to be right on paper
# before it is installed. This is the live counterpart of RetentionPlanner.
cat > ./lifecycle.json <<'JSON'
{
  "rules": [
    {
      "enabled": true,
      "name": "expedition-artifact-cooling",
      "type": "Lifecycle",
      "definition": {
        "filters": {
          "blobTypes": [ "blockBlob" ],
          "prefixMatch": [ "artifacts/observations/" ]
        },
        "actions": {
          "baseBlob": {
            "tierToCool": { "daysAfterModificationGreaterThan": 30 },
            "tierToArchive": { "daysAfterModificationGreaterThan": 180 },
            "delete": { "daysAfterModificationGreaterThan": 2555 }
          },
          "version": {
            "delete": { "daysAfterCreationGreaterThan": 90 }
          }
        }
      }
    }
  ]
}
JSON

az storage account management-policy create \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --policy @./lifecycle.json \
  --output json

rm -f ./lifecycle.json

# -----------------------------------------------------------------------------
step "8. Read the policy back and check it against the plan"
# -----------------------------------------------------------------------------
# Cool has a 30-day minimum and Archive a 180-day one. The transitions above sit
# exactly on those boundaries, which is what RetentionPlanner.Evaluate checks.
az storage account management-policy show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "policy.rules[].{rule:name, cool:definition.actions.baseBlob.tierToCool.daysAfterModificationGreaterThan, archive:definition.actions.baseBlob.tierToArchive.daysAfterModificationGreaterThan, deleteVersions:definition.actions.version.delete.daysAfterCreationGreaterThan}" \
  --output table

# -----------------------------------------------------------------------------
step "9. Delete everything (NOT optional)"
# -----------------------------------------------------------------------------
# One command, because step 1 put everything in one group. Note that container
# soft delete does not keep a deleted resource group alive: deleting the group
# deletes the account and everything the retention settings were protecting.
az group delete --name "$RESOURCE_GROUP" --yes --output table

echo
echo "Teardown requested. Confirm nothing survived:"
echo "  az resource list --tag managed-by=learning-azure --output table"
