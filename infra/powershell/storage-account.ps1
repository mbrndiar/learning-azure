#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.Storage

<#
.SYNOPSIS
    module.storage-account -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Creates ONE storage account, inspects it, reconfigures it, proves the Entra ID
    auth boundary, and deletes everything it made.

    This is the twin of infra/azure-cli/storage-account.sh: the same nine steps,
    in the same order, with the same names, so the two can be read side by side.

    COST: a general-purpose v2 account with a few kilobytes of data and a handful
    of transactions costs well under USD 0.01 for the minutes this script runs.
    The account is deleted at the end; step 9 is not optional. If the script is
    interrupted, delete the resource group by hand:

        Remove-AzResourceGroup -Name rg-expedition-checkpoint -Force -AsJob

    PREREQUISITES: PowerShell 7 with the Az module and an authenticated session.
    This script never calls Connect-AzAccount for you -- sign in yourself so you
    can see which identity and subscription you are about to spend money in.

.EXAMPLE
    pwsh -File infra/powershell/storage-account.ps1
#>

[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string] $ResourceGroupName = 'rg-expedition-checkpoint',
    [string] $AccountName = ('stexpedition' + (Get-Random -Maximum 99999999)),
    [string] $ContainerName = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The account name becomes a DNS label: 3-24 lowercase letters and digits.
$AccountName = ($AccountName.ToLowerInvariant() -replace '[^a-z0-9]', '')
if ($AccountName.Length -gt 24) { $AccountName = $AccountName.Substring(0, 24) }

$tags = @{
    expedition  = 'field-journal'
    environment = 'checkpoint'
    'managed-by' = 'learning-azure'
}

$InformationPreference = 'Continue'

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
    Write-Information ''
    Write-Information "== $Message"
}

# -----------------------------------------------------------------------------
Write-Step '0. Confirm the identity and subscription that will be billed'
# -----------------------------------------------------------------------------
$context = Get-AzContext
$context | Format-List Name, Account, Subscription, Tenant

$reply = Read-Host 'Create resources in the subscription above? [y/N]'
if ($reply -ne 'y' -and $reply -ne 'Y') { throw 'Aborted.' }

# -----------------------------------------------------------------------------
Write-Step '1. Create the resource group (the teardown handle)'
# -----------------------------------------------------------------------------
# Everything this script creates lands here, so step 9 removes all of it with one
# command. A resource created outside this group survives the teardown and keeps
# billing.
New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Tag $tags |
    Format-Table ResourceGroupName, Location, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '2. Create the storage account on the security baseline'
# -----------------------------------------------------------------------------
# Every parameter below is a decision, not a default:
#   -SkuName Standard_ZRS            three copies across three availability zones
#   -AllowSharedKeyAccess $false     data-plane access requires Entra ID
#   -AllowBlobPublicAccess $false    no container can be made anonymous
#   -EnableHttpsTrafficOnly $true    plain HTTP is refused
#   -MinimumTlsVersion TLS1_2        no downgrade to TLS 1.0/1.1
#   -RequireInfrastructureEncryption a second encryption layer at rest
New-AzStorageAccount `
    -ResourceGroupName $ResourceGroupName `
    -Name $AccountName `
    -Location $Location `
    -Kind StorageV2 `
    -SkuName Standard_ZRS `
    -AccessTier Hot `
    -AllowSharedKeyAccess $false `
    -AllowBlobPublicAccess $false `
    -EnableHttpsTrafficOnly $true `
    -MinimumTlsVersion TLS1_2 `
    -RequireInfrastructureEncryption `
    -Tag $tags |
    Format-Table StorageAccountName, Location, SkuName, AccessTier

# -----------------------------------------------------------------------------
Write-Step '3. Inspect the account: endpoints, redundancy, and the auth boundary'
# -----------------------------------------------------------------------------
# The account name is the leftmost DNS label of every service endpoint. This is
# the live counterpart of StorageEndpoints.For in the exercise.
$account = Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $AccountName
[pscustomobject]@{
    Blob       = $account.PrimaryEndpoints.Blob
    Queue      = $account.PrimaryEndpoints.Queue
    Table      = $account.PrimaryEndpoints.Table
    Sku        = $account.Sku.Name
    Tier       = $account.AccessTier
    SharedKey  = $account.AllowSharedKeyAccess
    PublicBlob = $account.AllowBlobPublicAccess
    Tls        = $account.MinimumTlsVersion
} | Format-List

# -----------------------------------------------------------------------------
Write-Step '4. Grant this identity a data-plane role'
# -----------------------------------------------------------------------------
# Control-plane rights (Owner, Contributor) do NOT grant data-plane access when
# shared-key access is disabled. Without this assignment, step 6 fails with 403 --
# which is the single most useful thing this checkpoint demonstrates.
$principalId = (Get-AzADUser -SignedIn).Id

New-AzRoleAssignment `
    -ObjectId $principalId `
    -RoleDefinitionName 'Storage Blob Data Contributor' `
    -Scope $account.Id |
    Format-Table DisplayName, RoleDefinitionName, Scope

Write-Information 'Role assignments can take up to five minutes to propagate.'
Start-Sleep -Seconds 60

# -----------------------------------------------------------------------------
Write-Step '5. Create a container using Entra ID, not a key'
# -----------------------------------------------------------------------------
# -UseConnectedAccount is what makes the Az module use your Entra identity. Omit
# it and the module reaches for an account key that no longer exists.
$storageContext = New-AzStorageContext -StorageAccountName $AccountName -UseConnectedAccount

New-AzStorageContainer -Name $ContainerName -Context $storageContext |
    Format-Table Name, PublicAccess

# -----------------------------------------------------------------------------
Write-Step '6. Write and read one artifact over the data plane'
# -----------------------------------------------------------------------------
'station-bravo observed ice shelf calving' | Set-Content -Path './observation.txt' -Encoding utf8

Set-AzStorageBlobContent `
    -Container $ContainerName `
    -File './observation.txt' `
    -Blob 'observations/station-bravo.txt' `
    -Context $storageContext `
    -Force |
    Format-Table Name, Length, BlobType

Get-AzStorageBlob -Container $ContainerName -Context $storageContext |
    Format-Table Name, @{ Name = 'Tier'; Expression = { $_.BlobProperties.AccessTier } }, Length

Remove-Item -Path './observation.txt' -Force

# -----------------------------------------------------------------------------
Write-Step '7. Reconfigure: move the account default tier to Cool'
# -----------------------------------------------------------------------------
# Azurite has no tiers at all, so this behavior cannot be observed locally. Note
# that the change applies to blobs with no explicit tier -- existing blobs that
# inherited Hot move with it.
Set-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $AccountName -AccessTier Cool |
    Format-Table StorageAccountName, AccessTier

(Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $AccountName) |
    Select-Object AccessTier, @{ Name = 'Sku'; Expression = { $_.Sku.Name } } |
    Format-List

# -----------------------------------------------------------------------------
Write-Step '8. Observe what the emulator cannot show you'
# -----------------------------------------------------------------------------
# Redundancy, the geo-replication state, and the last sync time have no Azurite
# equivalent. For a ZRS account there is no secondary; switch -SkuName to
# Standard_GRS in step 2 to see GeoReplicationStats populated instead.
Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $AccountName -IncludeGeoReplicationStats |
    Select-Object @{ Name = 'Sku'; Expression = { $_.Sku.Name } }, GeoReplicationStats |
    Format-List

# -----------------------------------------------------------------------------
Write-Step '9. Delete everything (NOT optional)'
# -----------------------------------------------------------------------------
# One command, because step 1 put everything in one group. Drop -AsJob if you
# want to watch the deletion finish.
Remove-AzResourceGroup -Name $ResourceGroupName -Force | Out-Null

Write-Information ''
Write-Information 'Teardown requested. Confirm nothing survived:'
Write-Information "  Get-AzResource -Tag @{ 'managed-by' = 'learning-azure' } | Format-Table"
