#Requires -Version 7.0
#Requires -Modules Az.Storage

<#
.SYNOPSIS
    module.blob-storage -- emulator lab, Azure PowerShell.

.DESCRIPTION
    Drives the blob data plane end to end against Azurite: container, upload,
    metadata, tags, prefix listing, hierarchical listing, download, teardown.

    This is the twin of infra/azure-cli/blob-storage.sh: the same ten steps, in
    the same order, with the same names, so the two can be read side by side.

    COST: none. Every command below talks to 127.0.0.1:10000, not to Azure. The
    well-known Azurite account name and key are emulator-only credentials; they
    grant access to nothing outside this machine, which is why they may appear
    in source. A real account key must never be written down like this.

    TO RUN THE SAME STEPS AGAINST AZURE instead of the emulator, replace the
    storage context with an Entra ID one:

        $ctx = New-AzStorageContext -StorageAccountName <account> -UseConnectedAccount

    That needs the Storage Blob Data Contributor role on the account. See
    infra/powershell/storage-account.ps1, which creates an account configured
    exactly that way.

    PREREQUISITES: PowerShell 7 with the Az.Storage module and a running Azurite
    container (docker compose up -d azurite). No sign-in is required for the
    emulator path.

.EXAMPLE
    pwsh -File infra/powershell/blob-storage.ps1
#>

[CmdletBinding()]
param(
    [string] $ContainerName = 'expedition-artifacts',
    [string] $ConnectionString = $env:AZURITE_CONNECTION_STRING
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$azuriteAccount = 'devstoreaccount1'
$azuriteKey = 'Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=='

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = "DefaultEndpointsProtocol=http;AccountName=$azuriteAccount;AccountKey=$azuriteKey;BlobEndpoint=http://127.0.0.1:10000/$azuriteAccount;"
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("blob-lab-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $workDir

function Write-Step {
    param([Parameter(Mandatory)][string] $Title)
    Write-Information ''
    Write-Information "== $Title"
}

try {
    # -------------------------------------------------------------------------
    Write-Step '0. Confirm the endpoint that is about to be written to'
    # -------------------------------------------------------------------------
    # Printing the endpoint, not the key. If this does not say 127.0.0.1 you are
    # about to write to a real account.
    $ctx = New-AzStorageContext -ConnectionString $ConnectionString
    Write-Information "blob endpoint : $($ctx.BlobEndPoint)"

    # -------------------------------------------------------------------------
    Write-Step '1. Create the container'
    # -------------------------------------------------------------------------
    # A container is the only real grouping level. Everything below it is one
    # flat namespace of names, so this is also the last real directory you get.
    New-AzStorageContainer -Name $ContainerName -Context $ctx |
        Format-Table -Property Name, PublicAccess |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '2. Upload blobs whose names only look like paths'
    # -------------------------------------------------------------------------
    # No directory is created by any of these. The slashes are part of the name.
    Set-Content -Path (Join-Path $workDir 'frame-0001.jpg') -Value 'frame one'
    Set-Content -Path (Join-Path $workDir 'frame-0002.jpg') -Value 'frame two'
    Set-Content -Path (Join-Path $workDir 'frame-0001-delta.jpg') -Value 'frame three'
    Set-Content -Path (Join-Path $workDir 'manifest.json') -Value '{"expedition":"field-journal"}'

    $uploads = @(
        @{ File = 'frame-0001.jpg'; Blob = 'observations/station-bravo/2026/07/06/frame-0001.jpg' }
        @{ File = 'frame-0002.jpg'; Blob = 'observations/station-bravo/2026/07/06/frame-0002.jpg' }
        @{ File = 'frame-0001-delta.jpg'; Blob = 'observations/station-delta/2026/07/06/frame-0001.jpg' }
        @{ File = 'manifest.json'; Blob = 'manifest.json' }
    )

    foreach ($upload in $uploads) {
        $null = Set-AzStorageBlobContent `
            -Container $ContainerName `
            -File (Join-Path $workDir $upload.File) `
            -Blob $upload.Blob `
            -Context $ctx `
            -Force
    }

    Write-Information "uploaded $($uploads.Count) blobs and 0 directories"

    # -------------------------------------------------------------------------
    Write-Step '3. Set metadata on one blob'
    # -------------------------------------------------------------------------
    # Metadata rides along with the blob and comes back with its properties. It
    # is never indexed, so it can describe a blob you already know how to find
    # and nothing more.
    $target = 'observations/station-bravo/2026/07/06/frame-0001.jpg'
    $blob = Get-AzStorageBlob -Container $ContainerName -Blob $target -Context $ctx
    $blob.BlobClient.SetMetadata(@{
        station     = 'station-bravo'
        capturedUtc = '2026-07-06T04:12:55Z'
    }) | Out-Null

    $properties = $blob.BlobClient.GetProperties().Value
    $properties.Metadata.GetEnumerator() |
        Sort-Object -Property Key |
        Format-Table -Property Key, Value |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '4. Set tags on the same blob'
    # -------------------------------------------------------------------------
    # Tags are the indexed twin of metadata: a separate call to read, but the
    # only one the service can search across an entire account.
    $blob.BlobClient.SetTags(@{
        station   = 'station-bravo'
        retention = 'cold'
    }) | Out-Null

    $blob.BlobClient.GetTags().Value.Tags.GetEnumerator() |
        Sort-Object -Property Key |
        Format-Table -Property Key, Value |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '5. List by prefix'
    # -------------------------------------------------------------------------
    # A prefix scan is a string comparison. The trailing slash is what keeps
    # 'station-bravo' from also matching 'station-bravo-2'.
    Get-AzStorageBlob -Container $ContainerName -Prefix 'observations/station-bravo/' -Context $ctx |
        Select-Object -ExpandProperty Name |
        Out-String |
        Write-Information

    # -------------------------------------------------------------------------
    Write-Step '6. List the same blobs hierarchically'
    # -------------------------------------------------------------------------
    # The delimiter tells the service where to stop and fold. Same data, same
    # container, different view: nothing was moved, created, or renamed.
    $folded = Get-AzStorageBlob -Container $ContainerName -Prefix 'observations/' -Context $ctx |
        ForEach-Object { ($_.Name -split '/')[0..1] -join '/' } |
        Sort-Object -Unique
    $folded | Out-String | Write-Information

    # -------------------------------------------------------------------------
    Write-Step '7. Page the listing explicitly'
    # -------------------------------------------------------------------------
    # -MaxCount caps what one call returns. In an SDK this is the page size, and
    # it is the unit both the request count and the bill are measured in.
    $token = $null
    $page = 0
    do {
        $batch = Get-AzStorageBlob -Container $ContainerName -MaxCount 2 -ContinuationToken $token -Context $ctx
        if ($null -eq $batch -or $batch.Count -eq 0) { break }
        $page++
        Write-Information "page ${page}: $($batch.Count) blobs"
        $token = $batch[$batch.Count - 1].ContinuationToken
    } while ($null -ne $token)

    # -------------------------------------------------------------------------
    Write-Step '8. Download a blob and compare it byte for byte'
    # -------------------------------------------------------------------------
    $roundTrip = Join-Path $workDir 'roundtrip.jpg'
    $null = Get-AzStorageBlobContent `
        -Container $ContainerName `
        -Blob $target `
        -Destination $roundTrip `
        -Context $ctx `
        -Force

    $original = Get-FileHash -Path (Join-Path $workDir 'frame-0001.jpg') -Algorithm SHA256
    $copy = Get-FileHash -Path $roundTrip -Algorithm SHA256
    if ($original.Hash -eq $copy.Hash) {
        Write-Information 'round trip identical: blob storage stores opaque bytes, unchanged'
    }
    else {
        throw 'Round trip differed. That should be impossible; investigate before continuing.'
    }

    # -------------------------------------------------------------------------
    Write-Step '9. Delete the container'
    # -------------------------------------------------------------------------
    # One delete removes every blob under it. Against a real account this is the
    # step that stops the bill, so it is never optional.
    Remove-AzStorageContainer -Name $ContainerName -Context $ctx -Force
    Write-Information "removed container $ContainerName"

    Write-Information ''
    Write-Information 'Done. Nothing remains in the emulator.'
}
finally {
    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
