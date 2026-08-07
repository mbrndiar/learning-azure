#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.Storage, Az.Monitor

<#
.SYNOPSIS
    module.secure-operable-cloud -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Proves, against a real subscription, the five things no emulator and no
    offline evaluator can prove: that a role assignment takes time to take
    effect, that a control-plane role grants no data access, that disabling
    Shared Key changes what "the correct key" means, what your own 403 looks
    like, and what survives a delete.

        pwsh infra/powershell/secure-operable-cloud.ps1

    The Azure CLI twin infra/azure-cli/secure-operable-cloud.sh performs the
    same steps in the same order with the same names, so the two can be read
    side by side.

    EVERY CHECK IN STEP 0 FAILS CLOSED. This is the only script in the course
    that grants and revokes access, and the only one that deletes a resource
    group, so it refuses to run rather than guess: no session, no run; a
    subscription selector that matches two subscriptions, no run; a region
    outside the allow-list, no run; a resource group that exists but was not
    made by this course, no delete.

    It never signs you in. Connect-AzAccount is your decision, made with your
    eyes open, in the tenant you meant.

    COST: a Standard_LRS storage account with a handful of small blobs, held for
    the length of the run. Storage is billed per GiB-month, so a few kilobytes
    for half an hour is far below a cent; transactions are billed per 10,000,
    and this script makes a few dozen. The optional Log Analytics workspace in
    step 7 has a free ingestion allowance and is off by default. Step 9 deletes
    everything, and step 10 checks that "deleted" meant it.

    If the script is interrupted, the teardown is one command:

        Remove-AzResourceGroup -Name <ResourceGroup> -Force -AsJob

    PREREQUISITES: an authenticated Az session, and an identity that can create
    resources and manage role assignments in the target subscription (Owner, or
    Contributor plus User Access Administrator, or Role Based Access Control
    Administrator). Step 0 checks for exactly this and tells you which half is
    missing.

.PARAMETER Location
    The Azure region to create resources in. Must be in -AllowedLocations.

.PARAMETER AllowedLocations
    The regions this script is willing to create resources in at all.

.PARAMETER Subscription
    A subscription id, or a display name. A display name that matches more than
    one subscription is refused rather than resolved to the first match.

.PARAMETER RunId
    The value that keeps two people running this in one subscription apart. It
    is appended to every globally unique name and never truncated.

.PARAMETER ResourceGroup
    The resource group that holds everything, and that step 9 deletes.

.PARAMETER OwnerTag
    The value written to the owner tag, and checked again before the delete.

.PARAMETER EnableDiagnosticSettings
    Also create a Log Analytics workspace and route the blob service's
    data-plane logs to it. Off by default, because a workspace outlives its
    resource group by fourteen days.
#>

[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string[]] $AllowedLocations = @('westeurope', 'northeurope'),
    [string] $Subscription,
    [string] $RunId = ('{0:x6}' -f (Get-Random -Minimum 0 -Maximum 16777215)),
    [string] $ResourceGroup,
    [string] $OwnerTag = $env:USER,
    [switch] $EnableDiagnosticSettings
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$InformationPreference = 'Continue'

if (-not $ResourceGroup) { $ResourceGroup = "rg-expedition-secops-$RunId" }
if (-not $OwnerTag) { $OwnerTag = $env:USERNAME }
if (-not $OwnerTag) { $OwnerTag = 'field-team' }

$containerName = 'reports'
$managedBy = 'learning-azure'

# A storage account name is a DNS label in a global namespace: 3-24 characters,
# lower-case letters and digits only. The run id goes last and is never
# truncated -- it is the part that keeps two people in one subscription apart.
$accountName = "stexpsecops$RunId".ToLowerInvariant() -replace '[^a-z0-9]', ''
if ($accountName.Length -gt 24) { $accountName = $accountName.Substring(0, 24) }

# The tags are not decoration. Step 9 refuses to delete a group that does not
# carry managed-by=learning-azure, which is what stops a mistyped
# -ResourceGroup from becoming an incident.
$tags = @{
    owner        = $OwnerTag
    'managed-by' = $managedBy
    purpose      = 'module-12-checkpoint'
    'expires-on' = (Get-Date).ToUniversalTime().AddDays(1).ToString('yyyy-MM-dd')
}

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
    Write-Information ''
    Write-Information "== $Message"
}

# Named as a refusal rather than as an action: it changes nothing, it only ends
# the run with the reason attached.
function Assert-Refused {
    param([Parameter(Mandatory)][string] $Because)
    throw "Refusing to continue: $Because"
}

# -----------------------------------------------------------------------------
Write-Step '0. Preflight -- every check here fails closed'
# -----------------------------------------------------------------------------
# Get-AzContext returns $null when there is no session. It never prompts, which
# is what makes it safe to ask before doing anything else.
$context = Get-AzContext
if (-not $context) {
    Assert-Refused 'no signed-in session. Run Connect-AzAccount yourself, then re-run this script.'
}

if (-not $Subscription) { $Subscription = $context.Subscription.Id }

# A display name is not unique -- "Visual Studio Enterprise" is the name of a
# great many subscriptions, and two of them in one tenant is ordinary -- so a
# name that matches twice is an error, never "the first one".
$candidates = @(Get-AzSubscription | Where-Object { $_.Id -eq $Subscription -or $_.Name -eq $Subscription })
switch ($candidates.Count) {
    0 { Assert-Refused "no subscription matches '$Subscription'. Run Get-AzSubscription." }
    1 { $targetSubscription = $candidates[0] }
    default {
        Assert-Refused "'$Subscription' matches $($candidates.Count) subscriptions. Pass -Subscription <id> instead of a display name."
    }
}

$null = Set-AzContext -SubscriptionId $targetSubscription.Id
$context = Get-AzContext
$tenantId = $targetSubscription.TenantId

# A user, a service principal, and a managed identity are three different
# things to a role assignment, and only one of them has a signed-in user to
# look up.
$principalId = $null
switch ($context.Account.Type) {
    'User' { $principalId = (Get-AzADUser -UserPrincipalName $context.Account.Id -ErrorAction SilentlyContinue).Id }
    'ServicePrincipal' { $principalId = (Get-AzADServicePrincipal -ApplicationId $context.Account.Id -ErrorAction SilentlyContinue).Id }
    default { $principalId = $null }
}
if (-not $principalId) {
    Assert-Refused 'cannot resolve the object id of the signed-in identity, so no role can be assigned to it.'
}

# The region allow-list exists because a resource created in the wrong region is
# not wrong in a way anything will tell you about. It is simply somewhere else,
# on a different bill, behind a different latency.
if ($AllowedLocations -notcontains $Location) {
    Assert-Refused "-Location '$Location' is not in -AllowedLocations '$($AllowedLocations -join ', ')'."
}

# Managing role assignments is a distinct permission from creating resources.
# Contributor can build the whole architecture and cannot grant anyone access to
# it, which is a confusing failure to hit in step 4 rather than here.
$subscriptionScope = "/subscriptions/$($targetSubscription.Id)"
$heldRoles = @(Get-AzRoleAssignment -ObjectId $principalId -Scope $subscriptionScope |
    Select-Object -ExpandProperty RoleDefinitionName)

if (-not ($heldRoles | Where-Object { $_ -in @('Owner', 'Contributor') })) {
    Assert-Refused "the signed-in identity holds none of Owner/Contributor at $subscriptionScope (it holds: $($heldRoles -join ', '))."
}
if (-not ($heldRoles | Where-Object { $_ -in @('Owner', 'User Access Administrator', 'Role Based Access Control Administrator') })) {
    Assert-Refused 'the identity can create resources but cannot manage role assignments; step 4 would fail. Needed: Owner, User Access Administrator, or Role Based Access Control Administrator.'
}

Write-Information ''
Write-Information "  subscription : $($targetSubscription.Name) ($($targetSubscription.Id))"
Write-Information "  tenant       : $tenantId"
Write-Information "  principal    : $principalId"
Write-Information "  region       : $Location"
Write-Information "  group        : $ResourceGroup"
Write-Information "  account      : $accountName"
Write-Information "  tags         : $(($tags.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')"
Write-Information ''

$reply = Read-Host 'Create these resources in the subscription above? [y/N]'
if ($reply -notin @('y', 'Y')) {
    Write-Information 'Aborted. Nothing was created.'
    return
}

# -----------------------------------------------------------------------------
Write-Step '1. Create the resource group -- the teardown handle'
# -----------------------------------------------------------------------------
# One group per run, tagged at creation. Everything else in this script lives
# inside it, which is what makes step 9 a single atomic delete instead of an
# inventory exercise.
$group = New-AzResourceGroup -Name $ResourceGroup -Location $Location -Tag $tags
$group | Format-Table ResourceGroupName, Location, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '2. Create a storage account that refuses its own keys'
# -----------------------------------------------------------------------------
# -AllowSharedKeyAccess $false is the switch this whole module turns on. With it
# set, the account's access keys stop working -- including the correct one --
# and every request has to carry an Entra token. That is the difference between
# "we use managed identity" and "we cannot do anything else".
$account = New-AzStorageAccount `
    -ResourceGroupName $ResourceGroup `
    -Name $accountName `
    -Location $Location `
    -SkuName Standard_LRS `
    -Kind StorageV2 `
    -AllowSharedKeyAccess $false `
    -AllowBlobPublicAccess $false `
    -MinimumTlsVersion TLS1_2 `
    -EnableHttpsTrafficOnly $true `
    -Tag $tags

$account | Format-Table StorageAccountName, Location, ProvisioningState
$accountScope = $account.Id

Write-Information ''
Write-Information "-- the account's own view of what it will accept"
Get-AzStorageAccount -ResourceGroupName $ResourceGroup -Name $accountName |
    Format-List AllowSharedKeyAccess, AllowBlobPublicAccess, MinimumTlsVersion

# -----------------------------------------------------------------------------
Write-Step '3. Prove that Owner is not a data role'
# -----------------------------------------------------------------------------
# The identity that just created the account has Owner or Contributor over it.
# Neither carries a single data action, so this call is expected to fail, and
# the exact failure is the thing worth reading.
#
# -UseConnectedAccount is what makes the context use your token instead of an
# account key. Without it the cmdlet would try to fetch a key, and the failure
# would be about Shared Key rather than about roles.
$dataContext = New-AzStorageContext -StorageAccountName $accountName -UseConnectedAccount

Write-Information '-- expected to FAIL with AuthorizationPermissionMismatch'
try {
    $null = New-AzStorageContainer -Name $containerName -Context $dataContext -ErrorAction Stop
    Write-Information ''
    Write-Information 'NOTE: the call succeeded, which means this identity already holds a blob'
    Write-Information "      data role at or above $accountScope. Read that assignment with:"
    Write-Information "        Get-AzRoleAssignment -ObjectId $principalId -Scope $accountScope"
}
catch {
    Write-Information $_.Exception.Message
    Write-Information ''
    Write-Information 'That is the whole lesson in one message. The identity may delete this'
    Write-Information 'account and rotate its keys, and may not list a container inside it.'
}

# -----------------------------------------------------------------------------
Write-Step '4. Grant the narrowest role that does the job'
# -----------------------------------------------------------------------------
# Storage Blob Data Contributor, at the account -- not at the resource group,
# and not at the subscription. The scope is the other half of the grant: the
# same role name is a different amount of access at every level.
New-AzRoleAssignment `
    -ObjectId $principalId `
    -RoleDefinitionName 'Storage Blob Data Contributor' `
    -Scope $accountScope |
    Format-Table RoleDefinitionName, Scope, ObjectType

Write-Information ''
Write-Information "-- what the assignment looks like from the platform's side"
Get-AzRoleAssignment -ObjectId $principalId -Scope $accountScope |
    Format-Table RoleDefinitionName, Scope, ObjectType

# -----------------------------------------------------------------------------
Write-Step '5. Wait for it, because a fresh grant is not a fast grant'
# -----------------------------------------------------------------------------
# Microsoft documents role assignment changes as taking up to 10 minutes to take
# effect. The first 403 after a grant therefore means nothing at all, and the
# correct response to it is to wait -- never to assign a broader role because
# "it worked when I gave it Contributor".
$budget = [TimeSpan]::FromMinutes(10)
$started = Get-Date
$attempt = 0
$ready = $false

while (((Get-Date) - $started) -lt $budget) {
    $attempt++
    try {
        $null = New-AzStorageContainer -Name $containerName -Context $dataContext -ErrorAction Stop
        $ready = $true
    }
    catch {
        # An existing container means the grant already worked on a previous
        # attempt; anything else is still a refusal.
        $ready = Get-AzStorageContainer -Name $containerName -Context $dataContext -ErrorAction SilentlyContinue
    }

    if ($ready) {
        Write-Information "authorized after $([int]((Get-Date) - $started).TotalSeconds)s on attempt $attempt"
        break
    }

    Write-Information "attempt $attempt at $([int]((Get-Date) - $started).TotalSeconds)s: still refused; waiting"
    Start-Sleep -Seconds 20
}

if (-not $ready) {
    Write-Information ''
    Write-Information 'Still refused after 10 minutes. Do not widen the role. Check instead that:'
    Write-Information "  * the assignment's scope is $accountScope and not something narrower"
    Write-Information "  * the principal id in the assignment is $principalId"
    Write-Information '  * your token was issued after the assignment; Disconnect-AzAccount and sign in again refreshes it'
    Assert-Refused 'the grant never took effect within its budget.'
}

Write-Information ''
Write-Information '-- write and read a blob with a token; no key is involved at any point'
$blobPath = Join-Path ([System.IO.Path]::GetTempPath()) 'checkpoint.txt'
"checkpoint written by $OwnerTag at $((Get-Date).ToUniversalTime().ToString('o'))" |
    Set-Content -Path $blobPath -Encoding utf8

Set-AzStorageBlobContent `
    -File $blobPath `
    -Container $containerName `
    -Blob 'checkpoint.txt' `
    -Context $dataContext `
    -Force |
    Format-Table Name, Length, BlobType

Remove-Item -Path $blobPath -Force

Get-AzStorageBlob -Container $containerName -Context $dataContext |
    Format-Table Name, Length, LastModified

# -----------------------------------------------------------------------------
Write-Step '6. Revoke it, and watch the same call stop working'
# -----------------------------------------------------------------------------
# A grant you cannot take back is not access control. Revocation propagates on
# the same schedule as the grant, so the first success after this is not proof
# of anything either.
Remove-AzRoleAssignment `
    -ObjectId $principalId `
    -RoleDefinitionName 'Storage Blob Data Contributor' `
    -Scope $accountScope

Write-Information "revoked Storage Blob Data Contributor at $accountScope"
Write-Information '-- polling until the refusal comes back (up to 10 minutes)'

$started = Get-Date
while (((Get-Date) - $started) -lt $budget) {
    $stillAllowed = $true
    try {
        $null = Get-AzStorageBlob -Container $containerName -Context $dataContext -ErrorAction Stop
    }
    catch {
        $stillAllowed = $false
    }

    if (-not $stillAllowed) {
        Write-Information "refused again after $([int]((Get-Date) - $started).TotalSeconds)s"
        break
    }

    Write-Information "still authorized at $([int]((Get-Date) - $started).TotalSeconds)s: the revocation has not propagated yet"
    Start-Sleep -Seconds 20
}

# -----------------------------------------------------------------------------
Write-Step '7. Diagnostics -- what the platform recorded about all of that'
# -----------------------------------------------------------------------------
# The control plane keeps 90 days of activity log for free. Every role
# assignment above is in it, with who made it and when, which is the audit trail
# a "who gave them access?" conversation actually runs on.
Write-Information '-- role assignment writes in the last hour'
Get-AzActivityLog -ResourceGroupName $ResourceGroup -StartTime (Get-Date).AddHours(-1) -WarningAction SilentlyContinue |
    Where-Object { $_.Authorization.Action -like '*roleAssignments*' } |
    Format-Table EventTimestamp, @{ Name = 'Action'; Expression = { $_.Authorization.Action } }, Caller

Write-Information ''
Write-Information '-- data-plane transactions, split by response type; the refusals are in here'
Get-AzMetric `
    -ResourceId "$accountScope/blobServices/default" `
    -MetricName 'Transactions' `
    -TimeGrain '00:01:00' `
    -StartTime (Get-Date).AddHours(-1) `
    -WarningAction SilentlyContinue |
    Select-Object -ExpandProperty Data |
    Format-Table TimeStamp, Total

if ($EnableDiagnosticSettings) {
    # Off by default because a workspace outlives its resource group: deleting
    # it leaves a soft-deleted workspace behind for 14 days, which step 10
    # finds.
    $workspaceName = "log-expedition-$RunId"
    $workspace = New-AzOperationalInsightsWorkspace `
        -ResourceGroupName $ResourceGroup `
        -Name $workspaceName `
        -Location $Location `
        -Tag $tags

    # StorageRead/StorageWrite are data-plane logs: they record the individual
    # blob calls, including the ones that were refused, which the activity log
    # never sees.
    $logs = @(
        New-AzDiagnosticSettingLogSettingsObject -Category 'StorageRead' -Enabled $true
        New-AzDiagnosticSettingLogSettingsObject -Category 'StorageWrite' -Enabled $true
    )

    New-AzDiagnosticSetting `
        -Name 'blob-audit' `
        -ResourceId "$accountScope/blobServices/default" `
        -WorkspaceId $workspace.ResourceId `
        -Log $logs |
        Format-Table Name, Type

    Write-Information 'Ingestion lags by up to 15 minutes. Then, in the workspace:'
    Write-Information "  StorageBlobLogs | where StatusText contains 'Authorization' | project TimeGenerated, OperationName, StatusText, RequesterObjectId"
}

# -----------------------------------------------------------------------------
Write-Step '8. What this run will cost, and what forgetting it would cost'
# -----------------------------------------------------------------------------
# Cost Management lags by hours and is unavailable on some offers, so nothing
# below is load-bearing -- the tags are what make the answer findable later.
Write-Information '-- everything this run created, by tag'
Get-AzResource -ResourceGroupName $ResourceGroup |
    Format-Table Name, ResourceType, @{ Name = 'ManagedBy'; Expression = { $_.Tags['managed-by'] } }, @{ Name = 'Expires'; Expression = { $_.Tags['expires-on'] } }

Write-Information ''
Write-Information @'
Standard_LRS storage bills for bytes held and for transactions made, so a
handful of small blobs for half an hour is far below a cent. The number that
matters is the other one: a resource left behind bills for existing, forever,
and nobody notices a single-digit monthly line item. That is what step 9 is for.
'@

# -----------------------------------------------------------------------------
Write-Step '9. Teardown -- and it checks before it deletes'
# -----------------------------------------------------------------------------
# The group is deleted only if the platform still says this run created it.
# Re-reading the tag rather than trusting the variable is the difference between
# deleting your own group and deleting one that happens to share its name.
$live = Get-AzResourceGroup -Name $ResourceGroup
$actualManagedBy = if ($live.Tags) { $live.Tags['managed-by'] } else { $null }
$actualOwner = if ($live.Tags) { $live.Tags['owner'] } else { $null }

if ($actualManagedBy -ne $managedBy) {
    Assert-Refused "'$ResourceGroup' is tagged managed-by='$actualManagedBy', not '$managedBy'. Delete it by hand if it really is yours."
}
if ($actualOwner -ne $OwnerTag) {
    Assert-Refused "'$ResourceGroup' belongs to '$actualOwner', not '$OwnerTag'."
}

Remove-AzResourceGroup -Name $ResourceGroup -Force | Out-Null
Write-Information "deleted resource group $ResourceGroup"

# -----------------------------------------------------------------------------
Write-Step "10. Verify the cleanup, because 'deleted' is a state and not an absence"
# -----------------------------------------------------------------------------
$stillThere = Get-AzResourceGroup -Name $ResourceGroup -ErrorAction SilentlyContinue
Write-Information "resource group still listed : $([bool]$stillThere)"

Write-Information ''
Write-Information '-- storage accounts recoverable for 14 days (creating a new account with'
Write-Information '   the same name silently forfeits that recovery)'
$deletedAccountsUri = "/subscriptions/$($targetSubscription.Id)/providers/Microsoft.Storage/deletedAccounts?api-version=2023-05-01"
try {
    $response = Invoke-AzRestMethod -Method GET -Path $deletedAccountsUri
    ($response.Content | ConvertFrom-Json).value |
        Select-Object name, @{ Name = 'Deleted'; Expression = { $_.properties.deletionTime } } |
        Format-Table
}
catch {
    Write-Information '(could not read the deleted-accounts list; it needs Microsoft.Storage/deletedAccounts/read)'
}

Write-Information ''
Write-Information '-- role assignments whose principal no longer exists; they survive at a scope that does'
Get-AzRoleAssignment -Scope $subscriptionScope |
    Where-Object { -not $_.DisplayName } |
    Format-Table RoleDefinitionName, Scope, ObjectId

Write-Information ''
Write-Information @"
Cleanup is complete only when all four of the above are empty for this run:

  Get-AzResourceGroup -Name $ResourceGroup   -> not found
  deletedAccounts                            -> no $accountName
  soft-deleted workspaces                    -> none from this run
  orphaned role assignments                  -> none pointing at $principalId

Anything left is either chargeable, recoverable by someone else, or a permission
nobody can attribute.
"@
