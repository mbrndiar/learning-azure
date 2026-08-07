#!/usr/bin/env bash
# =============================================================================
# module.blob-storage — emulator lab, Azure CLI
# =============================================================================
#
# Drives the blob data plane end to end against Azurite: container, upload,
# metadata, tags, prefix listing, hierarchical listing, download, teardown.
#
#   docker compose up -d azurite
#   bash infra/azure-cli/blob-storage.sh
#
# The PowerShell twin infra/powershell/blob-storage.ps1 performs the same steps
# in the same order with the same names, so the two can be read side by side.
#
# COST: none. Every command below talks to 127.0.0.1:10000, not to Azure. The
# well-known Azurite account name and key are emulator-only credentials; they
# grant access to nothing outside this machine, which is why they may appear in
# source. A real account key must never be written down like this.
#
# TO RUN THE SAME STEPS AGAINST AZURE instead of the emulator, export
# AZURE_STORAGE_AUTH_MODE=login and AZURE_STORAGE_ACCOUNT=<your account>, then
# unset AZURE_STORAGE_CONNECTION_STRING. Every 'az storage' call below then goes
# through your Entra ID identity and needs the Storage Blob Data Contributor
# role on the account. See infra/azure-cli/storage-account.sh, which creates an
# account configured exactly that way.
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
  export AZURE_STORAGE_CONNECTION_STRING="${AZURITE_CONNECTION_STRING:-DefaultEndpointsProtocol=http;AccountName=$AZURITE_ACCOUNT;AccountKey=$AZURITE_KEY;BlobEndpoint=http://127.0.0.1:10000/$AZURITE_ACCOUNT;}"
fi

CONTAINER_NAME="expedition-artifacts"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

# -----------------------------------------------------------------------------
step "0. Confirm the endpoint that is about to be written to"
# -----------------------------------------------------------------------------
# Printing the endpoint, not the key. If this does not say 127.0.0.1 you are
# about to write to a real account.
printf 'blob endpoint : %s\n' \
  "$(printf '%s' "$AZURE_STORAGE_CONNECTION_STRING" | tr ';' '\n' | grep '^BlobEndpoint=' || echo 'BlobEndpoint=(default, i.e. Azure)')"

az storage account show-connection-string --output none 2>/dev/null || true

# -----------------------------------------------------------------------------
step "1. Create the container"
# -----------------------------------------------------------------------------
# A container is the only real grouping level. Everything below it is one flat
# namespace of names, so this is also the last real directory you get.
az storage container create \
  --name "$CONTAINER_NAME" \
  --output table

# -----------------------------------------------------------------------------
step "2. Upload blobs whose names only look like paths"
# -----------------------------------------------------------------------------
# No directory is created by any of these. The slashes are part of the name.
printf 'frame one\n'   > "$WORK_DIR/frame-0001.jpg"
printf 'frame two\n'   > "$WORK_DIR/frame-0002.jpg"
printf 'frame three\n' > "$WORK_DIR/frame-0001-delta.jpg"
printf '{"expedition":"field-journal"}\n' > "$WORK_DIR/manifest.json"

az storage blob upload \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0001.jpg" \
  --file "$WORK_DIR/frame-0001.jpg" \
  --overwrite \
  --output none

az storage blob upload \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0002.jpg" \
  --file "$WORK_DIR/frame-0002.jpg" \
  --overwrite \
  --output none

az storage blob upload \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-delta/2026/07/06/frame-0001.jpg" \
  --file "$WORK_DIR/frame-0001-delta.jpg" \
  --overwrite \
  --output none

az storage blob upload \
  --container-name "$CONTAINER_NAME" \
  --name "manifest.json" \
  --file "$WORK_DIR/manifest.json" \
  --overwrite \
  --output none

echo "uploaded 4 blobs and 0 directories"

# -----------------------------------------------------------------------------
step "3. Set metadata on one blob"
# -----------------------------------------------------------------------------
# Metadata rides along with the blob and comes back with its properties. It is
# never indexed, so it can describe a blob you already know how to find and
# nothing more.
az storage blob metadata update \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0001.jpg" \
  --metadata station=station-bravo capturedUtc=2026-07-06T04:12:55Z \
  --output none

az storage blob metadata show \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0001.jpg" \
  --output json

# -----------------------------------------------------------------------------
step "4. Set tags on the same blob"
# -----------------------------------------------------------------------------
# Tags are the indexed twin of metadata: a separate call to read, but the only
# one the service can search across an entire account.
az storage blob tag set \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0001.jpg" \
  --tags station=station-bravo retention=cold \
  --output none

az storage blob tag list \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0001.jpg" \
  --output json

# -----------------------------------------------------------------------------
step "5. List by prefix"
# -----------------------------------------------------------------------------
# A prefix scan is a string comparison. The trailing slash is what keeps
# 'station-bravo' from also matching 'station-bravo-2'.
az storage blob list \
  --container-name "$CONTAINER_NAME" \
  --prefix "observations/station-bravo/" \
  --query "[].name" \
  --output tsv

# -----------------------------------------------------------------------------
step "6. List the same blobs hierarchically"
# -----------------------------------------------------------------------------
# The delimiter tells the service where to stop and fold. Same data, same
# container, different view: nothing was moved, created, or renamed.
az storage blob list \
  --container-name "$CONTAINER_NAME" \
  --prefix "observations/" \
  --delimiter "/" \
  --query "[].name" \
  --output tsv

# -----------------------------------------------------------------------------
step "7. Page the listing explicitly"
# -----------------------------------------------------------------------------
# --num-results caps what one call returns. In an SDK this is the page size, and
# it is the unit both the request count and the bill are measured in.
az storage blob list \
  --container-name "$CONTAINER_NAME" \
  --num-results 2 \
  --query "[].name" \
  --output tsv

# -----------------------------------------------------------------------------
step "8. Download a blob and compare it byte for byte"
# -----------------------------------------------------------------------------
az storage blob download \
  --container-name "$CONTAINER_NAME" \
  --name "observations/station-bravo/2026/07/06/frame-0001.jpg" \
  --file "$WORK_DIR/roundtrip.jpg" \
  --output none

cmp "$WORK_DIR/frame-0001.jpg" "$WORK_DIR/roundtrip.jpg" \
  && echo "round trip identical: blob storage stores opaque bytes, unchanged"

# -----------------------------------------------------------------------------
step "9. Delete the container"
# -----------------------------------------------------------------------------
# One delete removes every blob under it. Against a real account this is the
# step that stops the bill, so it is never optional.
az storage container delete \
  --name "$CONTAINER_NAME" \
  --output table

echo
echo "Done. Nothing remains in the emulator."
