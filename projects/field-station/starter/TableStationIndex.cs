using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;

namespace LearningAzure.Projects.FieldStation;

/// <summary>The table entity the status index stores.</summary>
/// <remarks>
/// This type exists only at the boundary. Keeping <c>ITableEntity</c> out of
/// <see cref="StationStatus"/> is what stops the storage model from dictating the
/// domain model — and, more practically, what lets the same pipeline be graded
/// against an in-memory index that has no ETags at all.
/// </remarks>
public sealed class StationStatusEntity : ITableEntity
{
    /// <summary>The station; the partition every point read is scoped to.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>The observation, or the summary row.</summary>
    public string RowKey { get; set; } = string.Empty;

    /// <summary>Service-managed write timestamp.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>Service-managed version, used for every conditional write.</summary>
    public ETag ETag { get; set; }

    /// <summary>The processing state, stored as its name so the table stays readable.</summary>
    public string State { get; set; } = nameof(ProcessingState.Pending);

    /// <summary>1 on a processed observation row; the running total on a summary row.</summary>
    public int ProcessedCount { get; set; }

    /// <summary>The artifact the row refers to.</summary>
    public string ArtifactName { get; set; } = string.Empty;

    /// <summary>The application clock's timestamp, which is reproducible in tests.</summary>
    public string UpdatedUtc { get; set; } = string.Empty;
}

/// <summary>Implements <see cref="IStationStatusIndex"/> over a real Table.</summary>
/// <param name="table">The table holding the station status rows.</param>
public sealed class TableStationIndex(TableClient table) : IStationStatusIndex
{
    /// <summary>The table holding the station status rows.</summary>
    public TableClient Table { get; } = table ?? throw new ArgumentNullException(nameof(table));

    /// <inheritdoc />
    public async Task<StationStatus?> TryGetAsync(
        string stationId,
        string rowKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        // GAP 15 — Both keys, so this is a point read.
        //
        // A filtered query on one key is a partition scan; a filtered query on
        // neither is a table scan. They return the same row and cost a different
        // amount of money every time the pipeline runs.
        //
        // Use GetEntityAsync<StationStatusEntity>(partitionKey, rowKey, ...) and
        // map the entity with ToStatus. A 404 is "no such row", so return null
        // rather than throwing; catch by status so a 403 still propagates.
        throw new NotImplementedException(
            "GAP 15: point-read one status row. See "
            + "projects/field-station/README.md#milestone-4-the-ledger.");
    }

    /// <inheritdoc />
    public async Task<string?> TryInsertAsync(StationStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);

        // AddEntityAsync is the conditional insert: the service rejects a second
        // insert of the same partition and row key with 409, which is the signal
        // the whole idempotency design rests on. Report that as null instead of
        // letting it throw, and return the new ETag on success.
        throw new NotImplementedException(
            "GAP 15: insert a status row only when the keys are free. See "
            + "projects/field-station/README.md#milestone-4-the-ledger.");
    }

    /// <inheritdoc />
    public async Task<string?> TryReplaceAsync(
        StationStatus status,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);

        // UpdateEntityAsync with TableUpdateMode.Replace and a SPECIFIC ETag is
        // the optimistic concurrency contract. ETag.All here would mean
        // "overwrite whatever is there", which is the lost update this row exists
        // to prevent.
        //
        // 412 is a lost race and 404 is a row deleted underneath the caller. Both
        // mean "your version is not current", which is the answer the caller
        // asked for, so return null for either.
        throw new NotImplementedException(
            "GAP 15: replace a status row only when it is unchanged. See "
            + "projects/field-station/README.md#milestone-4-the-ledger.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StationStatus> QueryStationAsync(
        string stationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        // A single-partition query: the server filters, and the client never
        // pages through rows belonging to other stations.
        var query = Table.QueryAsync<StationStatusEntity>(
            entity => entity.PartitionKey == stationId,
            cancellationToken: cancellationToken);

        await foreach (var page in query.AsPages().ConfigureAwait(false))
        {
            foreach (var entity in page.Values)
            {
                yield return ToStatus(entity);
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

    private static StationStatusEntity ToEntity(StationStatus status) => new()
    {
        PartitionKey = status.StationId,
        RowKey = status.RowKey,
        State = status.State.ToString(),
        ProcessedCount = status.ProcessedCount,
        ArtifactName = status.ArtifactName,
        UpdatedUtc = StationNaming.FormatInstant(status.UpdatedUtc),
    };

    private static StationStatus ToStatus(StationStatusEntity entity) => new()
    {
        StationId = entity.PartitionKey,
        RowKey = entity.RowKey,
        State = Enum.TryParse<ProcessingState>(entity.State, out var state) ? state : ProcessingState.Pending,
        ProcessedCount = entity.ProcessedCount,
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
