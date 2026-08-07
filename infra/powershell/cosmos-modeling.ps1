#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.CosmosDB, Az.Monitor

<#
.SYNOPSIS
    module.cosmos-modeling -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Creates a Cosmos DB account, builds the two containers the lesson companion
    builds, reads the numbers the emulator refuses to produce -- real request
    charges, real throughput ceilings, real partition key ranges -- and deletes
    it all again.

        pwsh infra/powershell/cosmos-modeling.ps1

    The Azure CLI twin infra/azure-cli/cosmos-modeling.sh performs the same
    steps in the same order with the same names, so the two can be read side by
    side.

    WHY THIS CHECKPOINT IS REQUIRED. This module is about cost, and the emulator
    does not model cost. Four things are simply not observable locally:

      * Request charges. Every response the emulator returns is billed at 1 RU,
        including a 200-document cross-partition query. The whole subject of
        this module is the difference between those numbers (step 5).
      * Query metrics. retrievedDocumentCount and indexUtilizationRatio come
        back as zero from the emulator (step 5).
      * Physical partitions. The emulator reports exactly one feed range no
        matter how much data is written, so a partition split cannot happen
        (step 6).
      * Throttling. There is no rate limit locally, so 429 and its retry-after
        header never appear (step 7).

    COST: a Cosmos DB account with two containers at 400 RU/s each bills roughly
    USD 0.008 per 100 RU/s per hour, so ten minutes of both is under a cent.
    Step 9 deletes everything. If the script is interrupted:

        Remove-AzResourceGroup -Name rg-expedition-checkpoint -Force

    PREREQUISITES: the Az PowerShell modules and an authenticated session. This
    script never calls Connect-AzAccount for you -- sign in yourself so you can
    see which identity and subscription you are about to spend money in.

.PARAMETER Location
    The Azure region to create resources in.

.PARAMETER ResourceGroup
    The resource group that holds everything, and that step 9 deletes.

.PARAMETER AccountName
    The Cosmos DB account name. Must be globally unique.
#>

[CmdletBinding()]
param(
    [string]$Location = 'westeurope',
    [string]$ResourceGroup = 'rg-expedition-checkpoint',
    [string]$AccountName = "cosmosexpedition$(Get-Random)$(Get-Random)"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$databaseName = 'expedition'
$byStation = 'readings-by-station'
$byDay = 'readings-by-day'

# A Cosmos account name becomes a DNS label: 3-44 characters, lower-case
# letters, digits and hyphens, and it must be globally unique.
$AccountName = ($AccountName.ToLowerInvariant() -replace '[^a-z0-9-]', '')
if ($AccountName.Length -gt 44) { $AccountName = $AccountName.Substring(0, 44) }

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
Get-AzContext | Format-List Name, Account, Subscription, Tenant

$reply = Read-Host 'Create resources in the subscription above? [y/N]'
if ($reply -ne 'y' -and $reply -ne 'Y') {
    throw 'Aborted.'
}

# -----------------------------------------------------------------------------
Write-Step '1. Create the resource group (the teardown handle)'
# -----------------------------------------------------------------------------
New-AzResourceGroup -Name $ResourceGroup -Location $Location -Tag $tags |
    Format-Table ResourceGroupName, Location, ProvisioningState

# -----------------------------------------------------------------------------
Write-Step '2. Create the account'
# -----------------------------------------------------------------------------
# Session consistency is the default and the one worth understanding: a client
# reads its own writes, and two clients may briefly disagree. Strong consistency
# is available only within a single region and roughly doubles the RU charge of
# every read, which is a modelling decision rather than a switch to flip late.
New-AzCosmosDBAccount `
    -ResourceGroupName $ResourceGroup `
    -Name $AccountName `
    -Location $Location `
    -DefaultConsistencyLevel 'Session' `
    -ApiKind 'Sql' `
    -Tag $tags |
    Format-Table Name, Location, DocumentEndpoint

New-AzCosmosDBSqlDatabase `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -Name $databaseName |
    Format-Table Name, Id

# -----------------------------------------------------------------------------
Write-Step '3. Create the two containers the companion creates'
# -----------------------------------------------------------------------------
# The partition key path is fixed at creation. There is no cmdlet parameter that
# changes it, because changing it means moving every document into a different
# logical partition: it is a migration, not an edit. That single fact is why
# this module exists.
New-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byStation `
    -PartitionKeyPath '/stationId' `
    -PartitionKeyKind 'Hash' `
    -Throughput 400 |
    Format-Table Name, Id

New-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byDay `
    -PartitionKeyPath '/day' `
    -PartitionKeyKind 'Hash' `
    -Throughput 400 |
    Format-Table Name, Id

Write-Information '-- what the service records about the containers it just made'
$container = Get-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byStation

$container.Resource.PartitionKey | Format-List Paths, Kind, Version
$container.Resource.IndexingPolicy | Format-List IndexingMode, Automatic

# -----------------------------------------------------------------------------
Write-Step '4. Configure throughput: manual, autoscale, and back'
# -----------------------------------------------------------------------------
# Manual provisioning bills the number you set, every hour, whether or not the
# container is touched. Autoscale bills what was used at 1.5x the manual rate,
# and never less than 10% of the maximum -- which is the arithmetic
# ThroughputPlanner.RelativeAutoscaleCost performs in the exercise.
Get-AzCosmosDBSqlContainerThroughput `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byStation |
    Format-List Throughput, MinimumThroughput, AutoscaleSettings

Invoke-AzCosmosDBSqlContainerThroughputMigration `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byStation `
    -ThroughputType 'Autoscale' |
    Format-List Throughput, AutoscaleSettings

Write-Information "migrated $byStation to autoscale"

# MinimumThroughput is the floor the service will not let you go below, and it
# rises with the data stored and with the number of physical partitions the
# container has ever had. It never comes back down: a container that was briefly
# scaled to 100,000 RU/s keeps a higher floor forever.

# -----------------------------------------------------------------------------
Write-Step '5. Read the numbers the emulator will not produce'
# -----------------------------------------------------------------------------
# Everything above is control plane. The request charge is a DATA plane header,
# so it is read by running the companion against this account rather than by a
# cmdlet. These are the exports that point it here.
$account = Get-AzCosmosDBAccount -ResourceGroupName $ResourceGroup -Name $AccountName

# The companion authenticates with an account key because that is the credential
# the Cosmos emulator accepts, and the code is unchanged between the two runs.
# A Cosmos primary master key is an account-wide root credential: it grants full
# read/write over every database in the account and cannot be scoped down. Keep
# it in the shell variable below, never in a file, and let the resource group
# deletion in the last step revoke it. Module 12 and the capstone lab show the
# production posture instead: `--disable-local-auth true` on the account, a
# Cosmos DB data-plane role assignment, and `DefaultAzureCredential` in the app.
Write-Information ''
Write-Information 'To run the lesson companion against THIS account instead of the emulator:'
Write-Information "  `$env:COSMOS_ENDPOINT = '$($account.DocumentEndpoint)'"
Write-Information "  `$env:COSMOS_KEY = (Get-AzCosmosDBAccountKey -ResourceGroupName $ResourceGroup -Name $AccountName).PrimaryMasterKey"
Write-Information '  dotnet run --project lessons/10-cosmos-modeling/RequestUnits'
Write-Information ''
Write-Information 'Record the request charge printed for the point read, the single-partition'
Write-Information 'query and the cross-partition query. Locally all three are 1.00 RU. Here'
Write-Information 'they are not, and the ratio between them is the lesson.'

# -----------------------------------------------------------------------------
Write-Step '6. Look at the partition key ranges'
# -----------------------------------------------------------------------------
# A physical partition is created by the service, not by you. A new container
# starts with one; it splits when it passes 50 GB or when the provisioned
# throughput exceeds what one partition can serve. Every split halves the
# throughput each partition may spend, which is why a container that was fine at
# 10,000 RU/s over two partitions can start throttling at 10,000 over four.
#
# The emulator reports exactly one range forever, so this behaviour has no local
# equivalent at all.
$dayContainer = Get-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byDay

$dayContainer.Resource.PartitionKey | Format-List Paths, Kind, Version

Write-Information '-- partition key version 2 supports large (2 KB) key values; version 1 caps at 101 bytes'

# -----------------------------------------------------------------------------
Write-Step "7. Watch throttling and consumption on the platform's own meters"
# -----------------------------------------------------------------------------
# TotalRequestUnits is what you spent. TotalRequests split by status code 429 is
# what you were refused. Neither exists locally, because the emulator has no
# rate limiter: a load test against it proves nothing about capacity.
try {
    Get-AzMetric `
        -ResourceId $account.Id `
        -MetricName 'TotalRequestUnits', 'TotalRequests' `
        -TimeGrain '00:01:00' `
        -WarningAction SilentlyContinue |
        Format-Table Name, Unit
}
catch {
    Write-Information "(no metrics yet: an account under a few minutes old has nothing to report)"
}

# -----------------------------------------------------------------------------
Write-Step '8. Change an indexing policy, and see that it is asynchronous'
# -----------------------------------------------------------------------------
# Excluding a path is the only lever that makes writes cheaper. It is applied by
# a background reindex that leaves the container fully queryable throughout and
# consumes leftover RU/s while it runs, so on a busy container the saving does
# not appear immediately.
#
# Excluding "/*" and then including only the two paths the workload filters on
# is the standard shape: it says "index nothing unless I asked for it" rather
# than "index everything except these".
$included = @(
    New-AzCosmosDBSqlIncludedPath -Path '/stationId/?'
    New-AzCosmosDBSqlIncludedPath -Path '/day/?'
)

$indexingPolicy = New-AzCosmosDBSqlIndexingPolicy `
    -IncludedPath $included `
    -ExcludedPath '/*' `
    -IndexingMode 'Consistent' `
    -Automatic $true

Update-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byStation `
    -PartitionKeyPath '/stationId' `
    -PartitionKeyKind 'Hash' `
    -IndexingPolicy $indexingPolicy |
    Format-Table Name, Id

Write-Information "applied a narrowed indexing policy to $byStation"

$updated = Get-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $byStation

$updated.Resource.IndexingPolicy.IncludedPaths | Format-Table Path
$updated.Resource.IndexingPolicy.ExcludedPaths | Format-Table Path

# -----------------------------------------------------------------------------
Write-Step '9. Delete everything'
# -----------------------------------------------------------------------------
# Provisioned throughput bills by the hour whether or not a single request is
# made, and it is the meter people forget: an idle container at 400 RU/s costs
# about USD 6 a month for holding nothing.
Remove-AzResourceGroup -Name $ResourceGroup -Force | Out-Null

Write-Information ''
Write-Information "Deleted resource group $ResourceGroup. Verify with:"
Write-Information "  Get-AzResourceGroup -Name $ResourceGroup"
