#Requires -Version 7.0
#Requires -Modules Az.Storage

<#
.SYNOPSIS
    module.table-storage -- emulator lab, Azure PowerShell.

.DESCRIPTION
    Drives the table data plane end to end against Azurite: table, insert, point
    read, partition scan, table scan, optimistic concurrency, teardown.

    This is the twin of infra/azure-cli/table-storage.sh: the same ten steps, in
    the same order, with the same names, so the two can be read side by side.

    COST: none. Every command below talks to 127.0.0.1:10002, not to Azure. The
    well-known Azurite account name and key are emulator-only credentials; they
    grant access to nothing outside this machine, which is why they may appear
    in source. A real account key must never be written down like this.

    TO RUN THE SAME STEPS AGAINST AZURE instead of the emulator, pass the account:

        pwsh -File infra/powershell/table-storage.ps1 -StorageAccountName <account>

    That needs the Storage Table Data Contributor role on the account. See
    infra/powershell/storage-account.ps1, which creates an account configured
    exactly that way.

    NOTE: two rules exercised here -- the single-partition transaction limit and
    the 100-operation batch limit -- are NOT enforced by Azurite. See step 8.

    PREREQUISITES: PowerShell 7 with Az.Storage. The emulator path needs Azurite;
    the Azure path needs a signed-in account and the data-plane role above.

.EXAMPLE
    pwsh -File infra/powershell/table-storage.ps1
#>

[CmdletBinding()]
param(
    [string] $TableName = 'expeditionobservations',
    [string] $ConnectionString = $env:AZURITE_CONNECTION_STRING,
    [string] $StorageAccountName = $env:AZURE_STORAGE_ACCOUNT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$azuriteAccount = 'devstoreaccount1'
$azuriteKey = 'Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=='

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = "DefaultEndpointsProtocol=http;AccountName=$azuriteAccount;AccountKey=$azuriteKey;TableEndpoint=http://127.0.0.1:10002/$azuriteAccount;"
}

$partitionBravo = 'station-bravo|2026-07-06'
$partitionDelta = 'station-delta|2026-07-06'

function Write-Step {
    param([Parameter(Mandatory)][string] $Title)
    Write-Information ''
    Write-Information "== $Title"
}

function ConvertTo-Observation {
    param(
        [Parameter(Mandatory)][string] $PartitionKey,
        [Parameter(Mandatory)][string] $RowKey,
        [Parameter(Mandatory)][string] $StationId,
        [Parameter(Mandatory)][double] $TemperatureC
    )

    $entity = [Azure.Data.Tables.TableEntity]::new($PartitionKey, $RowKey)
    $entity['StationId'] = $StationId
    $entity['TemperatureC'] = $TemperatureC
    $entity['Status'] = 'pending'
    $entity
}

try {
    # -------------------------------------------------------------------------
    Write-Step '0. Confirm the endpoint that is about to be written to'
    # -------------------------------------------------------------------------
    # Printing the endpoint, not the key. If this does not say 127.0.0.1 you are
    # about to write to a real account.
    $ctx = if ([string]::IsNullOrWhiteSpace($StorageAccountName)) {
        New-AzStorageContext -ConnectionString $ConnectionString
    }
    else {
        New-AzStorageContext -StorageAccountName $StorageAccountName -UseConnectedAccount
    }
    Write-Information "table endpoint : $($ctx.TableEndPoint)"

    # -------------------------------------------------------------------------
    Write-Step '1. Create the table'
    # -------------------------------------------------------------------------
    # A table name is alphanumeric only: no hyphens, no underscores. It also has
    # no schema, so this is the last structural decision the service makes for
    # you.
    New-AzStorageTable -Name $TableName -Context $ctx |
        Format-Table -Property Name, Uri |
        Out-String |
        Write-Information

    $client = (Get-AzStorageTable -Name $TableName -Context $ctx).Context.TableClient

    # -------------------------------------------------------------------------
    Write-Step '2. Insert observations across two partitions'
    # -------------------------------------------------------------------------
    # The partition key is station AND day. The row key is a fixed-width UTC
    # timestamp, because row keys sort ascending as STRINGS: '9:05' would sort
    # after '10:05' and every range query would be silently wrong.
    foreach ($minute in '00', '05', '10') {
        $null = $client.AddEntity((ConvertTo-Observation `
                    -PartitionKey $partitionBravo `
                    -RowKey "2026-07-06T12:${minute}:00.0000000Z" `
                    -StationId 'station-bravo' `
                    -TemperatureC -3.5))
    }

    $null = $client.AddEntity((ConvertTo-Observation `
                -PartitionKey $partitionDelta `
                -RowKey '2026-07-06T12:00:00.0000000Z' `
                -StationId 'station-delta' `
                -TemperatureC -7.25))

    Write-Information 'inserted 4 entities into 2 partitions'

    # -------------------------------------------------------------------------
    Write-Step '3. Point read: both keys known'
    # -------------------------------------------------------------------------
    # One entity, one lookup, and a cost that does not change as the table
    # grows. This is the only query shape worth designing keys around.
    $point = $client.GetEntity[Azure.Data.Tables.TableEntity](
        $partitionBravo, '2026-07-06T12:05:00.0000000Z').Value
    Write-Information "point read : $($point.RowKey) status=$($point['Status']) etag=$($point.ETag)"

    # -------------------------------------------------------------------------
    Write-Step '4. Partition scan: partition key only'
    # -------------------------------------------------------------------------
    # Bounded by the partition, which is why the partition key carries the day:
    # one station reporting every minute would otherwise grow one partition
    # forever.
    foreach ($row in $client.Query[Azure.Data.Tables.TableEntity]("PartitionKey eq '$partitionBravo'")) {
        Write-Information "  partition scan : $($row.RowKey)"
    }

    # -------------------------------------------------------------------------
    Write-Step '5. Table scan: the query that looks identical and is not'
    # -------------------------------------------------------------------------
    # Same rows, same syntax, no PartitionKey predicate. StationId is a
    # duplicated column, not a key, so the service reads every row in the table
    # to find these.
    foreach ($row in $client.Query[Azure.Data.Tables.TableEntity]("StationId eq 'station-bravo'")) {
        Write-Information "  table scan     : $($row.RowKey)"
    }

    Write-Information 'same result, whole-table cost: this is the mistake that only shows up at scale'

    # -------------------------------------------------------------------------
    Write-Step '6. A key range query, done with the row key'
    # -------------------------------------------------------------------------
    # Because row keys are fixed-width and sorted, a range is expressible as a
    # string comparison against the key rather than as a filter on a property.
    $rangeFilter = "PartitionKey eq '$partitionBravo' and RowKey ge '2026-07-06T12:05:00.0000000Z'"
    foreach ($row in $client.Query[Azure.Data.Tables.TableEntity]($rangeFilter)) {
        Write-Information "  range          : $($row.RowKey)"
    }

    # -------------------------------------------------------------------------
    Write-Step '7. Optimistic concurrency with the entity ETag'
    # -------------------------------------------------------------------------
    # Read the version, then write betting on it. Passing [Azure.ETag]::All
    # would be the last-write-wins default that module 5 spent a whole module
    # removing.
    $target = $client.GetEntity[Azure.Data.Tables.TableEntity](
        $partitionBravo, '2026-07-06T12:00:00.0000000Z').Value
    $readEtag = $target.ETag
    Write-Information "read etag : $readEtag"

    $target['Status'] = 'ingested'
    $null = $client.UpdateEntity($target, $readEtag, [Azure.Data.Tables.TableUpdateMode]::Replace)
    Write-Information 'first write with a fresh etag: accepted'

    $target['Status'] = 'rejected'
    try {
        $null = $client.UpdateEntity($target, $readEtag, [Azure.Data.Tables.TableUpdateMode]::Replace)
        Write-Information 'unexpected: the stale etag was accepted'
    }
    catch [Azure.RequestFailedException] {
        Write-Information "second write with the SAME (now stale) etag: rejected, $($_.Exception.ErrorCode) (HTTP $($_.Exception.Status))"
    }

    # -------------------------------------------------------------------------
    Write-Step '8. What the emulator does not enforce'
    # -------------------------------------------------------------------------
    # Two rules this module teaches are NOT enforced by Azurite: a transactional
    # batch may not span partitions, and it may not exceed 100 operations. Azure
    # rejects both with InvalidInput; Azurite accepts the first and returns an
    # unparseable response for the second.
    #
    # This is why the exercise validates them in your own code.
    Write-Information 'see lessons/07-table-storage/README.md#what-the-emulator-will-not-tell-you'
}
finally {
    # -------------------------------------------------------------------------
    Write-Step '9. Delete the table'
    # -------------------------------------------------------------------------
    # One delete removes every entity in it. Against a real account this is the
    # step that stops the bill, so it runs in a finally block: it happens even
    # when a step above fails.
    if ($null -ne $ctx) {
        Remove-AzStorageTable -Name $TableName -Context $ctx -Force -ErrorAction SilentlyContinue
        Write-Information ''
        Write-Information 'Done. Nothing remains in the emulator.'
    }
}
