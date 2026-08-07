#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.Resources, Az.CosmosDB, Az.Monitor

<#
.SYNOPSIS
    module.cosmos-development -- live checkpoint, Azure PowerShell.

.DESCRIPTION
    Creates a Cosmos DB account with a deliberately small throughput budget,
    runs the lesson companion against it, and reads the two things the emulator
    cannot produce: real pages and real 429s.

        pwsh infra/powershell/cosmos-development.ps1

    The Azure CLI twin infra/azure-cli/cosmos-development.sh performs the same
    steps in the same order with the same names, so the two can be read side by
    side.

    WHY THIS CHECKPOINT IS REQUIRED. Module 10's checkpoint was about cost. This
    one is about behaviour, and four behaviours central to this module have no
    local equivalent at all:

      * Pagination. The emulator ignores MaxItemCount and returns every match in
        one page with a null continuation token, no matter how many documents
        are involved. The drain loop the exercise builds is therefore never
        exercised locally past its first iteration (step 5).
      * Throttling. There is no rate limiter locally. Eight hundred concurrent
        writes against a container provisioned for 400 RU/s all succeed, so 429,
        the x-ms-retry-after-ms header, and every retry policy written against
        them are untested until they run here (step 6).
      * Time-to-live. The emulator accepts a TTL and never acts on it, so
        "let the service delete it" cannot be observed (step 7).
      * Consistency. Session tokens, and the read charge that Strong consistency
        roughly doubles, are account-level behaviour with no local switch
        (step 8).

    COST: the account below is provisioned rather than serverless, because a
    serverless account has no throughput ceiling to exceed and therefore cannot
    demonstrate 429. Provisioned throughput bills by the hour: 400 RU/s is
    roughly USD 0.008 per hour, so a thirty-minute run costs well under a cent.
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
    [string]$AccountName = "cosmosdataplane$(Get-Random)$(Get-Random)"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$databaseName = 'expedition-journal'
$containerName = 'readings'

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
Write-Step '2. Create the account and database'
# -----------------------------------------------------------------------------
# Session is the default consistency level and the one an application developer
# has to understand, because it is the one with a token: a client reads its own
# writes, and two clients may briefly disagree. Step 8 changes it.
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
Write-Step '3. Create the container the companion uses'
# -----------------------------------------------------------------------------
# 400 RU/s is the minimum a provisioned container can have, and it is chosen
# here precisely because it is easy to exceed. A container generous enough never
# to throttle would hide the behaviour this checkpoint exists to show.
#
# -TtlInSeconds -1 enables time-to-live without expiring anything: documents
# live forever unless one of them carries its own /ttl field. Step 7 changes
# that.
New-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $containerName `
    -PartitionKeyPath '/stationId' `
    -PartitionKeyKind 'Hash' `
    -Throughput 400 `
    -TtlInSeconds -1 |
    Format-Table Name, Id

Write-Information '-- what the service records about the container it just made'
$container = Get-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $containerName

$container.Resource | Format-List Id, PartitionKey, DefaultTtl

# The conflict resolution policy is LastWriterWins on /_ts by default. It only
# ever applies to multi-region write accounts -- a single-region account
# serialises writes at the primary, which is exactly why an ETag is the only
# tool available for the single-region races the companion demonstrates.

# -----------------------------------------------------------------------------
Write-Step "4. Read the account's own limits"
# -----------------------------------------------------------------------------
Get-AzCosmosDBSqlContainerThroughput `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $containerName |
    Format-List Throughput, MinimumThroughput

Write-Information '-- 400 RU/s is roughly 400 point reads, or 80 writes, per second'

# -----------------------------------------------------------------------------
Write-Step '5. Run the companion here, and watch it page'
# -----------------------------------------------------------------------------
# Everything above is control plane. Pagination is a DATA plane behaviour, so it
# is observed by running the companion against this account rather than by a
# cmdlet. These are the assignments that point it here.
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
Write-Information '  dotnet run --project lessons/11-cosmos-development/DataPlane'
Write-Information ''
Write-Information "Section 2 printed 'Pages returned: 1' against the emulator. Here it prints"
Write-Information '5, with a continuation token several hundred characters long. Record both'
Write-Information 'numbers: the code did not change, and its behaviour did.'
Write-Information ''
Write-Information 'Section 1 is worth a second look too. Locally the point read and the query'
Write-Information 'both cost 1.00 RU. Here the query costs more, and that gap is the reason'
Write-Information 'ReadItemAsync exists as a separate method at all.'

# -----------------------------------------------------------------------------
Write-Step "6. Provoke a 429, and read it off the platform's own meters"
# -----------------------------------------------------------------------------
# A 400 RU/s container refuses work once the budget for the second is spent. The
# emulator has no such budget, so no local test can produce this.
#
# The load is deliberately generated with the SDK rather than with a cmdlet: the
# Az modules have no data plane for Cosmos SQL, which is itself worth knowing.
Write-Information ''
Write-Information 'With COSMOS_ENDPOINT and COSMOS_KEY set, generate more load than the'
Write-Information 'container is provisioned for:'
Write-Information ''
Write-Information '  dotnet run --project lessons/11-cosmos-development/DataPlane'
Write-Information ''
Write-Information 'then give the platform two or three minutes and read the meters.'

$since = (Get-Date).AddMinutes(-30)

Write-Information ''
Write-Information '-- total request units consumed, per minute'
Get-AzMetric `
    -ResourceId $account.Id `
    -MetricName 'TotalRequestUnits' `
    -TimeGrain '00:01:00' `
    -StartTime $since `
    -WarningAction SilentlyContinue |
    Select-Object -ExpandProperty Data |
    Format-Table TimeStamp, Total

Write-Information ''
Write-Information '-- requests split by status code; 429 is the one that matters'
Get-AzMetric `
    -ResourceId $account.Id `
    -MetricName 'TotalRequests' `
    -TimeGrain '00:01:00' `
    -StartTime $since `
    -WarningAction SilentlyContinue |
    Select-Object -ExpandProperty Data |
    Format-Table TimeStamp, Total

# A 429 is not an error in the sense a 500 is: it is flow control, and the SDK
# retries it for you nine times within thirty seconds by default. That default
# is why a throttled application usually presents as latency rather than as
# failures, and why the retry bounds belong in the client options rather than in
# a comment.

# -----------------------------------------------------------------------------
Write-Step '7. Let the service do the deleting'
# -----------------------------------------------------------------------------
# A default TTL of 300 seconds means every document without its own /ttl field
# is deleted five minutes after its last write. The service spends leftover
# throughput doing it, so it does not compete with the application -- which is
# the entire argument against deleting a million documents one call at a time.
Update-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $containerName `
    -PartitionKeyPath '/stationId' `
    -PartitionKeyKind 'Hash' `
    -TtlInSeconds 300 |
    Format-Table Name, Id

Write-Information "set a 300-second default time-to-live on $containerName"

(Get-AzCosmosDBSqlContainer `
    -ResourceGroupName $ResourceGroup `
    -AccountName $AccountName `
    -DatabaseName $databaseName `
    -Name $containerName).Resource |
    Format-List Id, DefaultTtl

Write-Information '-- wait five minutes and query the container: it empties itself, and the'
Write-Information '   application was charged nothing for the deletions'

# -----------------------------------------------------------------------------
Write-Step '8. Change what a read is allowed to see'
# -----------------------------------------------------------------------------
# Strong consistency makes every read return the latest committed write, at
# roughly twice the RU charge and with a latency cost. It is available only
# because this account is single-region; a multi-region write account cannot
# offer it at all.
Update-AzCosmosDBAccount `
    -ResourceGroupName $ResourceGroup `
    -Name $AccountName `
    -DefaultConsistencyLevel 'Strong' |
    Format-Table Name, ConsistencyPolicy

Write-Information 'switched the account to Strong consistency'

Update-AzCosmosDBAccount `
    -ResourceGroupName $ResourceGroup `
    -Name $AccountName `
    -DefaultConsistencyLevel 'Session' |
    Format-Table Name, ConsistencyPolicy

Write-Information 'switched it back to Session'

# Run the companion once under each level and compare the RU charges in section
# 1. The difference is the price of never having to think about a session token.

# -----------------------------------------------------------------------------
Write-Step '9. Delete everything'
# -----------------------------------------------------------------------------
# Provisioned throughput bills by the hour whether or not a single request is
# made. An idle container at 400 RU/s costs about USD 6 a month to hold nothing.
Remove-AzResourceGroup -Name $ResourceGroup -Force | Out-Null

Write-Information ''
Write-Information "Deleted resource group $ResourceGroup. Verify with:"
Write-Information "  Get-AzResourceGroup -Name $ResourceGroup"
