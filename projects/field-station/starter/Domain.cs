namespace LearningAzure.Projects.FieldStation;

/// <summary>Identity of one observation captured by one field station.</summary>
/// <param name="StationId">The station that captured the observation.</param>
/// <param name="ObservationId">The station-local identity of the observation.</param>
/// <remarks>
/// This is the only identity the pipeline has. Every derived name — the blob
/// name, the work-order id, the status row key — is a pure function of it, which
/// is what makes a replayed upload land on the same blob, the same message
/// identity, and the same status row instead of on three new ones.
/// </remarks>
public sealed record ArtifactKey(string StationId, string ObservationId);

/// <summary>An artifact as it exists in the store after a successful write.</summary>
/// <param name="Name">The derived blob name.</param>
/// <param name="ETag">The version the service assigned, in quoted HTTP form.</param>
public sealed record StoredArtifact(string Name, string ETag);

/// <summary>An artifact read back with the version it was read at.</summary>
/// <param name="Content">The stored bytes.</param>
/// <param name="ETag">The version those bytes belong to, in quoted HTTP form.</param>
/// <param name="ContentType">The content type recorded at write time.</param>
public sealed record ArtifactRevision(byte[] Content, string ETag, string ContentType);

/// <summary>What a conditional artifact write did.</summary>
public enum WriteOutcome
{
    /// <summary>The precondition held and the bytes were stored.</summary>
    Written,

    /// <summary>An <c>If-None-Match: *</c> create lost: the artifact is already there.</summary>
    AlreadyExists,

    /// <summary>An <c>If-Match</c> replace lost: someone else wrote since the read.</summary>
    Stale,
}

/// <summary>The result of one conditional artifact write.</summary>
/// <param name="Outcome">What the service did.</param>
/// <param name="ETag">The new version, when one was written.</param>
public sealed record ArtifactWriteResult(WriteOutcome Outcome, string? ETag);

/// <summary>One unit of processing work, as it travels through the queue.</summary>
/// <param name="WorkOrderId">Deterministic identity derived from the artifact key and operation.</param>
/// <param name="StationId">The station the work belongs to.</param>
/// <param name="ObservationId">The observation the work belongs to.</param>
/// <param name="ArtifactName">The blob the work applies to.</param>
/// <param name="Operation">What to do with it.</param>
public sealed record WorkOrder(
    string WorkOrderId,
    string StationId,
    string ObservationId,
    string ArtifactName,
    string Operation)
{
    /// <summary>The artifact identity this order was derived from.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ArtifactKey Key => new(StationId, ObservationId);
}

/// <summary>A message as the queue service hands it back to a consumer.</summary>
/// <param name="MessageId">Service-assigned identity of this queue entry.</param>
/// <param name="PopReceipt">Proof of this particular receive; required to delete.</param>
/// <param name="DequeueCount">How many times this message has been handed out, starting at 1.</param>
/// <param name="Body">The message payload, already decoded to text.</param>
public sealed record ReceivedWork(string MessageId, string PopReceipt, long DequeueCount, string Body);

/// <summary>What the worker decided to do with a received message.</summary>
public enum WorkDisposition
{
    /// <summary>The work is settled. Delete the message so it is never delivered again.</summary>
    Complete,

    /// <summary>Something transient failed. Leave it for the visibility timeout to requeue.</summary>
    Retry,

    /// <summary>It has failed too often, or it can never succeed. Move it aside.</summary>
    Quarantine,
}

/// <summary>Why a message was quarantined, in the operator's language.</summary>
/// <param name="MessageId">The message that was moved aside.</param>
/// <param name="DequeueCount">How many deliveries it took before giving up.</param>
/// <param name="Reason">What the last failure was.</param>
public sealed record PoisonRecord(string MessageId, long DequeueCount, string Reason);

/// <summary>Where one observation has got to in the pipeline.</summary>
public enum ProcessingState
{
    /// <summary>Claimed by a worker; the effect has not been confirmed.</summary>
    Pending,

    /// <summary>An attempt reports that the idempotent effect was applied and confirmed.</summary>
    Processed,

    /// <summary>The work was moved aside and needs a human.</summary>
    Quarantined,
}

/// <summary>
/// One row of the station status index: the observation rows and the per-station
/// summary row share this shape.
/// </summary>
/// <remarks>
/// This is an application type, not a table entity. The Azure adapter maps it on
/// to <c>ITableEntity</c>; nothing above the adapter knows that
/// <see cref="StationId"/> is a PartitionKey.
/// </remarks>
public sealed class StationStatus
{
    /// <summary>The station this row belongs to; the index partitions by it.</summary>
    public required string StationId { get; init; }

    /// <summary>The row identity within the station: an observation id, or the summary row.</summary>
    public required string RowKey { get; init; }

    /// <summary>Where the observation has got to.</summary>
    public ProcessingState State { get; set; }

    /// <summary>On an observation row, 1 once processed; on the summary row, the station total.</summary>
    public int ProcessedCount { get; set; }

    /// <summary>The blob the row refers to; empty on the summary row.</summary>
    public string ArtifactName { get; set; } = string.Empty;

    /// <summary>When the row was last written, from the injected clock.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>The version this row was read at; empty for a row that has never been stored.</summary>
    public string ETag { get; set; } = string.Empty;
}

/// <summary>The artifact operations the field station needs, and no others.</summary>
/// <remarks>
/// The port is deliberately narrow. It exposes conditional writes because the
/// pipeline's correctness depends on them, and it exposes no SDK type at all, so
/// the whole flow can be exercised in memory and against Azurite without either
/// version of the test knowing which one it is driving.
/// </remarks>
public interface IArtifactStore
{
    /// <summary>Streams <paramref name="content"/> in only if the artifact does not exist yet.</summary>
    /// <param name="name">The derived artifact name.</param>
    /// <param name="content">The bytes, streamed rather than buffered.</param>
    /// <param name="contentType">The content type to record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="WriteOutcome.Written"/>, or <see cref="WriteOutcome.AlreadyExists"/>.</returns>
    Task<ArtifactWriteResult> CreateIfAbsentAsync(
        string name,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>Replaces the artifact only if its stored version is still <paramref name="ifMatch"/>.</summary>
    /// <param name="name">The derived artifact name.</param>
    /// <param name="content">The bytes, streamed rather than buffered.</param>
    /// <param name="contentType">The content type to record.</param>
    /// <param name="ifMatch">The version the new content was computed from.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="WriteOutcome.Written"/>, or <see cref="WriteOutcome.Stale"/>.</returns>
    Task<ArtifactWriteResult> ReplaceIfUnchangedAsync(
        string name,
        Stream content,
        string contentType,
        string ifMatch,
        CancellationToken cancellationToken);

    /// <summary>Reads one artifact, or <c>null</c> when it does not exist.</summary>
    /// <param name="name">The derived artifact name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes with the version they belong to, or <c>null</c>.</returns>
    Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists artifact names under <paramref name="prefix"/>, page by page.</summary>
    /// <param name="prefix">The virtual-directory prefix to list.</param>
    /// <param name="cancellationToken">Cancels the listing between pages.</param>
    /// <returns>Every matching name.</returns>
    IAsyncEnumerable<string> ListNamesAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Deletes one artifact if it is there.</summary>
    /// <param name="name">The derived artifact name.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><c>true</c> when something was deleted.</returns>
    Task<bool> DeleteIfExistsAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The work-dispatch operations the field station needs, and no others.</summary>
public interface IWorkBacklog
{
    /// <summary>Enqueues one work order.</summary>
    /// <param name="order">The order to dispatch.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the service accepted the message.</returns>
    Task SendAsync(WorkOrder order, CancellationToken cancellationToken);

    /// <summary>Receives up to <paramref name="maxMessages"/> messages and hides them.</summary>
    /// <param name="maxMessages">Batch size; the service caps it at 32.</param>
    /// <param name="visibilityTimeout">How long the batch stays invisible.</param>
    /// <param name="cancellationToken">Cancels the receive.</param>
    /// <returns>The received messages, possibly none.</returns>
    Task<IReadOnlyList<ReceivedWork>> ReceiveAsync(
        int maxMessages,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken);

    /// <summary>Deletes a message the worker has settled.</summary>
    /// <param name="work">The message, including the pop receipt that proves this receive.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when the message is gone.</returns>
    Task DeleteAsync(ReceivedWork work, CancellationToken cancellationToken);

    /// <summary>Moves a message to the poison queue and removes it from the work queue.</summary>
    /// <param name="work">The message being quarantined.</param>
    /// <param name="record">Why it was quarantined.</param>
    /// <param name="cancellationToken">Cancels the quarantine.</param>
    /// <returns>A task that completes when the message has been moved.</returns>
    Task QuarantineAsync(ReceivedWork work, PoisonRecord record, CancellationToken cancellationToken);

    /// <summary>The service's approximate message count, including invisible messages.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The approximate depth.</returns>
    Task<int> ApproximateDepthAsync(CancellationToken cancellationToken);
}

/// <summary>The station status operations the field station needs, and no others.</summary>
/// <remarks>
/// <see cref="TryInsertAsync"/> is the pipeline's idempotency gate: an insert
/// that loses to an existing row is how a duplicate delivery is detected, so it
/// must report the loss rather than overwrite.
/// </remarks>
public interface IStationStatusIndex
{
    /// <summary>Point-reads one row, or <c>null</c> when it does not exist.</summary>
    /// <param name="stationId">The partition.</param>
    /// <param name="rowKey">The row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row with its version, or <c>null</c>.</returns>
    Task<StationStatus?> TryGetAsync(string stationId, string rowKey, CancellationToken cancellationToken);

    /// <summary>Inserts a row only if that partition and row key are still free.</summary>
    /// <param name="status">The row to insert.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The new version, or <c>null</c> when the row already existed.</returns>
    Task<string?> TryInsertAsync(StationStatus status, CancellationToken cancellationToken);

    /// <summary>Replaces a row only if its stored version is still <paramref name="ifMatch"/>.</summary>
    /// <param name="status">The row to store.</param>
    /// <param name="ifMatch">The version the change was computed from.</param>
    /// <param name="cancellationToken">Cancels the replace.</param>
    /// <returns>The new version, or <c>null</c> when the stored version had moved on.</returns>
    Task<string?> TryReplaceAsync(StationStatus status, string ifMatch, CancellationToken cancellationToken);

    /// <summary>Reads every row of one station as a single-partition query.</summary>
    /// <param name="stationId">The partition to read.</param>
    /// <param name="cancellationToken">Cancels the query between pages.</param>
    /// <returns>Every row in the partition.</returns>
    IAsyncEnumerable<StationStatus> QueryStationAsync(string stationId, CancellationToken cancellationToken);

    /// <summary>Deletes one row.</summary>
    /// <param name="stationId">The partition.</param>
    /// <param name="rowKey">The row.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><c>true</c> when a row was deleted.</returns>
    Task<bool> DeleteAsync(string stationId, string rowKey, CancellationToken cancellationToken);
}
