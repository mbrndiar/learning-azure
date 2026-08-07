#!/usr/bin/env bash
# =============================================================================
# module.cosmos-modeling -- live checkpoint, Azure CLI
# =============================================================================
#
# Creates a Cosmos DB account, builds the two containers the lesson companion
# builds, reads the numbers the emulator refuses to produce -- real request
# charges, real throughput ceilings, real partition key ranges -- and deletes it
# all again.
#
#   bash infra/azure-cli/cosmos-modeling.sh
#
# The PowerShell twin infra/powershell/cosmos-modeling.ps1 performs the same
# steps in the same order with the same names, so the two can be read side by
# side.
#
# WHY THIS CHECKPOINT IS REQUIRED. This module is about cost, and the emulator
# does not model cost. Four things are simply not observable locally:
#
#   * Request charges. Every response the emulator returns is billed at 1 RU,
#     including a 200-document cross-partition query. The whole subject of this
#     module is the difference between those numbers (step 5).
#   * Query metrics. retrievedDocumentCount and indexUtilizationRatio come back
#     as zero from the emulator, so 'how much did the engine look at' has to be
#     inferred locally and can be read directly here (step 5).
#   * Physical partitions. The emulator reports exactly one feed range no matter
#     how much data is written, so a partition split -- the event that turns a
#     provisioned 400 RU/s into two shares of 200 -- cannot happen (step 6).
#   * Throttling. There is no rate limit locally, so 429 and its retry-after
#     header never appear (step 7).
#
# COST: a serverless Cosmos DB account bills per request and per GB-month, so a
# run of this script that writes a few hundred small documents costs a fraction
# of a cent. Step 4 briefly switches one container to provisioned throughput,
# which bills per hour: 400 RU/s is roughly USD 0.008 per hour, so ten minutes
# is well under a cent. Step 9 deletes everything. If the script is interrupted:
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
ACCOUNT_NAME="${ACCOUNT_NAME:-cosmosexpedition$RANDOM$RANDOM}"
DATABASE_NAME="expedition"
BY_STATION="readings-by-station"
BY_DAY="readings-by-day"

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
step "2. Create the account"
# -----------------------------------------------------------------------------
# Session consistency is the default and the one worth understanding: a client
# reads its own writes, and two clients may briefly disagree. Strong consistency
# is available only within a single region and roughly doubles the RU charge of
# every read, which is a modelling decision rather than a switch to flip late.
#
# --enable-free-tier is deliberately NOT used: an account can only be free-tier
# if the subscription has no other free-tier account, and a failed run would
# then block the next one for reasons that have nothing to do with this module.
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
step "3. Create the two containers the companion creates"
# -----------------------------------------------------------------------------
# The partition key path is fixed at creation. There is no 'az cosmosdb sql
# container update --partition-key-path', because changing it means moving every
# document into a different logical partition: it is a migration, not an edit.
# That single fact is why this module exists.
az cosmosdb sql container create \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --partition-key-path "/stationId" \
  --throughput 400 \
  --output table

az cosmosdb sql container create \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_DAY" \
  --partition-key-path "/day" \
  --throughput 400 \
  --output table

echo "-- what the service records about the containers it just made"
az cosmosdb sql container show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --query "resource.{id:id, partitionKey:partitionKey, indexingMode:indexingPolicy.indexingMode, includedPaths:indexingPolicy.includedPaths[].path, excludedPaths:indexingPolicy.excludedPaths[].path}" \
  --output json

# -----------------------------------------------------------------------------
step "4. Configure throughput: manual, autoscale, and back"
# -----------------------------------------------------------------------------
# Manual provisioning bills the number you set, every hour, whether or not the
# container is touched. Autoscale bills what was used at 1.5x the manual rate,
# and never less than 10% of the maximum -- which is the arithmetic
# ThroughputPlanner.RelativeAutoscaleCost performs in the exercise.
az cosmosdb sql container throughput show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --query "resource.{throughput:throughput, minimum:minimumThroughput, autoscale:autoscaleSettings.maxThroughput}" \
  --output json

az cosmosdb sql container throughput migrate \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --throughput-type autoscale \
  --output none
echo "migrated $BY_STATION to autoscale"

az cosmosdb sql container throughput show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --query "resource.{throughput:throughput, minimum:minimumThroughput, autoscaleMax:autoscaleSettings.maxThroughput}" \
  --output json

# minimumThroughput is the floor the service will not let you go below, and it
# rises with the data stored and with the number of physical partitions the
# container has ever had. It never comes back down. A container that was briefly
# scaled to 100,000 RU/s keeps a higher floor forever.

# -----------------------------------------------------------------------------
step "5. Read the numbers the emulator will not produce"
# -----------------------------------------------------------------------------
# Everything above is control plane. The request charge is a DATA plane header,
# so it is read by running the companion against this account rather than by an
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
echo "  dotnet run --project lessons/10-cosmos-modeling/RequestUnits"
echo
echo "Record the request charge printed for the point read, the single-partition"
echo "query and the cross-partition query. Locally all three are 1.00 RU. Here"
echo "they are not, and the ratio between them is the lesson."

# -----------------------------------------------------------------------------
step "6. Look at the partition key ranges"
# -----------------------------------------------------------------------------
# A physical partition is created by the service, not by you. A new container
# starts with one; it splits when it passes 50 GB or when the provisioned
# throughput exceeds what one partition can serve. Every split halves the
# throughput each partition may spend, which is why a container that was fine at
# 10,000 RU/s over two partitions can start throttling at 10,000 over four.
#
# The emulator reports exactly one range forever, so this behaviour has no local
# equivalent at all.
az cosmosdb sql container show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_DAY" \
  --query "resource.{id:id, partitionKey:partitionKey.paths, version:partitionKey.version}" \
  --output json

echo "-- partition key version 2 supports large (2 KB) key values; version 1 caps at 101 bytes"

# -----------------------------------------------------------------------------
step "7. Watch throttling and consumption on the platform's own meters"
# -----------------------------------------------------------------------------
# TotalRequestUnits is what you spent. TotalRequests split by status code 429 is
# what you were refused. Neither exists locally, because the emulator has no
# rate limiter: a load test against it proves nothing about capacity.
ACCOUNT_SCOPE="$(az cosmosdb show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id \
  --output tsv)"

az monitor metrics list \
  --resource "$ACCOUNT_SCOPE" \
  --metric TotalRequestUnits TotalRequests \
  --interval PT1M \
  --output table \
  || echo "(no metrics yet: an account under a few minutes old has nothing to report)"

# -----------------------------------------------------------------------------
step "8. Change an indexing policy, and see that it is asynchronous"
# -----------------------------------------------------------------------------
# Excluding a path is the only lever that makes writes cheaper. It is applied by
# a background reindex that leaves the container fully queryable throughout and
# consumes leftover RU/s while it runs, so on a busy container the saving does
# not appear immediately.
# The policy is passed inline. Excluding "/*" and then including only the two
# paths the workload filters on is the standard shape: it says "index nothing
# unless I asked for it" rather than "index everything except these".
INDEXING_POLICY='{
  "indexingMode": "consistent",
  "automatic": true,
  "includedPaths": [
    { "path": "/stationId/?" },
    { "path": "/day/?" }
  ],
  "excludedPaths": [
    { "path": "/*" }
  ]
}'

az cosmosdb sql container update \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --idx "$INDEXING_POLICY" \
  --output none
echo "applied a narrowed indexing policy to $BY_STATION"

az cosmosdb sql container show \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --database-name "$DATABASE_NAME" \
  --name "$BY_STATION" \
  --query "resource.indexingPolicy" \
  --output json

# -----------------------------------------------------------------------------
step "9. Delete everything"
# -----------------------------------------------------------------------------
# Provisioned throughput bills by the hour whether or not a single request is
# made, and it is the meter people forget: an idle container at 400 RU/s costs
# about USD 6 a month for holding nothing.
az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --output none

echo
echo "Deleted resource group $RESOURCE_GROUP. Verify with:"
echo "  az group exists --name $RESOURCE_GROUP"
