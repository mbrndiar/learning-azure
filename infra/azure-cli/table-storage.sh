#!/usr/bin/env bash
# =============================================================================
# module.table-storage — emulator lab, Azure CLI
# =============================================================================
#
# Drives the table data plane end to end against Azurite: table, insert, point
# read, partition scan, table scan, optimistic concurrency, teardown.
#
#   docker compose up -d azurite
#   bash infra/azure-cli/table-storage.sh
#
# The PowerShell twin infra/powershell/table-storage.ps1 performs the same steps
# in the same order with the same names, so the two can be read side by side.
#
# COST: none. Every command below talks to 127.0.0.1:10002, not to Azure. The
# well-known Azurite account name and key are emulator-only credentials; they
# grant access to nothing outside this machine, which is why they may appear in
# source. A real account key must never be written down like this.
#
# TO RUN THE SAME STEPS AGAINST AZURE instead of the emulator, export
# AZURE_STORAGE_AUTH_MODE=login and AZURE_STORAGE_ACCOUNT=<your account>, then
# unset AZURE_STORAGE_CONNECTION_STRING. Every 'az storage' call below then goes
# through your Entra ID identity and needs the Storage Table Data Contributor
# role on the account. See infra/azure-cli/storage-account.sh, which creates an
# account configured exactly that way.
#
# NOTE: two rules exercised here — the single-partition transaction limit and
# the 100-operation batch limit — are NOT enforced by Azurite. See step 8.
#
# PREREQUISITES: Azure CLI 2.60+. The emulator path needs Azurite; the Azure path
# needs `az login`, AZURE_STORAGE_ACCOUNT, and the data-plane role above.
# =============================================================================

set -euo pipefail

AZURITE_ACCOUNT="devstoreaccount1"
AZURITE_KEY="Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="

if [[ -n "${AZURE_STORAGE_ACCOUNT:-}" ]]; then
  export AZURE_STORAGE_AUTH_MODE="${AZURE_STORAGE_AUTH_MODE:-login}"
  unset AZURE_STORAGE_CONNECTION_STRING
else
  export AZURE_STORAGE_CONNECTION_STRING="${AZURITE_CONNECTION_STRING:-DefaultEndpointsProtocol=http;AccountName=$AZURITE_ACCOUNT;AccountKey=$AZURITE_KEY;TableEndpoint=http://127.0.0.1:10002/$AZURITE_ACCOUNT;}"
fi

TABLE_NAME="expeditionobservations"
PARTITION_BRAVO="station-bravo|2026-07-06"
PARTITION_DELTA="station-delta|2026-07-06"

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

# -----------------------------------------------------------------------------
step "0. Confirm the endpoint that is about to be written to"
# -----------------------------------------------------------------------------
# Printing the endpoint, not the key. If this does not say 127.0.0.1 you are
# about to write to a real account.
printf 'table endpoint : %s\n' \
  "$(printf '%s' "$AZURE_STORAGE_CONNECTION_STRING" | tr ';' '\n' | grep '^TableEndpoint=' || echo 'TableEndpoint=(default, i.e. Azure)')"

# -----------------------------------------------------------------------------
step "1. Create the table"
# -----------------------------------------------------------------------------
# A table name is alphanumeric only: no hyphens, no underscores. It also has no
# schema, so this is the last structural decision the service makes for you.
az storage table create \
  --name "$TABLE_NAME" \
  --output table

# -----------------------------------------------------------------------------
step "2. Insert observations across two partitions"
# -----------------------------------------------------------------------------
# The partition key is station AND day. The row key is a fixed-width UTC
# timestamp, because row keys sort ascending as STRINGS: '9:05' would sort after
# '10:05' and every range query would be silently wrong.
for minute in 00 05 10; do
  az storage entity insert \
    --table-name "$TABLE_NAME" \
    --entity \
      PartitionKey="$PARTITION_BRAVO" \
      RowKey="2026-07-06T12:$minute:00.0000000Z" \
      StationId=station-bravo \
      TemperatureC@double=-3.5 \
      Status=pending \
    --output none
done

az storage entity insert \
  --table-name "$TABLE_NAME" \
  --entity \
    PartitionKey="$PARTITION_DELTA" \
    RowKey="2026-07-06T12:00:00.0000000Z" \
    StationId=station-delta \
    TemperatureC@double=-7.25 \
    Status=pending \
  --output none

echo "inserted 4 entities into 2 partitions"

# -----------------------------------------------------------------------------
step "3. Point read: both keys known"
# -----------------------------------------------------------------------------
# One entity, one lookup, and a cost that does not change as the table grows.
# This is the only query shape worth designing keys around.
az storage entity show \
  --table-name "$TABLE_NAME" \
  --partition-key "$PARTITION_BRAVO" \
  --row-key "2026-07-06T12:05:00.0000000Z" \
  --output json

# -----------------------------------------------------------------------------
step "4. Partition scan: partition key only"
# -----------------------------------------------------------------------------
# Bounded by the partition, which is why the partition key carries the day: one
# station reporting every minute would otherwise grow one partition forever.
az storage entity query \
  --table-name "$TABLE_NAME" \
  --filter "PartitionKey eq '$PARTITION_BRAVO'" \
  --query "items[].RowKey" \
  --output tsv

# -----------------------------------------------------------------------------
step "5. Table scan: the query that looks identical and is not"
# -----------------------------------------------------------------------------
# Same rows, same syntax, no PartitionKey predicate. StationId is a duplicated
# column, not a key, so the service reads every row in the table to find these.
az storage entity query \
  --table-name "$TABLE_NAME" \
  --filter "StationId eq 'station-bravo'" \
  --query "items[].RowKey" \
  --output tsv

echo "same result, whole-table cost: this is the mistake that only shows up at scale"

# -----------------------------------------------------------------------------
step "6. A key range query, done with the row key"
# -----------------------------------------------------------------------------
# Because row keys are fixed-width and sorted, a range is expressible as a
# string comparison against the key rather than as a filter on a property.
az storage entity query \
  --table-name "$TABLE_NAME" \
  --filter "PartitionKey eq '$PARTITION_BRAVO' and RowKey ge '2026-07-06T12:05:00.0000000Z'" \
  --query "items[].RowKey" \
  --output tsv

# -----------------------------------------------------------------------------
step "7. Optimistic concurrency with the entity ETag"
# -----------------------------------------------------------------------------
# Read the version, then write betting on it. A merge with '--if-match "*"'
# would be the last-write-wins default that module 5 spent a whole module
# removing.
ETAG=$(az storage entity show \
  --table-name "$TABLE_NAME" \
  --partition-key "$PARTITION_BRAVO" \
  --row-key "2026-07-06T12:00:00.0000000Z" \
  --query "etag" \
  --output tsv)

printf 'read etag : %s\n' "$ETAG"

az storage entity merge \
  --table-name "$TABLE_NAME" \
  --entity \
    PartitionKey="$PARTITION_BRAVO" \
    RowKey="2026-07-06T12:00:00.0000000Z" \
    Status=ingested \
  --if-match "$ETAG" \
  --output none

echo "first write with a fresh etag: accepted"

if az storage entity merge \
  --table-name "$TABLE_NAME" \
  --entity \
    PartitionKey="$PARTITION_BRAVO" \
    RowKey="2026-07-06T12:00:00.0000000Z" \
    Status=rejected \
  --if-match "$ETAG" \
  --output none 2>/dev/null; then
  echo "unexpected: the stale etag was accepted"
else
  echo "second write with the SAME (now stale) etag: rejected, HTTP 412"
fi

# -----------------------------------------------------------------------------
step "8. What the emulator does not enforce"
# -----------------------------------------------------------------------------
# Two rules this module teaches are NOT enforced by Azurite: a transactional
# batch may not span partitions, and it may not exceed 100 operations. Azure
# rejects both with InvalidInput; Azurite accepts the first and returns an
# unparseable response for the second.
#
# This is why the exercise validates them in your own code. Run
#   dotnet run --project lessons/07-table-storage/ObservationIndex
# to see both divergences reported from a real call.
echo "see lessons/07-table-storage/README.md#what-the-emulator-will-not-tell-you"

# -----------------------------------------------------------------------------
step "9. Delete the table"
# -----------------------------------------------------------------------------
# One delete removes every entity in it. Against a real account this is the step
# that stops the bill, so it is never optional.
az storage table delete \
  --name "$TABLE_NAME" \
  --output table

echo
echo "Done. Nothing remains in the emulator."
