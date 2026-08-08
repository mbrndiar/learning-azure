#Requires -Version 7.0
#Requires -Modules Az.Storage

<#
.SYNOPSIS
    module.queue-storage -- emulator lab, Azure PowerShell.

.DESCRIPTION
    Drives the queue data plane end to end against Azurite: queue, send, peek,
    receive with a visibility timeout, observe redelivery, delete, teardown.

    This is the twin of infra/azure-cli/queue-storage.sh: the same ten steps, in
    the same order, with the same names, so the two can be read side by side.

    COST: none. Every command below talks to 127.0.0.1:10001, not to Azure. The
    well-known Azurite account name and key are emulator-only credentials; they
    grant access to nothing outside this machine, which is why they may appear
    in source. A real account key must never be written down like this.

    TO RUN THE SAME STEPS AGAINST AZURE instead of the emulator, pass the account:

        pwsh -File infra/powershell/queue-storage.ps1 -StorageAccountName <account>

    That needs the Storage Queue Data Contributor role on the account. See
    infra/powershell/storage-account.ps1, which creates an account configured
    exactly that way.

    PREREQUISITES: PowerShell 7 with Az.Storage. The emulator path needs Azurite;
    the Azure path needs a signed-in account and the data-plane role above.

.EXAMPLE
    pwsh -File infra/powershell/queue-storage.ps1
#>

[CmdletBinding()]
param(
    [string] $QueueName = 'expedition-dispatch',
    [string] $ConnectionString = $env:AZURITE_CONNECTION_STRING,
    [string] $StorageAccountName = $env:AZURE_STORAGE_ACCOUNT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$azuriteAccount = 'devstoreaccount1'
$azuriteKey = 'Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=='

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = "DefaultEndpointsProtocol=http;AccountName=$azuriteAccount;AccountKey=$azuriteKey;QueueEndpoint=http://127.0.0.1:10001/$azuriteAccount;"
}

function Write-Step {
    param([Parameter(Mandatory)][string] $Title)
    Write-Information ''
    Write-Information "== $Title"
}

function ConvertTo-MessageBody {
    param([Parameter(Mandatory)][string] $Json)
    [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Json))
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
    Write-Information "queue endpoint : $($ctx.QueueEndPoint)"

    # -------------------------------------------------------------------------
    Write-Step '1. Create the queue'
    # -------------------------------------------------------------------------
    # A queue has no schema, no partitions, and no consumer groups. It is a
    # single flat backlog of small messages, and that is the whole model.
    New-AzStorageQueue -Name $QueueName -Context $ctx |
        Format-Table -Property Name, Uri |
        Out-String |
        Write-Information

    $queue = Get-AzStorageQueue -Name $QueueName -Context $ctx
    $client = $queue.QueueClient

    # -------------------------------------------------------------------------
    Write-Step '2. Send three work orders'
    # -------------------------------------------------------------------------
    # The body is Base64 because that is what the SDKs put on the wire by
    # default, and because it is the encoded size the 64 KiB limit applies to.
    foreach ($n in 1001, 1002, 1003) {
        $body = ConvertTo-MessageBody -Json "{`"workOrderId`":`"wo-$n`",`"operation`":`"ingest`"}"
        $receipt = $client.SendMessage($body).Value
        Write-Information "sent $($receipt.MessageId) expires $($receipt.ExpirationTime.UtcDateTime.ToString('u'))"
    }

    # -------------------------------------------------------------------------
    Write-Step '3. Look at the backlog without claiming it'
    # -------------------------------------------------------------------------
    # Peek returns no pop receipt, so a peeked message cannot be deleted and its
    # dequeue count does not advance. Peek is for dashboards, not for consumers.
    foreach ($peeked in $client.PeekMessages(3).Value) {
        Write-Information "peeked $($peeked.MessageId) dequeueCount=$($peeked.DequeueCount)"
    }

    $queue.ApproximateMessageCount |
        ForEach-Object { Write-Information "ApproximateMessageCount : $_" }

    # -------------------------------------------------------------------------
    Write-Step '4. Receive one message with a short visibility timeout'
    # -------------------------------------------------------------------------
    # Receiving hides the message; it does not remove it. The pop receipt is
    # proof of THIS receive, and it is the only thing that can delete or extend
    # it.
    $received = $client.ReceiveMessages(1, [TimeSpan]::FromSeconds(5)).Value[0]
    Write-Information "received $($received.MessageId) dequeueCount=$($received.DequeueCount)"
    Write-Information "invisible until $($received.NextVisibleOn.Value.UtcDateTime.ToString('u'))"

    $staleId = $received.MessageId
    $staleReceipt = $received.PopReceipt

    # -------------------------------------------------------------------------
    Write-Step '5. Wait out the visibility timeout and receive it again'
    # -------------------------------------------------------------------------
    # Nothing failed and nothing was retried. The message came back purely
    # because the handler -- here, Start-Sleep -- outlived the window it asked
    # for. This is what at-least-once delivery is, and no setting turns it off.
    Start-Sleep -Seconds 7

    $again = $client.ReceiveMessages(1, [TimeSpan]::FromSeconds(30)).Value[0]
    Write-Information "received $($again.MessageId) dequeueCount=$($again.DequeueCount)"
    Write-Information 'the dequeueCount above is 2: same message, second consumer'

    # -------------------------------------------------------------------------
    Write-Step '6. Prove the first pop receipt is now worthless'
    # -------------------------------------------------------------------------
    # The redelivery invalidated it. A consumer holding a stale receipt cannot
    # delete the message someone else is now working on, which is exactly the
    # protection you want.
    try {
        $null = $client.DeleteMessage($staleId, $staleReceipt)
        Write-Information 'unexpected: the stale pop receipt was accepted'
    }
    catch [Azure.RequestFailedException] {
        Write-Information "rejected as expected: $($_.Exception.ErrorCode) (HTTP $($_.Exception.Status))"
    }

    # -------------------------------------------------------------------------
    Write-Step '7. Receive and delete properly'
    # -------------------------------------------------------------------------
    # Delete is a separate call on purpose: the message survives a consumer that
    # crashes between receiving and finishing.
    $fresh = $client.ReceiveMessages(1, [TimeSpan]::FromSeconds(30)).Value[0]
    $null = $client.DeleteMessage($fresh.MessageId, $fresh.PopReceipt)
    Write-Information "deleted $($fresh.MessageId) with the receipt from the receive that produced it"

    # -------------------------------------------------------------------------
    Write-Step '8. Clear the backlog'
    # -------------------------------------------------------------------------
    # Clearing is not the same as deleting the queue: the queue survives, empty.
    $null = $client.ClearMessages()
    Write-Information "peeked after clear : $($client.PeekMessages(5).Value.Count) messages"
}
finally {
    # -------------------------------------------------------------------------
    Write-Step '9. Delete the queue'
    # -------------------------------------------------------------------------
    # Against a real account this is the step that stops the bill, so it runs in
    # a finally block: it happens even when a step above fails.
    if ($null -ne $ctx) {
        Remove-AzStorageQueue -Name $QueueName -Context $ctx -Force -ErrorAction SilentlyContinue
        Write-Information ''
        Write-Information 'Done. Nothing remains in the emulator.'
    }
}
