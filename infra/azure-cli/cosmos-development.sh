#!/usr/bin/env bash
# =============================================================================
# module.cosmos-development -- live checkpoint, Azure CLI
# =============================================================================
#
# Creates a Cosmos DB account with a deliberately small throughput budget, runs
# the lesson companion against it, and reads the two things the emulator cannot
# produce: real pages and real 429s.
#
#   bash infra/azure-cli/cosmos-development.sh
#
# The PowerShell twin infra/powershell/cosmos-development.ps1 performs the same
# steps in the same order with the same names, so the two can be read side by
# side.
#
# WHY THIS CHECKPOINT IS REQUIRED. Module 10's checkpoint was about cost. This
# one is about behaviour, and four behaviours central to this module have no
# local equivalent at all:
#
#   * Pagination. The emulator ignores MaxItemCount and returns every match in
#     one page with a null continuation token, no matter how many documents are
#     involved. The drain loop the exercise builds is therefore never exercised
#     locally past its first iteration (step 5).
#   * Throttling. There is no rate limiter locally. Eight hundred concurrent
#     writes against a container provisioned for 400 RU/s all succeed, so 429,
#     the x-ms-retry-after-ms header, and every retry policy written against
#     them are untested until they run here (step 6).
#   * Time-to-live. The emulator accepts a TTL and never acts on it, so
#     "let the service delete it" cannot be observed (step 7).
#   * Consistency. Session tokens, and the read charge that Strong consistency
#     roughly doubles, are account-level behaviour with no local switch (step 8).
#
# COST: the account below is provisioned rather than serverless, because a
# serverless account has no throughput ceiling to exceed and therefore cannot
# demonstrate 429. Provisioned throughput bills by the hour: 400 RU/s is roughly
# USD 0.008 per hour, so a thirty-minute run costs well under a cent. Storage
# for a few hundred small documents is negligible. Step 9 deletes everything.
# If the script is interrupted:
#
#   az group delete --name rg-expedition-checkpoint --yes --no-wait
#
# PREREQUISITES: Azure CLI 2.60+ and an authenticated session. This script never
# calls 'az login' for you -- sign in yourself so you can see which identity and
# subscription you are about to spend money in.
# =============================================================================

set -euo pipefail

LOCATION="${LOCATION:-westeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-expedition-checkpoint}"
ACCOUNT_NAME="${ACCOUNT_NAME:-cosmosdataplane$RANDOM$RANDOM}"
DATABASE_NAME="expedition-journal"
CONTAINER_NAME="readings"

# A Cosmos account name becomes a DNS label: 3-44 characters, lower-case
# letters, digits and hyphens, and it must be globally unique.
ACCOUNT_NAME="$(printf '%s' "$ACCOUNT_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-' | cut -c1-44)"

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
step "2. Create the account and database"
# -----------------------------------------------------------------------------
# Session is the default consistency level and the one an application developer
# has to understand, because it is the one with a token: a client reads its own
# writes, and two clients may briefly disagree. Step 8 changes it.
az cosmosdb create \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --locations regionName="$LOCATION" failoverPriority=0 isZoneRedundant=False \
  --default-consistency-level Session \
  --kind GlobalDocumentDB \
  --tags expedition=field-journal environment=checkpoint managed-by=learning-azure \
  --output table

az cosmosdb sql database create \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DATABASE_NAME" \
  --output table

# -----------------------------------------------------------------------------
step "3. Create the container the companion uses"
# -----------------------------------------------------------------------------
# 400 RU/s is the minimum a provisioned container can have, and it is chosen
# here precisely because it is easy to exceed. A container generous enough never
# to throttle would hide the behaviour this checkpoint exists to show.
#
# --ttl -1 enables time-to-live without expiring anything: documents live
# forever unless one of them carries its own /ttl field. Step 7 changes that.
az cosmosdb sql container create \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$CONTAINER_NAME" \
  --partition-key-path "/stationId" \
  --throughput 400 \
  --ttl -1 \
  --output table

echo "-- what the service records about the container it just made"
az cosmosdb sql container show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$CONTAINER_NAME" \
  --query "resource.{id:id, partitionKey:partitionKey.paths, defaultTtl:defaultTtl, conflictResolution:conflictResolutionPolicy.mode}" \
  --output json

# The conflict resolution policy is LastWriterWins on /_ts by default. It only
# ever applies to multi-region write accounts -- a single-region account
# serialises writes at the primary, which is exactly why an ETag is the only
# tool available for the single-region races the companion demonstrates.

# -----------------------------------------------------------------------------
step "4. Read the account's own limits"
# -----------------------------------------------------------------------------
az cosmosdb sql container throughput show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$CONTAINER_NAME" \
  --query "resource.{throughput:throughput, minimum:minimumThroughput}" \
  --output json

echo "-- 400 RU/s is roughly 400 point reads, or 80 writes, per second"

# -----------------------------------------------------------------------------
step "5. Run the companion here, and watch it page"
# -----------------------------------------------------------------------------
# Everything above is control plane. Pagination is a DATA plane behaviour, so it
# is observed by running the companion against this account rather than by an
# az command. These are the exports that point it here.
ENDPOINT="$(az cosmosdb show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query documentEndpoint \
  --output tsv)"

# The companion authenticates with an account key because that is the credential
# the Cosmos emulator accepts, and the code is unchanged between the two runs.
# A Cosmos primary master key is an account-wide root credential: it grants full
# read/write over every database in the account and cannot be scoped down. Keep
# it in the shell variable below, never in a file, and let the resource group
# deletion in the last step revoke it. Module 12 and the capstone lab show the
# production posture instead: `--disable-local-auth true` on the account, a
# Cosmos DB data-plane role assignment, and `DefaultAzureCredential` in the app.
echo
echo "To run the lesson companion against THIS account instead of the emulator:"
printf '  export COSMOS_ENDPOINT="%s"\n' "$ENDPOINT"
printf '  export COSMOS_KEY="$(az cosmosdb keys list --resource-group %s --name %s --query primaryMasterKey --output tsv)"\n' \
  "$RESOURCE_GROUP" "$ACCOUNT_NAME"
echo "  dotnet run --project lessons/11-cosmos-development/DataPlane"
echo
echo "Section 2 printed 'Pages returned: 1' against the emulator. Here it prints"
echo "5, with a continuation token several hundred characters long. Record both"
echo "numbers: the code did not change, and its behaviour did."
echo
echo "Section 1 is worth a second look too. Locally the point read and the query"
echo "both cost 1.00 RU. Here the query costs more, and that gap is the reason"
echo "ReadItemAsync exists as a separate method at all."

# -----------------------------------------------------------------------------
step "6. Provoke a 429, and read it off the platform's own meters"
# -----------------------------------------------------------------------------
# A 400 RU/s container refuses work once the budget for the second is spent. The
# emulator has no such budget, so no local test can produce this.
#
# The load below is deliberately generated with the SDK rather than the CLI: the
# CLI has no data plane for Cosmos SQL, which is itself worth knowing. Run the
# snippet the companion prints, or simply run the companion's seed loop with
# Readings raised to a few thousand.
echo
echo "With COSMOS_ENDPOINT and COSMOS_KEY exported, generate more load than the"
echo "container is provisioned for:"
echo
echo "  dotnet run --project lessons/11-cosmos-development/DataPlane"
echo
echo "then give the platform two or three minutes and read the meters."

ACCOUNT_SCOPE="$(az cosmosdb show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id \
  --output tsv)"

echo
echo "-- total request units consumed, per minute"
az monitor metrics list \
  --resource "$ACCOUNT_SCOPE" \
  --metric TotalRequestUnits \
  --interval PT1M \
  --output table \
  || echo "(no metrics yet: an account under a few minutes old has nothing to report)"

echo
echo "-- requests split by status code; 429 is the one that matters"
az monitor metrics list \
  --resource "$ACCOUNT_SCOPE" \
  --metric TotalRequests \
  --interval PT1M \
  --filter "StatusCode eq '*'" \
  --output table \
  || echo "(no metrics yet)"

# A 429 is not an error in the sense a 500 is: it is flow control, and the SDK
# retries it for you nine times within thirty seconds by default. That default
# is why a throttled application usually presents as latency rather than as
# failures, and why the retry bounds belong in the client options rather than in
# a comment.

# -----------------------------------------------------------------------------
step "7. Let the service do the deleting"
# -----------------------------------------------------------------------------
# A default TTL of 300 seconds means every document without its own /ttl field
# is deleted five minutes after its last write. The service spends leftover
# throughput doing it, so it does not compete with the application -- which is
# the entire argument against deleting a million documents one call at a time.
az cosmosdb sql container update \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$CONTAINER_NAME" \
  --ttl 300 \
  --output none
echo "set a 300-second default time-to-live on $CONTAINER_NAME"

az cosmosdb sql container show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$CONTAINER_NAME" \
  --query "resource.{id:id, defaultTtl:defaultTtl}" \
  --output json

echo "-- wait five minutes and query the container: it empties itself, and the"
echo "   application was charged nothing for the deletions"

# -----------------------------------------------------------------------------
step "8. Change what a read is allowed to see"
# -----------------------------------------------------------------------------
# Strong consistency makes every read return the latest committed write, at
# roughly twice the RU charge and with a latency cost. It is available only
# because this account is single-region; a multi-region write account cannot
# offer it at all.
az cosmosdb update \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --default-consistency-level Strong \
  --output none
echo "switched the account to Strong consistency"

az cosmosdb show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{consistency:consistencyPolicy.defaultConsistencyLevel, staleness:consistencyPolicy.maxStalenessPrefix}" \
  --output json

az cosmosdb update \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --default-consistency-level Session \
  --output none
echo "switched it back to Session"

# Run the companion once under each level and compare the RU charges in section
# 1. The difference is the price of never having to think about a session token.

# -----------------------------------------------------------------------------
step "9. Delete everything"
# -----------------------------------------------------------------------------
# Provisioned throughput bills by the hour whether or not a single request is
# made. An idle container at 400 RU/s costs about USD 6 a month to hold nothing.
az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --output none

echo
echo "Deleted resource group $RESOURCE_GROUP. Verify with:"
echo "  az group exists --name $RESOURCE_GROUP"
