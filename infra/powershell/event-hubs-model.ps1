#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.EventHub

<#
.SYNOPSIS
    module.event-hubs-model -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Creates ONE Event Hubs namespace and one hub, inspects the partitions,
    reconfigures everything that is configurable, proves that the partition
    count is not, and deletes it all.

    This is the twin of infra/azure-cli/event-hubs-model.sh: the same nine
    steps, in the same order, with the same names, so the two can be read side
    by side.

    WHY THIS CHECKPOINT IS REQUIRED. The Event Hubs emulator has no control
    plane at all: it reads infra/local/eventhubs/config.json at container start
    and exposes no way to create, resize, or reconfigure anything. Every
    capacity decision this module teaches -- throughput units, retention,
    consumer groups, and above all the immutability of the partition count -- is
    therefore invisible locally. Step 6 is the only place in this course where
    you watch Azure refuse a change.

    COST: a Standard-tier namespace is billed per throughput unit per hour, at
    roughly USD 0.03 per TU-hour, plus USD 0.028 per million ingress events. One
    TU for the ten minutes this script runs is well under USD 0.01, and the
    namespace is deleted at the end. Step 8 is not optional. If the script is
    interrupted, delete the resource group by hand:

        Remove-AzResourceGroup -Name rg-expedition-checkpoint -Force -AsJob

    PREREQUISITES: PowerShell 7 with the Az module and an authenticated session.
    This script never calls Connect-AzAccount for you -- sign in yourself so you
    can see which identity and subscription you are about to spend money in.

.EXAMPLE
    pwsh -File infra/powershell/event-hubs-model.ps1
#>

[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string] $ResourceGroupName = 'rg-expedition-checkpoint',
    [string] $NamespaceName = ('ehexpedition' + (Get-Random -Maximum 99999999)),
    [string] $HubName = 'telemetry',
    [string] $ConsumerGroupName = 'field-journal'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A namespace name becomes a DNS label: 6-50 characters, letters, digits, and
# hyphens, starting with a letter.
$NamespaceName = ($NamespaceName.ToLowerInvariant() -replace '[^a-z0-9-]', '')
if ($NamespaceName.Length -gt 50) { $NamespaceName = $NamespaceName.Substring(0, 50) }

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
# Everything this script creates lands here, so step 8 removes all of it with
# one command. A resource created outside this group survives the teardown and
# keeps billing -- and an Event Hubs namespace bills by the hour whether or not
# a single event is ever published to it.
New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Tag $tags |
    Format-Table ResourceGroupName, Location, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '2. Create the namespace: the unit of capacity and of billing'
# -----------------------------------------------------------------------------
# The namespace, not the hub, owns throughput. Every hub inside it shares these
# throughput units, so a noisy hub starves a quiet one.
#
#   -SkuName Standard             20 consumer groups per hub, up to 7 days
#   -SkuCapacity 1                one throughput unit: 1 MB/s or 1,000 events/s
#   -EnableAutoInflate            raise capacity instead of throttling
#   -MaximumThroughputUnits 3     the ceiling auto-inflate may not exceed
#
# Auto-inflate only ever scales UP. It is a throttling guard, not a cost
# control: the maximum is the number you are agreeing to pay for.
New-AzEventHubNamespace `
    -ResourceGroupName $ResourceGroupName `
    -Name $NamespaceName `
    -Location $Location `
    -SkuName 'Standard' `
    -SkuCapacity 1 `
    -EnableAutoInflate `
    -MaximumThroughputUnit 3 `
    -MinimumTlsVersion '1.2' `
    -Tag $tags |
    Format-Table Name, Location, @{ Name = 'Sku'; Expression = { $_.SkuName } }, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '3. Create the hub: the partition count is decided here, once'
# -----------------------------------------------------------------------------
# -PartitionCount is the only parameter on this command that cannot be changed
# afterwards on Basic or Standard. Four partitions matches
# infra/local/eventhubs/config.json so the emulator and the live hub agree.
#
# -RetentionTimeInHour 24 is the shortest window Standard allows. Retention
# decides how far a replay can reach and how much storage the namespace's
# throughput units have to cover (84 GB per TU).
New-AzEventHub `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -Name $HubName `
    -PartitionCount 4 `
    -CleanupPolicy 'Delete' `
    -RetentionTimeInHour 24 |
    Format-Table Name, PartitionCount, Status

# -----------------------------------------------------------------------------
Write-Step '4. Inspect the hub: the partitions the SDK will report'
# -----------------------------------------------------------------------------
# These are the same partition ids that GetEventHubPropertiesAsync returns in
# lessons/08-event-hubs-model/TelemetryStream, and the same count the exercise's
# PartitionKeyPlanner.Spread is asked to place keys over.
$hub = Get-AzEventHub -ResourceGroupName $ResourceGroupName -NamespaceName $NamespaceName -Name $HubName
[pscustomobject]@{
    Partitions     = $hub.PartitionCount
    PartitionIds   = ($hub.PartitionId -join ', ')
    RetentionHours = $hub.RetentionDescriptionRetentionTimeInHour
    CleanupPolicy  = $hub.RetentionDescriptionCleanupPolicy
    Status         = $hub.Status
} | Format-List

# -----------------------------------------------------------------------------
Write-Step '5. Reconfigure everything that IS configurable'
# -----------------------------------------------------------------------------
# Retention, throughput units, and consumer groups are all live dials. None of
# them requires a restart, a migration, or a maintenance window.

Write-Information '-- retention 1 day -> 3 days'
Set-AzEventHub `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -Name $HubName `
    -RetentionTimeInHour 72 |
    Format-Table Name, @{ Name = 'RetentionHours'; Expression = { $_.RetentionDescriptionRetentionTimeInHour } }

Write-Information '-- throughput units 1 -> 2'
Set-AzEventHubNamespace `
    -ResourceGroupName $ResourceGroupName `
    -Name $NamespaceName `
    -SkuCapacity 2 |
    Format-Table Name, @{ Name = 'Sku'; Expression = { $_.SkuName } }, @{ Name = 'Capacity'; Expression = { $_.SkuCapacity } }

Write-Information '-- add a consumer group (each one reads the WHOLE stream, so egress doubles)'
New-AzEventHubConsumerGroup `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -EventHubName $HubName `
    -Name $ConsumerGroupName |
    Format-Table Name

Get-AzEventHubConsumerGroup `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -EventHubName $HubName |
    Select-Object -ExpandProperty Name

# -----------------------------------------------------------------------------
Write-Step '6. Try to change the one thing that cannot be changed'
# -----------------------------------------------------------------------------
# On Basic and Standard the partition count is fixed at creation. There is no
# parameter, no support request, and no scale operation: the only route to a
# different partition count is a NEW hub and a migration that re-reads the
# stream.
#
# The command below is expected to FAIL, or -- worse and more instructive -- to
# report success while leaving PartitionCount unchanged. Step 6b reads the value
# back rather than trusting the response, which is the habit this step exists to
# build.
try {
    Set-AzEventHub `
        -ResourceGroupName $ResourceGroupName `
        -NamespaceName $NamespaceName `
        -Name $HubName `
        -PartitionCount 8 `
        -ErrorAction Stop | Out-Null
    Write-Information 'the update call returned success -- now check whether anything happened'
}
catch {
    Write-Information "the update call was rejected outright: $($_.Exception.Message)"
}

Write-Information '-- 6b. read the partition count back'
$actual = (Get-AzEventHub -ResourceGroupName $ResourceGroupName -NamespaceName $NamespaceName -Name $HubName).PartitionCount
Write-Information "partition count is still: $actual"

if ($actual -eq 4) {
    Write-Information 'confirmed: the number chosen in step 3 is the number you keep'
}
else {
    Write-Information "unexpected: this subscription's tier allowed the change"
}

# -----------------------------------------------------------------------------
Write-Step '7. Grant this identity data-plane roles'
# -----------------------------------------------------------------------------
# Control-plane rights (Owner, Contributor) let you create the hub above and do
# NOT let you publish a single event to it. Sending and receiving are two
# separate roles, which is what makes least privilege expressible here.
$principalId = (Get-AzADUser -SignedIn).Id
$scope = (Get-AzEventHubNamespace -ResourceGroupName $ResourceGroupName -Name $NamespaceName).Id

foreach ($role in @('Azure Event Hubs Data Sender', 'Azure Event Hubs Data Receiver')) {
    New-AzRoleAssignment -ObjectId $principalId -RoleDefinitionName $role -Scope $scope | Out-Null
    Write-Information "assigned: $role"
}

Write-Information ''
Write-Information 'To publish to this hub from the lesson companion, set:'
Write-Information ('  $env:EVENTHUBS_CONNECTION_STRING = (Get-AzEventHubKey -ResourceGroupName ' +
    "$ResourceGroupName -NamespaceName $NamespaceName -Name RootManageSharedAccessKey).PrimaryConnectionString")
Write-Information "  `$env:EVENTHUBS_NAME = '$HubName'"
Write-Information '(A connection string is the emulator''s authentication model, not Azure''s.'
Write-Information ' Role assignments above are what a real workload uses; the exercise''s'
Write-Information ' DefaultAzureCredential path is module 2.)'

# -----------------------------------------------------------------------------
Write-Step '8. Delete everything'
# -----------------------------------------------------------------------------
# A namespace bills per throughput unit per hour whether or not it carries
# traffic, so this step is the one that stops the meter. Deleting the resource
# group removes the namespace, the hub, the consumer group, the events still
# inside their retention window, and the role assignments scoped to them.
Remove-AzResourceGroup -Name $ResourceGroupName -Force | Out-Null

Write-Information ''
Write-Information "Deleted resource group $ResourceGroupName. Verify with:"
Write-Information "  Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue"
