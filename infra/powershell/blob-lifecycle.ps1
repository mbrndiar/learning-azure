#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.Storage

<#
.SYNOPSIS
    module.blob-lifecycle -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Proves the three retention mechanisms that Azurite cannot emulate: blob
    versioning, soft delete, and lifecycle management rules. Creates ONE storage
    account, exercises each mechanism, and deletes everything it made.

    This is the twin of infra/azure-cli/blob-lifecycle.sh: the same ten steps, in
    the same order, with the same names, so the two can be read side by side.

    COST: a general-purpose v2 account holding a few kilobytes for the minutes
    this script runs costs well under USD 0.01. The account is deleted at the
    end; step 9 is not optional. If the script is interrupted, delete the
    resource group by hand:

        Remove-AzResourceGroup -Name rg-expedition-lifecycle -Force -AsJob

    PREREQUISITES: PowerShell 7 with the Az module and an authenticated session.
    This script never calls Connect-AzAccount for you -- sign in yourself so you
    can see which identity and subscription you are about to spend money in.

.EXAMPLE
    pwsh -File infra/powershell/blob-lifecycle.ps1
#>

[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string] $ResourceGroupName = 'rg-expedition-lifecycle',
    [string] $AccountName = ('stlifecycle' + (Get-Random -Maximum 99999999)),
    [string] $ContainerName = 'artifacts',
    [string] $BlobName = 'observations/station-bravo/notes.txt'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

# The account name becomes a DNS label: 3-24 lowercase letters and digits.
$AccountName = ($AccountName.ToLowerInvariant() -replace '[^a-z0-9]', '')
if ($AccountName.Length -gt 24) { $AccountName = $AccountName.Substring(0, 24) }

$tags = @{
    expedition   = 'field-journal'
    environment  = 'checkpoint'
    'managed-by' = 'learning-azure'
}

$workFile = Join-Path ([System.IO.Path]::GetTempPath()) 'notes.txt'

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
    Write-Information ''
    Write-Information "== $Message"
}

# -----------------------------------------------------------------------------
Write-Step '0. Confirm the identity and subscription that will be billed'
# -----------------------------------------------------------------------------
Get-AzContext | Format-List -Property Name, Account, Subscription, Tenant | Out-String | Write-Information

$reply = Read-Host 'Create resources in the subscription above? [y/N]'
if ($reply -ne 'y' -and $reply -ne 'Y') { throw 'Aborted.' }

try {
    # -------------------------------------------------------------------------
    Write-Step '1. Create the resource group (the teardown handle)'
    # -------------------------------------------------------------------------
    New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Tag $tags |
        Format-Table -Property ResourceGroupName, Location, ProvisioningState |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '2. Create the storage account and grant this identity data access'
    # -------------------------------------------------------------------------
    # Same security baseline as module 3: no shared keys, no anonymous access,
    # TLS 1.2 minimum. Data-plane access therefore requires an Entra ID role.
    $account = New-AzStorageAccount `
        -ResourceGroupName $ResourceGroupName `
        -Name $AccountName `
        -Location $Location `
        -SkuName Standard_LRS `
        -Kind StorageV2 `
        -AccessTier Hot `
        -AllowSharedKeyAccess $false `
        -AllowBlobPublicAccess $false `
        -EnableHttpsTrafficOnly $true `
        -MinimumTlsVersion TLS1_2 `
        -Tag $tags

    $account | Format-Table -Property StorageAccountName, Location, @{ Name = 'Sku'; Expression = { $_.Sku.Name } } |
        Out-String |
        Write-Information

    $principalId = (Get-AzADUser -SignedIn).Id
    New-AzRoleAssignment `
        -ObjectId $principalId `
        -RoleDefinitionName 'Storage Blob Data Contributor' `
        -Scope $account.Id |
        Format-Table -Property DisplayName, RoleDefinitionName, Scope |
        Out-String |
        Write-Information

    Write-Information 'Role assignments can take up to five minutes to propagate.'
    Start-Sleep -Seconds 60

    # -------------------------------------------------------------------------
    Write-Step '3. Turn on the three retention mechanisms'
    # -------------------------------------------------------------------------
    # These are three INDEPENDENT promises, and each covers a different loss:
    #   -IsVersioningEnabled            an overwrite keeps the previous bytes
    #   -EnableDeleteRetentionPolicy    a deleted blob stays recoverable
    #   -EnableContainerDeleteRetentionPolicy  the same for a deleted container
    # Soft delete does not cover overwrites. Versioning does not cover deletes.
    Update-AzStorageBlobServiceProperty `
        -ResourceGroupName $ResourceGroupName `
        -StorageAccountName $AccountName `
        -IsVersioningEnabled $true |
        Out-Null

    Enable-AzStorageBlobDeleteRetentionPolicy `
        -ResourceGroupName $ResourceGroupName `
        -StorageAccountName $AccountName `
        -RetentionDays 7 |
        Out-Null

    Enable-AzStorageContainerDeleteRetentionPolicy `
        -ResourceGroupName $ResourceGroupName `
        -StorageAccountName $AccountName `
        -RetentionDays 7 |
        Out-Null

    Get-AzStorageBlobServiceProperty -ResourceGroupName $ResourceGroupName -StorageAccountName $AccountName |
        Format-List -Property IsVersioningEnabled, DeleteRetentionPolicy, ContainerDeleteRetentionPolicy |
        Out-String |
        Write-Information

    $ctx = New-AzStorageContext -StorageAccountName $AccountName -UseConnectedAccount
    New-AzStorageContainer -Name $ContainerName -Context $ctx |
        Format-Table -Property Name, PublicAccess |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '4. Watch an overwrite create a version instead of destroying data'
    # -------------------------------------------------------------------------
    Set-Content -Path $workFile -Value 'temp=-3C'
    $null = Set-AzStorageBlobContent -Container $ContainerName -File $workFile -Blob $BlobName -Context $ctx -Force

    Set-Content -Path $workFile -Value 'temp=-3C;ice=thin'
    $null = Set-AzStorageBlobContent -Container $ContainerName -File $workFile -Blob $BlobName -Context $ctx -Force

    # Every version, oldest first. This listing is the thing Azurite cannot produce.
    Get-AzStorageBlob -Container $ContainerName -Prefix $BlobName -IncludeVersion -Context $ctx |
        Format-Table -Property Name, VersionId, IsLatestVersion, Length |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '5. Write conditionally and watch the service refuse a stale write'
    # -------------------------------------------------------------------------
    # The Az cmdlets do not surface If-Match, so this step drops to the same
    # BlobClient the exercise uses. That is the point: the header is the API.
    $blob = Get-AzStorageBlob -Container $ContainerName -Blob $BlobName -Context $ctx
    $staleETag = $blob.BlobClient.GetProperties().Value.ETag

    Set-Content -Path $workFile -Value 'temp=-3C;ice=thin;wind=12kt'
    $conditions = [Azure.Storage.Blobs.Models.BlobRequestConditions]::new()
    $conditions.IfMatch = $staleETag
    $options = [Azure.Storage.Blobs.Models.BlobUploadOptions]::new()
    $options.Conditions = $conditions

    $stream = [System.IO.File]::OpenRead($workFile)
    try { $null = $blob.BlobClient.Upload($stream, $options) } finally { $stream.Dispose() }
    Write-Information 'conditional write with the current ETag: accepted'

    Set-Content -Path $workFile -Value 'temp=-3C;visibility=poor'
    $stream = [System.IO.File]::OpenRead($workFile)
    try {
        $null = $blob.BlobClient.Upload($stream, $options)
        Write-Information 'UNEXPECTED: the stale conditional write succeeded. Investigate before continuing.'
    }
    catch [Azure.RequestFailedException] {
        Write-Information "conditional write with the STALE ETag: refused ($($_.Exception.Status) $($_.Exception.ErrorCode)), as designed"
    }
    finally {
        $stream.Dispose()
    }

    Remove-Item -Path $workFile -Force -ErrorAction SilentlyContinue

    # -------------------------------------------------------------------------
    Write-Step '6. Delete the blob, then undelete it'
    # -------------------------------------------------------------------------
    # This is what soft delete buys, and it is the only step in this script that
    # cannot be rehearsed anywhere but a real account.
    Remove-AzStorageBlob -Container $ContainerName -Blob $BlobName -Context $ctx -Force

    Get-AzStorageBlob -Container $ContainerName -IncludeDeleted -Context $ctx |
        Format-Table -Property Name, IsDeleted, RemainingDaysBeforePermanentDelete |
        Out-String |
        Write-Information

    $deleted = Get-AzStorageBlob -Container $ContainerName -Blob $BlobName -IncludeDeleted -Context $ctx
    $deleted.BlobBaseClient.Undelete() | Out-Null

    Get-AzStorageBlob -Container $ContainerName -Blob $BlobName -Context $ctx |
        Format-Table -Property Name, IsDeleted, Length |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '7. Install a lifecycle management policy'
    # -------------------------------------------------------------------------
    # The rule is data, not code: evaluated by the service once a day. It never
    # runs while you watch, which is exactly why the plan has to be right on
    # paper before it is installed. The live counterpart of RetentionPlanner.
    $action = Add-AzStorageAccountManagementPolicyAction -BaseBlobAction TierToCool -DaysAfterModificationGreaterThan 30
    $action = Add-AzStorageAccountManagementPolicyAction -InputObject $action -BaseBlobAction TierToArchive -DaysAfterModificationGreaterThan 180
    $action = Add-AzStorageAccountManagementPolicyAction -InputObject $action -BaseBlobAction Delete -DaysAfterModificationGreaterThan 2555
    $action = Add-AzStorageAccountManagementPolicyAction -InputObject $action -SnapshotAction Delete -daysAfterCreationGreaterThan 90

    $filter = New-AzStorageAccountManagementPolicyFilter -PrefixMatch 'artifacts/observations/' -BlobType blockBlob
    $rule = New-AzStorageAccountManagementPolicyRule -Name 'expedition-artifact-cooling' -Action $action -Filter $filter

    Set-AzStorageAccountManagementPolicy `
        -ResourceGroupName $ResourceGroupName `
        -StorageAccountName $AccountName `
        -Rule $rule |
        Out-Null

    # -------------------------------------------------------------------------
    Write-Step '8. Read the policy back and check it against the plan'
    # -------------------------------------------------------------------------
    # Cool has a 30-day minimum and Archive a 180-day one. The transitions above
    # sit exactly on those boundaries, which is what RetentionPlanner.Evaluate
    # checks.
    (Get-AzStorageAccountManagementPolicy -ResourceGroupName $ResourceGroupName -StorageAccountName $AccountName).Rules |
        ForEach-Object {
            [pscustomobject]@{
                Rule    = $_.Name
                Cool    = $_.Definition.Actions.BaseBlob.TierToCool.DaysAfterModificationGreaterThan
                Archive = $_.Definition.Actions.BaseBlob.TierToArchive.DaysAfterModificationGreaterThan
                Delete  = $_.Definition.Actions.BaseBlob.Delete.DaysAfterModificationGreaterThan
            }
        } |
        Format-Table |
        Out-String |
        Write-Information
}
finally {
    # -------------------------------------------------------------------------
    Write-Step '9. Delete everything (NOT optional)'
    # -------------------------------------------------------------------------
    # One command, because step 1 put everything in one group. Note that
    # container soft delete does not keep a deleted resource group alive:
    # deleting the group deletes the account and everything the retention
    # settings were protecting.
    Remove-Item -Path $workFile -Force -ErrorAction SilentlyContinue
    Remove-AzResourceGroup -Name $ResourceGroupName -Force -AsJob | Out-Null

    Write-Information ''
    Write-Information 'Teardown requested. Confirm nothing survived:'
    Write-Information '  Get-AzResource -Tag @{ "managed-by" = "learning-azure" } | Format-Table'
}
