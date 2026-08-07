using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>Implements <see cref="IArtifactVault"/> over a real Blob container.</summary>
/// <remarks>
/// <para>
/// Given code. This adapter is the Field Station project's blob adapter, carried
/// forward unchanged in shape: the capstone integrates what you already built
/// rather than asking you to rebuild it. The evaluator still grades it on the
/// wire, so a change that breaks the conditional header is caught here rather
/// than three services downstream.
/// </para>
/// <para>
/// Translation includes error classification: a 404 answers "does this exist?"
/// and a 412 answers "was my version still current?", while a 403 answers neither
/// and must keep travelling.
/// </para>
/// </remarks>
/// <param name="container">The container holding this expedition's reports.</param>
public sealed class BlobArtifactVault(BlobContainerClient container) : IArtifactVault
{
    /// <summary>The container holding this expedition's reports.</summary>
    public BlobContainerClient Container { get; } =
        container ?? throw new ArgumentNullException(nameof(container));

    /// <inheritdoc />
    public async Task<ArtifactWriteResult> CreateIfAbsentAsync(
        string name,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var response = await Container.GetBlobClient(name).UploadAsync(
                content,
                new BlobUploadOptions
                {
                    // If-None-Match: * is the whole idempotency guarantee: it
                    // makes "create only if absent" one atomic service decision
                    // instead of a check and a hope.
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                    TransferOptions = TransferOptions,
                },
                cancellationToken).ConfigureAwait(false);

            return new ArtifactWriteResult(WriteOutcome.Written, response.Value.ETag.ToString("H"));
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            // The service reports a lost create as 409 BlobAlreadyExists, and as
            // 412 when the If-None-Match is what failed. Both mean one thing to
            // the caller: somebody else got there first.
            return new ArtifactWriteResult(WriteOutcome.AlreadyExists, null);
        }
    }

    /// <inheritdoc />
    public async Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var response = await Container.GetBlobClient(name)
                .DownloadContentAsync(cancellationToken)
                .ConfigureAwait(false);

            return new ArtifactRevision(
                response.Value.Content.ToArray(),
                // "H" is the HTTP form: quoted, exactly as it must go back out in
                // an If-Match header. ToString() drops the quotes, and an unquoted
                // If-Match is rejected by the service.
                response.GetRawResponse().Headers.ETag?.ToString("H")
                    ?? throw new InvalidOperationException("A 200 response with no ETag is a service defect."),
                response.Value.Details.ContentType ?? "application/octet-stream");
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            // Caught by STATUS, not by exception type, so a 403 from a missing
            // role assignment still propagates instead of reading as "no data".
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListNamesAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in Container
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken)
            .AsPages()
            .ConfigureAwait(false))
        {
            foreach (var item in page.Values)
            {
                yield return item.Name;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteIfExistsAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var response = await Container.GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    internal static StorageTransferOptions TransferOptions => new()
    {
        InitialTransferSize = 4 * 1024 * 1024,
        MaximumTransferSize = 4 * 1024 * 1024,
        MaximumConcurrency = 2,
    };
}

/// <summary>Implements <see cref="IWorkBacklog"/> over a real Storage queue pair.</summary>
/// <remarks>
/// Given code. A Storage queue has no dead-letter queue, so the poison queue is
/// an ordinary second queue this adapter owns, and "quarantine" is two operations
/// that must both happen — copy aside, then delete.
/// </remarks>
/// <param name="work">The dispatch queue.</param>
/// <param name="poison">The queue quarantined messages are moved to.</param>
public sealed class QueueWorkDispatch(QueueClient work, QueueClient poison) : IWorkBacklog
{
    /// <summary>The dispatch queue.</summary>
    public QueueClient Work { get; } = work ?? throw new ArgumentNullException(nameof(work));

    /// <summary>The queue quarantined messages are moved to.</summary>
    public QueueClient Poison { get; } = poison ?? throw new ArgumentNullException(nameof(poison));

    /// <inheritdoc />
    public async Task SendAsync(ArtifactWorkOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        await Work.SendMessageAsync(JournalCodec.EncodeWorkOrder(order), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceivedWork>> ReceiveAsync(
        int maxMessages,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);

        // 32 is the service maximum for one receive. Asking for more is a 400,
        // not a larger batch.
        var response = await Work.ReceiveMessagesAsync(
            Math.Min(maxMessages, 32),
            visibilityTimeout,
            cancellationToken).ConfigureAwait(false);

        return [.. response.Value.Select(message => new ReceivedWork(
            message.MessageId,
            message.PopReceipt,
            message.DequeueCount,
            message.Body.ToString()))];
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ReceivedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            // The pop receipt proves THIS receive, which is what stops a worker
            // whose visibility timeout has expired from deleting a message
            // another worker is now holding.
            await Work.DeleteMessageAsync(work.MessageId, work.PopReceipt, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            // MessageNotFound means the visibility timeout expired and someone
            // else already settled it. The work is done either way.
        }
    }

    /// <inheritdoc />
    public async Task QuarantineAsync(ReceivedWork work, PoisonRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(record);

        var envelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            record.MessageId,
            record.DeliveryCount,
            record.Reason,
            work.Body,
        });

        await Poison.SendMessageAsync(envelope, cancellationToken).ConfigureAwait(false);
        await DeleteAsync(work, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ApproximateDepthAsync(CancellationToken cancellationToken)
    {
        QueueProperties properties = await Work.GetPropertiesAsync(cancellationToken).ConfigureAwait(false);

        // The count includes invisible messages: depth is "work not finished",
        // never "work not started".
        return properties.ApproximateMessagesCount;
    }
}

/// <summary>The table entity the station registry stores.</summary>
/// <remarks>
/// This type exists only at the boundary. Keeping <c>ITableEntity</c> out of
/// <see cref="StationState"/> is what stops the storage model from dictating the
/// domain model, and what lets the same pipeline be graded against an in-memory
/// registry that has no ETags at all.
/// </remarks>
public sealed class StationStateEntity : ITableEntity
{
    /// <summary>The station; the partition every point read is scoped to.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>The observation, or the watermark row.</summary>
    public string RowKey { get; set; } = string.Empty;

    /// <summary>Service-managed write timestamp.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>Service-managed version, used for every conditional write.</summary>
    public ETag ETag { get; set; }

    /// <summary>The phase, stored as its name so the table stays readable.</summary>
    public string Phase { get; set; } = nameof(StationPhase.Pending);

    /// <summary>The last stream sequence number fully handled, on a watermark row.</summary>
    public long LastSequenceNumber { get; set; }

    /// <summary>1 on a journaled observation row; the running total on a watermark row.</summary>
    public int JournaledCount { get; set; }

    /// <summary>The artifact the row refers to.</summary>
    public string ArtifactName { get; set; } = string.Empty;

    /// <summary>The application clock's timestamp, which is reproducible in tests.</summary>
    public string UpdatedUtc { get; set; } = string.Empty;
}

/// <summary>Implements <see cref="IStationRegistry"/> over a real Table.</summary>
/// <remarks>Given code, carried forward from the Field Station project.</remarks>
/// <param name="table">The table holding the station rows.</param>
public sealed class TableStationRegistry(TableClient table) : IStationRegistry
{
    /// <summary>The table holding the station rows.</summary>
    public TableClient Table { get; } = table ?? throw new ArgumentNullException(nameof(table));

    /// <inheritdoc />
    public async Task<StationState?> TryGetAsync(
        string stationId,
        string rowKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        try
        {
            // Both keys, so this is a point read. A filter on one key is a
            // partition scan; a filter on neither is a table scan. They return
            // the same row for a different amount of money on every run.
            var response = await Table
                .GetEntityAsync<StationStateEntity>(stationId, rowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ToState(response.Value);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> TryInsertAsync(StationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            // AddEntity is the conditional insert: the service rejects a second
            // insert of the same partition and row key with 409, which is the
            // signal the whole idempotency design rests on.
            var response = await Table
                .AddEntityAsync(ToEntity(state), cancellationToken)
                .ConfigureAwait(false);

            return response.Headers.ETag?.ToString();
        }
        catch (RequestFailedException error) when (error.Status == 409)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> TryReplaceAsync(
        StationState state,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);

        try
        {
            // A specific ETag is the optimistic concurrency contract. ETag.All
            // here would mean "overwrite whatever is there", which is the lost
            // update this row exists to prevent.
            var response = await Table.UpdateEntityAsync(
                ToEntity(state),
                new ETag(ifMatch),
                TableUpdateMode.Replace,
                cancellationToken).ConfigureAwait(false);

            return response.Headers.ETag?.ToString();
        }
        catch (RequestFailedException error) when (error.Status is 412 or 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StationState> QueryStationAsync(
        string stationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        var query = Table.QueryAsync<StationStateEntity>(
            entity => entity.PartitionKey == stationId,
            cancellationToken: cancellationToken);

        await foreach (var page in query.AsPages().ConfigureAwait(false))
        {
            foreach (var entity in page.Values)
            {
                yield return ToState(entity);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string stationId, string rowKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        var response = await Table
            .DeleteEntityAsync(stationId, rowKey, ETag.All, cancellationToken)
            .ConfigureAwait(false);

        return !response.IsError && response.Status != 404;
    }

    private static StationStateEntity ToEntity(StationState state) => new()
    {
        PartitionKey = state.StationId,
        RowKey = state.RowKey,
        Phase = state.Phase.ToString(),
        LastSequenceNumber = state.LastSequenceNumber,
        JournaledCount = state.JournaledCount,
        ArtifactName = state.ArtifactName,
        UpdatedUtc = ExpeditionNaming.FormatInstant(state.UpdatedUtc),
    };

    private static StationState ToState(StationStateEntity entity) => new()
    {
        StationId = entity.PartitionKey,
        RowKey = entity.RowKey,
        Phase = Enum.TryParse<StationPhase>(entity.Phase, out var phase) ? phase : StationPhase.Pending,
        LastSequenceNumber = entity.LastSequenceNumber,
        JournaledCount = entity.JournaledCount,
        ArtifactName = entity.ArtifactName,
        UpdatedUtc = DateTimeOffset.TryParse(
            entity.UpdatedUtc,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var updated)
            ? updated
            : entity.Timestamp ?? default,
        ETag = entity.ETag.ToString(),
    };
}
