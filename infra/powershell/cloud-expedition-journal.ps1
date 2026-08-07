#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.EventHub, Az.Storage, Az.CosmosDB, Az.Monitor

<#
.SYNOPSIS
    capstone.cloud-expedition-journal -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Provisions the whole expedition estate -- storage account, Event Hubs
    namespace, and Cosmos account -- grants only data-plane roles to the
    signed-in identity, runs the capstone against it, shows the diagnostics and
    cost views that do not exist locally, and deletes everything.

    This is the twin of infra/azure-cli/cloud-expedition-journal.sh: the same
    nine steps, in the same order, with the same names, so the two can be read
    side by side.

    WHY THIS CHECKPOINT IS REQUIRED. Four things the capstone claims cannot be
    observed against emulators at all:

      * Shared-key and SAS authentication can be switched OFF at the resource
        (steps 2 and 3). No emulator can refuse a key, so "identity is the only
        path" is an assertion locally and a fact here.
      * Cosmos data-plane access is a SEPARATE RBAC system from the control
        plane (step 5). A subscription Owner still cannot read a document. That
        is surprising, expensive to discover in an incident, and invisible
        locally.
      * Request charges and throttling are real numbers on a real throughput
        budget (step 7), not a fake's arithmetic.
      * Deleting the resource group is the only teardown that is actually
        complete (step 8), and it is the only one that stops the meters.

    COST: a Standard Event Hubs namespace at one throughput unit is roughly USD
    0.03 per TU-hour; a serverless Cosmos account bills per request unit; a
    Standard_LRS storage account holding a handful of blobs is a fraction of a
    cent. Fifteen minutes of all three is well under USD 0.05, and step 8
    deletes them. If the script is interrupted, delete the group by hand:

        Remove-AzResourceGroup -Name rg-expedition-journal -Force -AsJob

    PREREQUISITES: PowerShell 7 with the Az module and an authenticated session,
    and the .NET SDK band in global.json if you run step 6. This script never
    calls Connect-AzAccount for you -- sign in yourself so you can see which
    identity and subscription you are about to spend money in.

.EXAMPLE
    pwsh -File infra/powershell/cloud-expedition-journal.ps1
#>

[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string] $ResourceGroupName = 'rg-expedition-journal',
    [string] $StorageAccountName = ('stexpedition' + (Get-Random -Maximum 99999999)),
    [string] $NamespaceName = ('ehexpedition' + (Get-Random -Maximum 99999999)),
    [string] $CosmosAccountName = ('cosmosexpedition' + (Get-Random -Maximum 99999999)),
    [string] $HubName = 'telemetry',
    [string] $ConsumerGroupName = 'field-journal',
    [string] $CosmosDatabaseName = 'expedition',
    [string] $CosmosContainerName = 'journal'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A namespace name becomes a DNS label: 6-50 characters, letters, digits, and
# hyphens, starting with a letter. A storage account name is stricter still:
# 3-24 characters, lower-case letters and digits only. A Cosmos account name is
# 3-44 characters with the same alphabet plus hyphens.
$StorageAccountName = ($StorageAccountName.ToLowerInvariant() -replace '[^a-z0-9]', '')
if ($StorageAccountName.Length -gt 24) { $StorageAccountName = $StorageAccountName.Substring(0, 24) }

$NamespaceName = ($NamespaceName.ToLowerInvariant() -replace '[^a-z0-9-]', '')
if ($NamespaceName.Length -gt 50) { $NamespaceName = $NamespaceName.Substring(0, 50) }

$CosmosAccountName = ($CosmosAccountName.ToLowerInvariant() -replace '[^a-z0-9-]', '')
if ($CosmosAccountName.Length -gt 44) { $CosmosAccountName = $CosmosAccountName.Substring(0, 44) }

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

$principalId = (Get-AzADUser -SignedIn).Id

# -----------------------------------------------------------------------------
Write-Step '1. Create the resource group (the teardown handle)'
# -----------------------------------------------------------------------------
New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Tag $tags |
    Format-Table ResourceGroupName, Location, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '2. Create the storage account with shared-key access switched off'
# -----------------------------------------------------------------------------
# -AllowSharedKeyAccess $false is the setting that makes the identity argument
# real: with it, an account key cannot be used even if one leaks, and every SDK
# path that silently prefers a key fails immediately instead of on the day
# somebody audits it.
$storage = New-AzStorageAccount `
    -ResourceGroupName $ResourceGroupName `
    -Name $StorageAccountName `
    -Location $Location `
    -SkuName 'Standard_LRS' `
    -Kind 'StorageV2' `
    -MinimumTlsVersion 'TLS1_2' `
    -AllowBlobPublicAccess $false `
    -AllowSharedKeyAccess $false `
    -Tag $tags

$storage | Format-Table StorageAccountName, Location, @{ Name = 'Sku'; Expression = { $_.Sku.Name } }
$storageScope = $storage.Id

# -----------------------------------------------------------------------------
Write-Step '3. Create the namespace and the hub with local auth switched off'
# -----------------------------------------------------------------------------
# -DisableLocalAuth does for Event Hubs what -AllowSharedKeyAccess $false does
# for Storage: SAS policies stop working, including the
# RootManageSharedAccessKey rule that every quick-start uses.
#
# Four partitions, matching infra/local/eventhubs/config.json, so the number of
# concurrent consumer instances that can do useful work is the same locally and
# here: four.
$namespace = New-AzEventHubNamespace `
    -ResourceGroupName $ResourceGroupName `
    -Name $NamespaceName `
    -Location $Location `
    -SkuName 'Standard' `
    -SkuCapacity 1 `
    -MinimumTlsVersion '1.2' `
    -DisableLocalAuth `
    -Tag $tags

$namespace | Format-Table Name, Location, @{ Name = 'Sku'; Expression = { $_.SkuName } }, ProvisioningState

New-AzEventHub `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -Name $HubName `
    -PartitionCount 4 `
    -CleanupPolicy 'Delete' `
    -RetentionTimeInHour 1 |
    Format-Table Name, PartitionCount, Status

New-AzEventHubConsumerGroup `
    -ResourceGroupName $ResourceGroupName `
    -NamespaceName $NamespaceName `
    -EventHubName $HubName `
    -Name $ConsumerGroupName |
    Format-Table Name

$namespaceScope = $namespace.Id

# -----------------------------------------------------------------------------
Write-Step '4. Create the Cosmos account, database, and partitioned container'
# -----------------------------------------------------------------------------
# Serverless, because this workload is a few hundred requests in a burst and
# then nothing: provisioned throughput would bill for reserved capacity nobody
# uses. The partition key path matches CosmosJournalProjection.PartitionKeyPath
# -- the container's physical layout and the application's dominant query have
# to agree, and this is the one place that agreement is declared.
$cosmos = New-AzCosmosDBAccount `
    -ResourceGroupName $ResourceGroupName `
    -Name $CosmosAccountName `
    -Location $Location `
    -DefaultConsistencyLevel 'Session' `
    -EnableFreeTier $false `
    -Capability 'EnableServerless' `
    -Tag $tags

$cosmos | Format-Table Name, Location, DocumentEndpoint

New-AzCosmosDBSqlDatabase `
    -ResourceGroupName $ResourceGroupName `
    -AccountName $CosmosAccountName `
    -Name $CosmosDatabaseName |
    Format-Table Name

New-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroupName `
    -AccountName $CosmosAccountName `
    -DatabaseName $CosmosDatabaseName `
    -Name $CosmosContainerName `
    -PartitionKeyPath '/stationId' `
    -PartitionKeyKind 'Hash' |
    Format-Table Name

# -----------------------------------------------------------------------------
Write-Step '5. Grant data-plane roles only'
# -----------------------------------------------------------------------------
# Owner on a resource grants management rights, not data rights. Nothing below
# is a control-plane role, and granting one would not be least privilege even if
# it happened to work.
foreach ($role in @(
        'Storage Blob Data Contributor',
        'Storage Queue Data Contributor',
        'Storage Table Data Contributor')) {
    New-AzRoleAssignment -ObjectId $principalId -RoleDefinitionName $role -Scope $storageScope | Out-Null
    Write-Information "granted: $role"
}

foreach ($role in @(
        'Azure Event Hubs Data Sender',
        'Azure Event Hubs Data Receiver')) {
    New-AzRoleAssignment -ObjectId $principalId -RoleDefinitionName $role -Scope $namespaceScope | Out-Null
    Write-Information "granted: $role"
}

# Cosmos data-plane RBAC is a separate system with its own cmdlet and its own
# built-in definitions. 00000000-...-0002 is Cosmos DB Built-in Data
# Contributor. There is no portal blade for this and no New-AzRoleAssignment
# equivalent; a Contributor who cannot read a document has usually hit exactly
# this.
New-AzCosmosDBSqlRoleAssignment `
    -ResourceGroupName $ResourceGroupName `
    -AccountName $CosmosAccountName `
    -RoleDefinitionId '00000000-0000-0000-0000-000000000002' `
    -PrincipalId $principalId `
    -Scope '/' | Out-Null
Write-Information 'granted: Cosmos DB Built-in Data Contributor'

Write-Information '-- what the identity can actually do, as the platform sees it'
Get-AzRoleAssignment -ObjectId $principalId -Scope $storageScope |
    Format-Table RoleDefinitionName, Scope

# -----------------------------------------------------------------------------
Write-Step '6. Run the capstone against these resources'
# -----------------------------------------------------------------------------
# Role assignments take a minute or two to propagate. A 403 immediately after
# step 5 is usually that, not a wrong role.
Write-Information 'Waiting 90s for role assignments to propagate...'
Start-Sleep -Seconds 90

$cosmosEndpoint = $cosmos.DocumentEndpoint

Write-Information 'Run the capstone from the repository root with:'
Write-Information ''
Write-Information '  $env:EXPEDITION_ENVIRONMENT = ''live'''
Write-Information "  `$env:EXPEDITION_STORAGE_ACCOUNT = '$StorageAccountName'"
Write-Information "  `$env:EXPEDITION_EVENTHUBS_NAMESPACE = '$NamespaceName'"
Write-Information "  `$env:EXPEDITION_COSMOS_ENDPOINT = '$cosmosEndpoint'"
Write-Information '  dotnet run --project capstones/cloud-expedition-journal/solution'
Write-Information ''
Write-Information 'The run refuses to start while a key, connection string, or SAS token is'
Write-Information 'in the environment, so use a session that never dot-sourced emulator.env.'

$runReply = Read-Host 'Run it now from this session? [y/N]'
if ($runReply -eq 'y' -or $runReply -eq 'Y') {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    foreach ($name in @(
            'AZURITE_CONNECTION_STRING',
            'EVENTHUBS_EMULATOR_CONNECTION_STRING',
            'COSMOS_EMULATOR_ENDPOINT',
            'COSMOS_EMULATOR_KEY')) {
        Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
    }

    $env:EXPEDITION_ENVIRONMENT = 'live'
    $env:EXPEDITION_STORAGE_ACCOUNT = $StorageAccountName
    $env:EXPEDITION_EVENTHUBS_NAMESPACE = $NamespaceName
    $env:EXPEDITION_COSMOS_ENDPOINT = $cosmosEndpoint

    dotnet run --project (Join-Path $repoRoot 'capstones/cloud-expedition-journal/solution')
}

# -----------------------------------------------------------------------------
Write-Step '7. Read the operator''s view that no emulator emits'
# -----------------------------------------------------------------------------
Write-Information '-- namespace throughput, the metric a lagging consumer shows up in'
Get-AzMetric `
    -ResourceId $namespaceScope `
    -MetricName 'IncomingMessages', 'OutgoingMessages' `
    -TimeGrain 00:01:00 `
    -WarningAction SilentlyContinue |
    Format-Table Name, Unit

Write-Information '-- Cosmos request units consumed, which is what the invoice is computed from'
Get-AzMetric `
    -ResourceId $cosmos.Id `
    -MetricName 'TotalRequestUnits' `
    -TimeGrain 00:01:00 `
    -WarningAction SilentlyContinue |
    Format-Table Name, Unit

Write-Information '-- what is left in the container after the run''s own teardown'
Get-AzStorageContainer -Context $storage.Context -ErrorAction SilentlyContinue |
    Format-Table Name, LastModified

# -----------------------------------------------------------------------------
Write-Step '8. Delete everything'
# -----------------------------------------------------------------------------
# Three meters stop here: the namespace's hourly throughput-unit charge, the
# Cosmos account's request and storage charges, and the storage account's
# capacity and transaction charges. The application's own teardown removes the
# container, queues, table, and Cosmos database; the accounts themselves are
# only ever removed by this.
Remove-AzResourceGroup -Name $ResourceGroupName -Force | Out-Null

Write-Information ''
Write-Information "Deleted resource group $ResourceGroupName. Verify with:"
Write-Information "  Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue"
