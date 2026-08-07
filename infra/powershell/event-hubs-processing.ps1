#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.EventHub, Az.Storage

<#
.SYNOPSIS
    module.event-hubs-processing -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Creates the two resources a real consumer needs -- an Event Hubs namespace
    and the storage account its checkpoints live in -- grants the split roles a
    processor actually requires, reads the runtime information that tells you
    how far behind a consumer is, and deletes it all.

    This is the twin of infra/azure-cli/event-hubs-processing.sh: the same nine
    steps, in the same order, with the same names, so the two can be read side
    by side.

    WHY THIS CHECKPOINT IS REQUIRED. Three things this module teaches cannot be
    observed locally at all:

      * Consumer groups are declared in infra/local/eventhubs/config.json and
        read once at container start. The emulator cannot add or remove one, so
        the cost of a new consumer group -- a second full read of the stream --
        is invisible until you create one here (step 4).
      * The roles a processor needs are split across two services: it reads from
        Event Hubs AND writes to Blob Storage. A processor with only the Event
        Hubs role starts, claims nothing, and reports errors from a component
        nobody was looking at (step 5).
      * Consumer lag is a platform metric, not an SDK call. The emulator emits
        no metrics whatsoever (step 6).

    COST: a Standard namespace at one throughput unit is roughly USD 0.03 per
    TU-hour; a Standard_LRS storage account holding a handful of checkpoint
    blobs is a fraction of a cent. Ten minutes of both is well under USD 0.02,
    and step 8 deletes them. If the script is interrupted, delete the group by
    hand:

        Remove-AzResourceGroup -Name rg-expedition-checkpoint -Force -AsJob

    PREREQUISITES: PowerShell 7 with the Az module and an authenticated session.
    This script never calls Connect-AzAccount for you -- sign in yourself so you
    can see which identity and subscription you are about to spend money in.

.EXAMPLE
    pwsh -File infra/powershell/event-hubs-processing.ps1
#>

[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string] $ResourceGroupName = 'rg-expedition-checkpoint',
    [string] $NamespaceName = ('ehexpedition' + (Get-Random -Maximum 99999999)),
    [string] $StorageAccountName = ('stexpedition' + (Get-Random -Maximum 99999999)),
    [string] $HubName = 'telemetry',
    [string] $ConsumerGroupName = 'field-journal',
    [string] $SecondConsumerGroupName = 'cold-archive',
    [string] $CheckpointContainerName = 'checkpoints'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A namespace name becomes a DNS label: 6-50 characters, letters, digits, and
# hyphens, starting with a letter. A storage account name is stricter still:
# 3-24 characters, lower-case letters and digits only.
$NamespaceName = ($NamespaceName.ToLowerInvariant() -replace '[^a-z0-9-]', '')
if ($NamespaceName.Length -gt 50) { $NamespaceName = $NamespaceName.Substring(0, 50) }

$StorageAccountName = ($StorageAccountName.ToLowerInvariant() -replace '[^a-z0-9]', '')
if ($StorageAccountName.Length -gt 24) { $StorageAccountName = $StorageAccountName.Substring(0, 24) }

$tags = @{
    expedition   = 'field-journal'
    environment  = 'checkpoint'
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
New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Tag $tags |
    Format-Table ResourceGroupName, Location, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '2. Create the namespace and the hub'
# -----------------------------------------------------------------------------
# Four partitions, matching infra/local/eventhubs/config.json, so the number of
# concurrent consumer instances that can do useful work is the same locally and
# here: four. A fifth instance would own nothing.
New-AzEventHubNamespace `
    -ResourceGroupName $ResourceGroupName `
    -Name $NamespaceName `
    -Location $Location `
    -SkuName 'Standard' `
    -SkuCapacity 1 `
    -MinimumTlsVersion '1.2' `
    -Tag $tags |
    Format-Table Name, Location, @{ Name = 'Sku'; Expression = { $_.SkuName } }, ProvisioningState

New-AzEventHub `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -Name $HubName `
    -PartitionCount 4 `
    -CleanupPolicy 'Delete' `
    -RetentionTimeInHour 24 |
    Format-Table Name, PartitionCount, Status

# -----------------------------------------------------------------------------
Write-Step '3. Create the checkpoint store'
# -----------------------------------------------------------------------------
# The checkpoint store is a separate service with a separate availability
# record, a separate bill, and separate permissions. A processor whose blob
# container is unreachable does not read events slowly -- it does not read them
# at all, because it cannot claim a partition without writing an ownership blob.
#
# Standard_LRS is right here: the blobs are tiny, they are rewritten
# constantly, and losing them costs a replay rather than data.
$storage = New-AzStorageAccount `
    -ResourceGroupName $ResourceGroupName `
    -Name $StorageAccountName `
    -Location $Location `
    -SkuName 'Standard_LRS' `
    -Kind 'StorageV2' `
    -MinimumTlsVersion 'TLS1_2' `
    -AllowBlobPublicAccess $false `
    -Tag $tags

$storage | Format-Table StorageAccountName, Location, @{ Name = 'Sku'; Expression = { $_.Sku.Name } }

New-AzStorageContainer -Name $CheckpointContainerName -Context $storage.Context |
    Format-Table Name, PublicAccess

# -----------------------------------------------------------------------------
Write-Step '4. Add a consumer group, and see what it costs'
# -----------------------------------------------------------------------------
# A consumer group is a cursor, not a copy. Adding one does not duplicate a
# single byte of storage -- and it does add a full second read of every event to
# the namespace's egress, which is charged against the same throughput units the
# producers are using. Standard allows 20 per hub; the reason to stop well short
# of that is egress, not the quota.
foreach ($group in @($ConsumerGroupName, $SecondConsumerGroupName)) {
    New-AzEventHubConsumerGroup `
        -ResourceGroupName $ResourceGroupName `
        -NamespaceName $NamespaceName `
        -EventHubName $HubName `
        -Name $group | Out-Null
    Write-Information "created consumer group: $group"
}

Get-AzEventHubConsumerGroup `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -EventHubName $HubName |
    Format-Table Name, CreatedAt

# -----------------------------------------------------------------------------
Write-Step '5. Grant the TWO roles a processor needs'
# -----------------------------------------------------------------------------
# This is the step that catches people. A processor is a client of two services,
# so it needs a role in each:
#
#   Azure Event Hubs Data Receiver   read events
#   Storage Blob Data Contributor    write ownership and checkpoint blobs
#
# With only the first, the processor starts cleanly, logs a storage failure
# through ProcessErrorAsync, claims no partitions, and reads nothing. It looks
# like an Event Hubs problem and is not one.
$principalId = (Get-AzADUser -SignedIn).Id
$namespaceScope = (Get-AzEventHubNamespace -ResourceGroupName $ResourceGroupName -Name $NamespaceName).Id
$containerScope = "$($storage.Id)/blobServices/default/containers/$CheckpointContainerName"

New-AzRoleAssignment `
    -ObjectId $principalId `
    -RoleDefinitionName 'Azure Event Hubs Data Receiver' `
    -Scope $namespaceScope | Out-Null
Write-Information 'assigned: Azure Event Hubs Data Receiver on the namespace'

New-AzRoleAssignment `
    -ObjectId $principalId `
    -RoleDefinitionName 'Storage Blob Data Contributor' `
    -Scope $containerScope | Out-Null
Write-Information 'assigned: Storage Blob Data Contributor on the checkpoint container only'

# -----------------------------------------------------------------------------
Write-Step '6. Read the runtime information a consumer is judged against'
# -----------------------------------------------------------------------------
# LastEnqueuedSequenceNumber is the top of the log. Subtract the sequence number
# your consumer group last checkpointed and you have its lag -- the same
# subtraction LagCalculator.Measure performs in the exercise.
#
# Nothing here reports the checkpoint: that number lives in YOUR storage
# account, in the blob metadata, and no Event Hubs API knows about it. Lag is a
# join between two services that only your code can perform.
foreach ($partition in @('0', '1', '2', '3')) {
    Get-AzEventHubPartition `
        -ResourceGroupName $ResourceGroupName `
        -NamespaceName $NamespaceName `
        -EventHubName $HubName `
        -PartitionId $partition |
        Format-List PartitionId, BeginSequenceNumber, LastEnqueuedSequenceNumber, LastEnqueuedTimeUtc, IsEmpty
}

Write-Information '-- the platform''s own view, which the emulator does not emit at all'
try {
    Get-AzMetric `
        -ResourceId $namespaceScope `
        -MetricName 'IncomingMessages', 'OutgoingMessages' `
        -TimeGrain ([TimeSpan]::FromMinutes(1)) `
        -ErrorAction Stop |
        Format-Table Name, Unit
}
catch {
    Write-Information "(no metrics yet: $($_.Exception.Message))"
}

# -----------------------------------------------------------------------------
Write-Step '7. Inspect the checkpoint container the way an operator would'
# -----------------------------------------------------------------------------
# After a processor has run against this namespace, the container holds one
# ownership blob and one checkpoint blob per partition per consumer group, under
# the path <namespace>/<hub>/<consumer-group>/. The position is in the METADATA;
# the blobs themselves are empty, which is why a naive 'list blobs, look at
# sizes' inspection concludes that nothing is there.
Get-AzStorageBlob -Container $CheckpointContainerName -Context $storage.Context |
    Format-Table Name, Length, @{ Name = 'Metadata'; Expression = { ($_.BlobProperties) } }

Write-Information ''
Write-Information 'To run the lesson companion against THIS namespace instead of the emulator:'
Write-Information ('  $env:EVENTHUBS_CONNECTION_STRING = (Get-AzEventHubKey -ResourceGroupName ' +
    "$ResourceGroupName -NamespaceName $NamespaceName -Name RootManageSharedAccessKey).PrimaryConnectionString")
Write-Information ('  $env:STORAGE_CONNECTION_STRING = ''DefaultEndpointsProtocol=https;AccountName=' +
    "$StorageAccountName;AccountKey=<key>;EndpointSuffix=core.windows.net'")
Write-Information "  `$env:EVENTHUBS_NAME = '$HubName'"

# -----------------------------------------------------------------------------
Write-Step '8. Delete everything'
# -----------------------------------------------------------------------------
# Both meters stop here: the namespace's hourly throughput-unit charge and the
# storage account's capacity and transaction charges. A checkpoint container is
# cheap to keep and expensive to forget, because a stale checkpoint pointing
# into an expired retention window is a consumer that will not start.
Remove-AzResourceGroup -Name $ResourceGroupName -Force | Out-Null

Write-Information ''
Write-Information "Deleted resource group $ResourceGroupName. Verify with:"
Write-Information "  Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue"
