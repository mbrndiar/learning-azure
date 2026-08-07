#!/usr/bin/env bash
# =============================================================================
# module.secure-operable-cloud -- live checkpoint, Azure CLI
# =============================================================================
#
# Proves, against a real subscription, the five things no emulator and no
# offline evaluator can prove: that a role assignment takes time to take effect,
# that a control-plane role grants no data access, that disabling Shared Key
# changes what "the correct key" means, what your own 403 looks like, and what
# survives a delete.
#
#   bash infra/azure-cli/secure-operable-cloud.sh
#
# The PowerShell twin infra/powershell/secure-operable-cloud.ps1 performs the
# same steps in the same order with the same names, so the two can be read side
# by side.
#
# EVERY CHECK IN STEP 0 FAILS CLOSED. This is the only script in the course that
# grants and revokes access, and the only one that deletes a resource group, so
# it refuses to run rather than guess: no session, no run; a subscription
# selector that matches two subscriptions, no run; a region outside the
# allow-list, no run; a resource group that exists but was not made by this
# course, no delete.
#
# It never signs you in. 'az login' is your decision, made with your eyes open,
# in the tenant you meant.
#
# COST: a Standard_LRS storage account with a handful of small blobs, held for
# the length of the run. Storage is billed per GiB-month, so a few kilobytes for
# half an hour is far below a cent; transactions are billed per 10,000, and this
# script makes a few dozen. The optional Log Analytics workspace in step 7 has a
# free ingestion allowance and is off by default. Step 9 deletes everything, and
# step 10 checks that "deleted" meant it.
#
# If the script is interrupted, the teardown is one command -- and the name is
# printed at every step precisely so you can run it:
#
#   az group delete --name <RESOURCE_GROUP> --yes --no-wait
#
# PREREQUISITES: an authenticated Azure CLI session, and an identity that can
# create resources and manage role assignments in the target subscription
# (Owner, or Contributor plus User Access Administrator, or Role Based Access
# Control Administrator). Step 0 checks for exactly this and tells you which
# half is missing.
# =============================================================================

set -euo pipefail

LOCATION="${LOCATION:-westeurope}"
ALLOWED_LOCATIONS="${ALLOWED_LOCATIONS:-westeurope northeurope}"
RUN_ID="${RUN_ID:-$(printf '%06x' $((RANDOM * RANDOM % 16777216)))}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-expedition-secops-$RUN_ID}"
OWNER_TAG="${OWNER_TAG:-$(whoami)}"
EXPIRES_ON="${EXPIRES_ON:-$(date -u -d '+1 day' +%Y-%m-%d)}"
CONTAINER_NAME="reports"
ENABLE_DIAGNOSTIC_SETTINGS="${ENABLE_DIAGNOSTIC_SETTINGS:-0}"

# A storage account name is a DNS label in a global namespace: 3-24 characters,
# lower-case letters and digits only. The run id goes last and is never
# truncated -- it is the part that keeps two people in one subscription apart.
ACCOUNT_NAME="${ACCOUNT_NAME:-stexpsecops$RUN_ID}"
ACCOUNT_NAME="$(printf '%s' "$ACCOUNT_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9' | cut -c1-24)"

# The tags are not decoration. Step 9 refuses to delete a group that does not
# carry managed-by=learning-azure, which is what stops a mistyped
# RESOURCE_GROUP from becoming an incident.
MANAGED_BY="learning-azure"
TAGS=(
  "owner=$OWNER_TAG"
  "managed-by=$MANAGED_BY"
  "purpose=module-12-checkpoint"
  "expires-on=$EXPIRES_ON"
)

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
fail() { printf '\n\033[1;31mRefusing to continue: %s\033[0m\n' "$1" >&2; exit 1; }

# -----------------------------------------------------------------------------
step "0. Preflight -- every check here fails closed"
# -----------------------------------------------------------------------------
command -v az >/dev/null 2>&1 || fail "the Azure CLI is not on PATH."

# 'az account show' is the cheapest question that distinguishes "signed in"
# from "signed in somewhere else". It exits non-zero when there is no session,
# which is why the whole thing is guarded rather than piped.
if ! az account show --output none 2>/dev/null; then
  fail "no signed-in session. Run 'az login' yourself, then re-run this script."
fi

CURRENT_SUBSCRIPTION_ID="$(az account show --query id --output tsv)"

# SUBSCRIPTION may be an id or a display name. A display name is not unique --
# "Visual Studio Enterprise" is the name of a great many subscriptions, and two
# of them in one tenant is ordinary -- so a name that matches twice is an error,
# never "the first one".
SUBSCRIPTION="${SUBSCRIPTION:-$CURRENT_SUBSCRIPTION_ID}"
MATCHES="$(az account list --query "[?id=='$SUBSCRIPTION' || name=='$SUBSCRIPTION'].id" --output tsv)"
MATCH_COUNT="$(printf '%s' "$MATCHES" | grep -c . || true)"

case "$MATCH_COUNT" in
  0) fail "no subscription matches '$SUBSCRIPTION'. Run 'az account list --output table'." ;;
  1) SUBSCRIPTION_ID="$MATCHES" ;;
  *) fail "'$SUBSCRIPTION' matches $MATCH_COUNT subscriptions. Pass SUBSCRIPTION=<id> instead of a display name." ;;
esac

az account set --subscription "$SUBSCRIPTION_ID"

SUBSCRIPTION_NAME="$(az account show --query name --output tsv)"
TENANT_ID="$(az account show --query tenantId --output tsv)"
PRINCIPAL_ID="$(az ad signed-in-user show --query id --output tsv 2>/dev/null || true)"
if [[ -z "$PRINCIPAL_ID" ]]; then
  # A service principal has no signed-in *user*; read the object id the session
  # actually carries instead of assuming there is a human behind it.
  PRINCIPAL_ID="$(az account show --query user.name --output tsv)"
  PRINCIPAL_ID="$(az ad sp show --id "$PRINCIPAL_ID" --query id --output tsv 2>/dev/null || true)"
fi
[[ -n "$PRINCIPAL_ID" ]] || fail "cannot resolve the object id of the signed-in identity, so no role can be assigned to it."

# The region allow-list exists because a resource created in the wrong region is
# not wrong in a way anything will tell you about. It is simply somewhere else,
# on a different bill, behind a different latency.
read -r -a ALLOWED_LOCATION_LIST <<< "$ALLOWED_LOCATIONS"
if ! printf '%s\n' "${ALLOWED_LOCATION_LIST[@]}" | grep -qx "$LOCATION"; then
  fail "LOCATION='$LOCATION' is not in ALLOWED_LOCATIONS='$ALLOWED_LOCATIONS'."
fi

# Managing role assignments is a distinct permission from creating resources.
# Contributor can build the whole architecture and cannot grant anyone access
# to it, which is a confusing failure to hit in step 4 rather than here.
SUBSCRIPTION_SCOPE="/subscriptions/$SUBSCRIPTION_ID"
HELD_ROLES="$(az role assignment list --assignee "$PRINCIPAL_ID" --scope "$SUBSCRIPTION_SCOPE" --include-inherited --query "[].roleDefinitionName" --output tsv || true)"
if ! printf '%s\n' "$HELD_ROLES" | grep -qxE 'Owner|Contributor'; then
  fail "the signed-in identity holds none of Owner/Contributor at $SUBSCRIPTION_SCOPE (it holds: ${HELD_ROLES//$'\n'/, })."
fi
if ! printf '%s\n' "$HELD_ROLES" | grep -qxE 'Owner|User Access Administrator|Role Based Access Control Administrator'; then
  fail "the identity can create resources but cannot manage role assignments; step 4 would fail. Needed: Owner, User Access Administrator, or Role Based Access Control Administrator."
fi

cat <<SUMMARY

  subscription : $SUBSCRIPTION_NAME ($SUBSCRIPTION_ID)
  tenant       : $TENANT_ID
  principal    : $PRINCIPAL_ID
  region       : $LOCATION
  group        : $RESOURCE_GROUP
  account      : $ACCOUNT_NAME
  tags         : ${TAGS[*]}

SUMMARY

read -r -p "Create these resources in the subscription above? [y/N] " reply
[[ "$reply" == "y" || "$reply" == "Y" ]] || { echo "Aborted. Nothing was created."; exit 1; }

# -----------------------------------------------------------------------------
step "1. Create the resource group -- the teardown handle"
# -----------------------------------------------------------------------------
# One group per run, tagged at creation. Everything else in this script lives
# inside it, which is what makes step 9 a single atomic delete instead of an
# inventory exercise.
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags "${TAGS[@]}" \
  --output table

GROUP_SCOPE="$(az group show --name "$RESOURCE_GROUP" --query id --output tsv)"

# -----------------------------------------------------------------------------
step "2. Create a storage account that refuses its own keys"
# -----------------------------------------------------------------------------
# --allow-shared-key-access false is the switch this whole module turns on.
# With it set, the account's access keys stop working -- including the correct
# one -- and every request has to carry an Entra token. That is the difference
# between "we use managed identity" and "we cannot do anything else".
az storage account create \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-shared-key-access false \
  --allow-blob-public-access false \
  --min-tls-version TLS1_2 \
  --https-only true \
  --tags "${TAGS[@]}" \
  --output table

ACCOUNT_SCOPE="$(az storage account show --name "$ACCOUNT_NAME" --resource-group "$RESOURCE_GROUP" --query id --output tsv)"

echo
echo "-- the account's own view of what it will accept"
az storage account show \
  --name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "{sharedKey:allowSharedKeyAccess, publicBlobs:allowBlobPublicAccess, tls:minimumTlsVersion}" \
  --output json

echo
echo "-- the keys still exist and are now useless; this is the point"
az storage account keys list \
  --account-name "$ACCOUNT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "[].keyName" \
  --output tsv

# -----------------------------------------------------------------------------
step "3. Prove that Owner is not a data role"
# -----------------------------------------------------------------------------
# The identity that just created the account has Owner or Contributor over it.
# Neither carries a single data action, so this call is expected to fail, and
# the exact failure is the thing worth reading.
#
# --auth-mode login is what makes the CLI use your token instead of an account
# key. Without it the CLI would try to fetch a key, and the failure would be
# about Shared Key rather than about roles.
echo "-- expected to FAIL with AuthorizationPermissionMismatch"
set +e
DENIAL="$(az storage container create \
  --name "$CONTAINER_NAME" \
  --account-name "$ACCOUNT_NAME" \
  --auth-mode login \
  --output json 2>&1)"
DENIAL_STATUS=$?
set -e
printf '%s\n' "$DENIAL" | head -20

if [[ $DENIAL_STATUS -eq 0 ]]; then
  echo
  echo "NOTE: the call succeeded, which means this identity already holds a blob"
  echo "      data role at or above $ACCOUNT_SCOPE."
  echo "      Read that assignment with:"
  echo "        az role assignment list --assignee $PRINCIPAL_ID --scope $ACCOUNT_SCOPE --include-inherited --output table"
else
  echo
  echo "That is the whole lesson in one message. The identity may delete this"
  echo "account and rotate its keys, and may not list a container inside it."
fi

# -----------------------------------------------------------------------------
step "4. Grant the narrowest role that does the job"
# -----------------------------------------------------------------------------
# Storage Blob Data Contributor, at the account -- not at the resource group,
# and not at the subscription. The scope is the other half of the grant: the
# same role name is a different amount of access at every level.
az role assignment create \
  --assignee-object-id "$PRINCIPAL_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "$ACCOUNT_SCOPE" \
  --output table \
  || fail "could not create the role assignment; check that the identity holds User Access Administrator or Owner."

echo
echo "-- what the assignment looks like from the platform's side"
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --scope "$ACCOUNT_SCOPE" \
  --query "[].{role:roleDefinitionName, scope:scope, principalType:principalType}" \
  --output table

# -----------------------------------------------------------------------------
step "5. Wait for it, because a fresh grant is not a fast grant"
# -----------------------------------------------------------------------------
# Microsoft documents role assignment changes as taking up to 10 minutes to take
# effect. The first 403 after a grant therefore means nothing at all, and the
# correct response to it is to wait -- never to assign a broader role because
# "it worked when I gave it Contributor".
DEADLINE=$((SECONDS + 600))
ATTEMPT=0
READY=0
while (( SECONDS < DEADLINE )); do
  ATTEMPT=$((ATTEMPT + 1))
  if az storage container create \
       --name "$CONTAINER_NAME" \
       --account-name "$ACCOUNT_NAME" \
       --auth-mode login \
       --output none 2>/dev/null; then
    READY=1
    echo "authorized after ${SECONDS}s on attempt $ATTEMPT"
    break
  fi
  echo "attempt $ATTEMPT at ${SECONDS}s: still refused; waiting"
  sleep 20
done

if [[ $READY -ne 1 ]]; then
  echo
  echo "Still refused after 10 minutes. Do not widen the role. Check instead that:"
  echo "  * the assignment's scope is $ACCOUNT_SCOPE and not something narrower"
  echo "  * the principal id in the assignment is $PRINCIPAL_ID"
  echo "  * your token was issued after the assignment; 'az account get-access-token --output none' refreshes it"
  fail "the grant never took effect within its budget."
fi

echo
echo "-- write and read a blob with a token; no key is involved at any point"
printf 'checkpoint written by %s at %s\n' "$OWNER_TAG" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > ./checkpoint.txt
az storage blob upload \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --name checkpoint.txt \
  --file ./checkpoint.txt \
  --auth-mode login \
  --overwrite \
  --output table
rm -f ./checkpoint.txt

az storage blob list \
  --account-name "$ACCOUNT_NAME" \
  --container-name "$CONTAINER_NAME" \
  --auth-mode login \
  --query "[].{name:name, size:properties.contentLength}" \
  --output table

# -----------------------------------------------------------------------------
step "6. Revoke it, and watch the same call stop working"
# -----------------------------------------------------------------------------
# A grant you cannot take back is not access control. Revocation propagates on
# the same schedule as the grant, so the first success after this is not proof
# of anything either.
az role assignment delete \
  --assignee "$PRINCIPAL_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "$ACCOUNT_SCOPE" \
  --yes \
  --output none
echo "revoked Storage Blob Data Contributor at $ACCOUNT_SCOPE"

echo "-- polling until the refusal comes back (up to 10 minutes)"
DEADLINE=$((SECONDS + 600))
while (( SECONDS < DEADLINE )); do
  if ! az storage blob list \
         --account-name "$ACCOUNT_NAME" \
         --container-name "$CONTAINER_NAME" \
         --auth-mode login \
         --output none 2>/dev/null; then
    echo "refused again after ${SECONDS}s"
    break
  fi
  echo "still authorized at ${SECONDS}s: the revocation has not propagated yet"
  sleep 20
done

# -----------------------------------------------------------------------------
step "7. Diagnostics -- what the platform recorded about all of that"
# -----------------------------------------------------------------------------
# The control plane keeps 90 days of activity log for free. Every role
# assignment above is in it, with who made it and when, which is the audit trail
# a "who gave them access?" conversation actually runs on.
echo "-- role assignment writes in the last hour"
az monitor activity-log list \
  --resource-group "$RESOURCE_GROUP" \
  --offset 1h \
  --query "[?contains(authorization.action, 'roleAssignments')].{time:eventTimestamp, action:authorization.action, caller:caller, status:status.value}" \
  --output table \
  || echo "(the activity log lags by a few minutes; re-run this query in a moment)"

echo
echo "-- data-plane transactions, split by response type; the refusals are in here"
az monitor metrics list \
  --resource "$ACCOUNT_SCOPE/blobServices/default" \
  --metric Transactions \
  --interval PT1M \
  --filter "ResponseType eq '*'" \
  --output table \
  || echo "(metrics lag by a few minutes for a brand-new account)"

if [[ "$ENABLE_DIAGNOSTIC_SETTINGS" == "1" ]]; then
  # Off by default because a workspace outlives its resource group: deleting it
  # leaves a soft-deleted workspace behind for 14 days, which step 10 finds.
  WORKSPACE_NAME="log-expedition-$RUN_ID"
  az monitor log-analytics workspace create \
    --resource-group "$RESOURCE_GROUP" \
    --workspace-name "$WORKSPACE_NAME" \
    --location "$LOCATION" \
    --tags "${TAGS[@]}" \
    --output table

  WORKSPACE_ID="$(az monitor log-analytics workspace show --resource-group "$RESOURCE_GROUP" --workspace-name "$WORKSPACE_NAME" --query id --output tsv)"

  # StorageRead/StorageWrite are data-plane logs: they record the individual
  # blob calls, including the ones that were refused, which the activity log
  # never sees.
  az monitor diagnostic-settings create \
    --name blob-audit \
    --resource "$ACCOUNT_SCOPE/blobServices/default" \
    --workspace "$WORKSPACE_ID" \
    --logs '[{"category":"StorageRead","enabled":true},{"category":"StorageWrite","enabled":true}]' \
    --output table

  echo "Ingestion lags by up to 15 minutes. Then, in the workspace:"
  echo "  StorageBlobLogs | where StatusText contains 'Authorization' | project TimeGenerated, OperationName, StatusText, RequesterObjectId"
fi

# -----------------------------------------------------------------------------
step "8. What this run will cost, and what forgetting it would cost"
# -----------------------------------------------------------------------------
# Cost Management lags by hours and is unavailable on some offers, so nothing
# below is load-bearing -- the tags are what make the answer findable later.
echo "-- everything this run created, by tag"
az resource list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[].{name:name, type:type, managedBy:tags.\"managed-by\", expires:tags.\"expires-on\"}" \
  --output table

echo
echo "-- recorded usage, if this subscription's offer exposes it"
az consumption usage list \
  --start-date "$(date -u -d '-1 day' +%Y-%m-%d)" \
  --end-date "$(date -u +%Y-%m-%d)" \
  --query "[?contains(instanceName, '$RESOURCE_GROUP')].{resource:instanceName, cost:pretaxCost, currency:currency}" \
  --output table \
  2>/dev/null \
  || echo "(no consumption data: usage is aggregated with a delay of up to 24h, and the API is not available on every offer)"

cat <<'COST'

Standard_LRS storage bills for bytes held and for transactions made, so a
handful of small blobs for half an hour is far below a cent. The number that
matters is the other one: a resource left behind bills for existing, forever,
and nobody notices a single-digit monthly line item. That is what step 9 is for.
COST

# -----------------------------------------------------------------------------
step "9. Teardown -- and it checks before it deletes"
# -----------------------------------------------------------------------------
# The group is deleted only if the platform still says this run created it.
# Re-reading the tag rather than trusting the variable is the difference between
# deleting your own group and deleting one that happens to share its name.
ACTUAL_MANAGED_BY="$(az group show --name "$RESOURCE_GROUP" --query "tags.\"managed-by\"" --output tsv 2>/dev/null || true)"
ACTUAL_OWNER="$(az group show --name "$RESOURCE_GROUP" --query "tags.owner" --output tsv 2>/dev/null || true)"

[[ "$ACTUAL_MANAGED_BY" == "$MANAGED_BY" ]] \
  || fail "'$RESOURCE_GROUP' is tagged managed-by='$ACTUAL_MANAGED_BY', not '$MANAGED_BY'. Delete it by hand if it really is yours."
[[ "$ACTUAL_OWNER" == "$OWNER_TAG" ]] \
  || fail "'$RESOURCE_GROUP' belongs to '$ACTUAL_OWNER', not '$OWNER_TAG'."

az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --output none
echo "deleted resource group $RESOURCE_GROUP"

# -----------------------------------------------------------------------------
step "10. Verify the cleanup, because 'deleted' is a state and not an absence"
# -----------------------------------------------------------------------------
echo -n "resource group still listed : "
az group exists --name "$RESOURCE_GROUP"

echo
echo "-- storage accounts recoverable for 14 days (creating a new account with"
echo "   the same name silently forfeits that recovery)"
az rest \
  --method get \
  --url "https://management.azure.com/subscriptions/$SUBSCRIPTION_ID/providers/Microsoft.Storage/deletedAccounts?api-version=2023-05-01" \
  --query "value[].{name:name, deleted:properties.deletionTime, wasIn:properties.resourceId}" \
  --output table \
  || echo "(could not read the deleted-accounts list; it needs Microsoft.Storage/deletedAccounts/read)"

echo
echo "-- soft-deleted Log Analytics workspaces, which keep their data and their name for 14 days"
az monitor log-analytics workspace list-deleted-workspaces --output table \
  || echo "(none, or this CLI build does not expose the deleted-workspace list)"

echo
echo "-- role assignments whose principal no longer exists; they survive at a scope that does"
az role assignment list \
  --all \
  --query "[?principalName==null || principalName==''].{role:roleDefinitionName, scope:scope, id:id}" \
  --output table \
  || echo "(listing every assignment in the subscription needs Microsoft.Authorization/roleAssignments/read at subscription scope)"

cat <<VERIFY

Cleanup is complete only when all four of the above are empty for this run:

  az group exists --name $RESOURCE_GROUP          -> false
  deletedAccounts                                 -> no $ACCOUNT_NAME
  soft-deleted workspaces                         -> none from this run
  orphaned role assignments                       -> none pointing at $PRINCIPAL_ID

Anything left is either chargeable, recoverable by someone else, or a permission
nobody can attribute.
VERIFY
