#!/usr/bin/env bash
# =============================================================================
# module.queue-storage — emulator lab, Azure CLI
# =============================================================================
#
# Drives the queue data plane end to end against Azurite: queue, send, peek,
# receive with a visibility timeout, observe redelivery, delete, teardown.
#
#   docker compose up -d azurite
#   bash infra/azure-cli/queue-storage.sh
#
# The PowerShell twin infra/powershell/queue-storage.ps1 performs the same steps
# in the same order with the same names, so the two can be read side by side.
#
# COST: none. Every command below talks to 127.0.0.1:10001, not to Azure. The
# well-known Azurite account name and key are emulator-only credentials; they
# grant access to nothing outside this machine, which is why they may appear in
# source. A real account key must never be written down like this.
#
# TO RUN THE SAME STEPS AGAINST AZURE instead of the emulator, export
# AZURE_STORAGE_AUTH_MODE=login and AZURE_STORAGE_ACCOUNT=<your account>, then
# unset AZURE_STORAGE_CONNECTION_STRING. Every 'az storage' call below then goes
# through your Entra ID identity and needs the Storage Queue Data Contributor
# role on the account. See infra/azure-cli/storage-account.sh, which creates an
# account configured exactly that way.
#
# PREREQUISITES: Azure CLI 2.60+ and a running Azurite container. No 'az login'
# is required for the emulator path.
# =============================================================================

set -euo pipefail

AZURITE_ACCOUNT="devstoreaccount1"
AZURITE_KEY="Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="

export AZURE_STORAGE_CONNECTION_STRING="${AZURITE_CONNECTION_STRING:-DefaultEndpointsProtocol=http;AccountName=$AZURITE_ACCOUNT;AccountKey=$AZURITE_KEY;QueueEndpoint=http://127.0.0.1:10001/$AZURITE_ACCOUNT;}"

QUEUE_NAME="expedition-dispatch"

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
encode() { printf '%s' "$1" | base64 | tr -d '\n'; }

# -----------------------------------------------------------------------------
step "0. Confirm the endpoint that is about to be written to"
# -----------------------------------------------------------------------------
# Printing the endpoint, not the key. If this does not say 127.0.0.1 you are
# about to write to a real account.
printf 'queue endpoint : %s\n' \
  "$(printf '%s' "$AZURE_STORAGE_CONNECTION_STRING" | tr ';' '\n' | grep '^QueueEndpoint=' || echo 'QueueEndpoint=(default, i.e. Azure)')"

# -----------------------------------------------------------------------------
step "1. Create the queue"
# -----------------------------------------------------------------------------
# A queue has no schema, no partitions, and no consumer groups. It is a single
# flat backlog of small messages, and that is the whole model.
az storage queue create \
  --name "$QUEUE_NAME" \
  --output table

# -----------------------------------------------------------------------------
step "2. Send three work orders"
# -----------------------------------------------------------------------------
# The body is Base64 because that is what the SDKs put on the wire by default,
# and because it is the encoded size that the 64 KiB limit applies to.
for n in 1001 1002 1003; do
  az storage queue message put \
    --queue-name "$QUEUE_NAME" \
    --content "$(encode "{\"workOrderId\":\"wo-$n\",\"operation\":\"ingest\"}")" \
    --query "{id:id, expirationTime:expirationTime}" \
    --output tsv
done

# -----------------------------------------------------------------------------
step "3. Look at the backlog without claiming it"
# -----------------------------------------------------------------------------
# Peek returns no pop receipt, so a peeked message cannot be deleted and its
# dequeue count does not advance. Peek is for dashboards, not for consumers.
az storage queue message peek \
  --queue-name "$QUEUE_NAME" \
  --num-messages 3 \
  --query "[].{id:id, dequeueCount:dequeueCount}" \
  --output table

az storage queue stats --output json 2>/dev/null || true

# -----------------------------------------------------------------------------
step "4. Receive one message with a short visibility timeout"
# -----------------------------------------------------------------------------
# Receiving hides the message; it does not remove it. The pop receipt is proof
# of THIS receive, and it is the only thing that can delete or extend it.
RECEIVED=$(az storage queue message get \
  --queue-name "$QUEUE_NAME" \
  --visibility-timeout 5 \
  --query "[0].{id:id, popReceipt:popReceipt, dequeueCount:dequeueCount}" \
  --output json)

printf '%s\n' "$RECEIVED"

MESSAGE_ID=$(printf '%s' "$RECEIVED" | sed -n 's/.*"id": *"\([^"]*\)".*/\1/p')
POP_RECEIPT=$(printf '%s' "$RECEIVED" | sed -n 's/.*"popReceipt": *"\([^"]*\)".*/\1/p')

# -----------------------------------------------------------------------------
step "5. Wait out the visibility timeout and receive it again"
# -----------------------------------------------------------------------------
# Nothing failed and nothing was retried. The message came back purely because
# the handler — here, 'sleep' — outlived the window it asked for. This is what
# at-least-once delivery is, and no setting turns it off.
sleep 7

az storage queue message get \
  --queue-name "$QUEUE_NAME" \
  --visibility-timeout 30 \
  --query "[0].{id:id, dequeueCount:dequeueCount}" \
  --output table

echo "the dequeueCount above is 2: same message, second consumer"

# -----------------------------------------------------------------------------
step "6. Prove the first pop receipt is now worthless"
# -----------------------------------------------------------------------------
# The redelivery invalidated it. A consumer holding a stale receipt cannot
# delete the message someone else is now working on, which is exactly the
# protection you want.
if az storage queue message delete \
  --queue-name "$QUEUE_NAME" \
  --id "$MESSAGE_ID" \
  --pop-receipt "$POP_RECEIPT" \
  --output none 2>/dev/null; then
  echo "unexpected: the stale pop receipt was accepted"
else
  echo "rejected as expected: the stale pop receipt no longer identifies this receive"
fi

# -----------------------------------------------------------------------------
step "7. Receive and delete properly"
# -----------------------------------------------------------------------------
# Delete is a separate call on purpose: the message survives a consumer that
# crashes between receiving and finishing.
FRESH=$(az storage queue message get \
  --queue-name "$QUEUE_NAME" \
  --visibility-timeout 30 \
  --query "[0].{id:id, popReceipt:popReceipt}" \
  --output json)

FRESH_ID=$(printf '%s' "$FRESH" | sed -n 's/.*"id": *"\([^"]*\)".*/\1/p')
FRESH_RECEIPT=$(printf '%s' "$FRESH" | sed -n 's/.*"popReceipt": *"\([^"]*\)".*/\1/p')

az storage queue message delete \
  --queue-name "$QUEUE_NAME" \
  --id "$FRESH_ID" \
  --pop-receipt "$FRESH_RECEIPT" \
  --output none

echo "deleted $FRESH_ID with the receipt from the receive that produced it"

# -----------------------------------------------------------------------------
step "8. Clear the backlog"
# -----------------------------------------------------------------------------
# Clearing is not the same as deleting the queue: the queue survives, empty.
az storage queue message clear \
  --queue-name "$QUEUE_NAME" \
  --output none

az storage queue message peek \
  --queue-name "$QUEUE_NAME" \
  --num-messages 5 \
  --output tsv

echo "backlog is empty"

# -----------------------------------------------------------------------------
step "9. Delete the queue"
# -----------------------------------------------------------------------------
# Against a real account this is the step that stops the bill, so it is never
# optional.
az storage queue delete \
  --name "$QUEUE_NAME" \
  --output table

echo
echo "Done. Nothing remains in the emulator."
