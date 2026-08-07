namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>Identity of one observation captured by one station.</summary>
/// <param name="StationId">The station that captured it.</param>
/// <param name="ObservationId">The station-local identity of the observation.</param>
/// <remarks>
/// This is the only identity the whole journal has. The event partition key, the
/// artifact blob name, the work-order id, the station row key, and the Cosmos
/// item id are all pure functions of it, so one replayed reading collides with
/// itself in five places instead of becoming five new records.
/// </remarks>
public sealed record ObservationKey(string StationId, string ObservationId);

/// <summary>One sensor reading as the field laptop hands it over.</summary>
/// <param name="StationId">The station that captured it.</param>
/// <param name="ObservationId">The station-local identity of the observation.</param>
/// <param name="Celsius">The measured temperature.</param>
/// <param name="ObservedUtc">When the station measured it, from the station's clock.</param>
public sealed record TelemetryReading(
    string StationId,
    string ObservationId,
    double Celsius,
    DateTimeOffset ObservedUtc)
{
    /// <summary>The observation identity this reading belongs to.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ObservationKey Key => new(StationId, ObservationId);
}

/// <summary>One reading as the stream hands it back to a consumer.</summary>
/// <param name="PartitionId">The stream partition it was read from.</param>
/// <param name="SequenceNumber">The partition-scoped, monotonic position of the event.</param>
/// <param name="Offset">The opaque partition-scoped offset the service assigned.</param>
/// <param name="PartitionKey">The producer-chosen key that decided the partition.</param>
/// <param name="Reading">The decoded reading.</param>
/// <remarks>
/// The sequence number is meaningful only inside its own partition. Comparing one
/// across partitions is the mistake that makes a "latest reading" projection
/// randomly wrong, which is why every consumer state in this capstone is keyed by
/// partition as well as by station.
/// </remarks>
public sealed record StreamEvent(
    string PartitionId,
    long SequenceNumber,
    string Offset,
    string PartitionKey,
    TelemetryReading Reading);

/// <summary>What one publish pass sent.</summary>
/// <param name="BatchCount">How many batches left the producer.</param>
/// <param name="ReadingCount">How many readings those batches carried.</param>
/// <param name="ByPartitionKey">Readings per partition key, in send order.</param>
public sealed record PublishReceipt(
    int BatchCount,
    int ReadingCount,
    IReadOnlyDictionary<string, int> ByPartitionKey);

/// <summary>A claim on one stream partition, held by one processor.</summary>
/// <param name="PartitionId">The partition the claim covers.</param>
/// <param name="OwnerId">The processor instance holding it.</param>
/// <param name="ETag">The version the claim was acquired at.</param>
/// <remarks>
/// Ownership is a lease, not a lock: it expires, and the holder is expected to
/// discover that by losing a conditional write rather than by being told.
/// </remarks>
public sealed record PartitionOwnership(string PartitionId, string OwnerId, string ETag);

/// <summary>The furthest position a consumer has successfully handled on one partition.</summary>
/// <param name="PartitionId">The partition the position belongs to.</param>
/// <param name="SequenceNumber">The last sequence number that was fully handled.</param>
/// <param name="Offset">The offset that sequence number sits at.</param>
public sealed record Checkpoint(string PartitionId, long SequenceNumber, string Offset);

/// <summary>One unit of artifact work, as it travels through the queue.</summary>
/// <param name="WorkOrderId">Deterministic identity derived from the observation and operation.</param>
/// <param name="StationId">The station the work belongs to.</param>
/// <param name="ObservationId">The observation the work belongs to.</param>
/// <param name="ArtifactName">The blob the work will write.</param>
/// <param name="Operation">What to do.</param>
public sealed record ArtifactWorkOrder(
    string WorkOrderId,
    string StationId,
    string ObservationId,
    string ArtifactName,
    string Operation)
{
    /// <summary>The observation identity this order was derived from.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ObservationKey Key => new(StationId, ObservationId);
}

/// <summary>A message as the queue service hands it back to a consumer.</summary>
/// <param name="MessageId">Service-assigned identity of this queue entry.</param>
/// <param name="PopReceipt">Proof of this particular receive; required to delete.</param>
/// <param name="DeliveryCount">How many times this message has been handed out, starting at 1.</param>
/// <param name="Body">The message payload, already decoded to text.</param>
public sealed record ReceivedWork(string MessageId, string PopReceipt, long DeliveryCount, string Body);

/// <summary>Why a message was moved aside, in the operator's language.</summary>
/// <param name="MessageId">The message that was quarantined.</param>
/// <param name="DeliveryCount">How many deliveries it took before giving up.</param>
/// <param name="Reason">What the last failure was.</param>
public sealed record PoisonRecord(string MessageId, long DeliveryCount, string Reason);

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

/// <summary>What one conditional write did.</summary>
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

/// <summary>An artifact read back with the version it was read at.</summary>
/// <param name="Content">The stored bytes.</param>
/// <param name="ETag">The version those bytes belong to, in quoted HTTP form.</param>
/// <param name="ContentType">The content type recorded at write time.</param>
public sealed record ArtifactRevision(byte[] Content, string ETag, string ContentType);

/// <summary>Where one station has got to in the pipeline.</summary>
public enum StationPhase
{
    /// <summary>Claimed by a processor; the reading has not been confirmed.</summary>
    Pending,

    /// <summary>The reading has been handled and projected exactly once.</summary>
    Journaled,

    /// <summary>The work was moved aside and needs a human.</summary>
    Quarantined,
}

/// <summary>
/// One row of the station registry: the per-observation rows and the per-station
/// watermark row share this shape.
/// </summary>
/// <remarks>
/// This is an application type, not a table entity. The Azure adapter maps it on
/// to <c>ITableEntity</c>; nothing above the adapter knows that
/// <see cref="StationId"/> is a PartitionKey.
/// </remarks>
public sealed class StationState
{
    /// <summary>The station this row belongs to; the registry partitions by it.</summary>
    public required string StationId { get; init; }

    /// <summary>The row identity within the station: an observation id, or a watermark row.</summary>
    public required string RowKey { get; init; }

    /// <summary>Where the observation has got to.</summary>
    public StationPhase Phase { get; set; }

    /// <summary>On a watermark row, the last stream sequence number fully handled.</summary>
    public long LastSequenceNumber { get; set; }

    /// <summary>On an observation row, 1 once journaled; on a watermark row, the station total.</summary>
    public int JournaledCount { get; set; }

    /// <summary>The blob the row refers to; empty on a watermark row.</summary>
    public string ArtifactName { get; set; } = string.Empty;

    /// <summary>When the row was last written, from the injected clock.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>The version this row was read at; empty for a row that has never been stored.</summary>
    public string ETag { get; set; } = string.Empty;
}

/// <summary>One document of the queryable journal.</summary>
/// <param name="Id">The item id, derived from the observation.</param>
/// <param name="StationId">The Cosmos partition key value.</param>
/// <param name="ObservationId">The observation this entry projects.</param>
/// <param name="PartitionId">The stream partition the reading arrived on.</param>
/// <param name="SequenceNumber">The stream position the entry was projected from.</param>
/// <param name="Celsius">The measured temperature.</param>
/// <param name="ArtifactName">The blob holding the preserved report.</param>
/// <param name="ObservedUtc">When the station measured it.</param>
/// <param name="ETag">The version the entry was read at; empty for one never stored.</param>
public sealed record JournalEntry(
    string Id,
    string StationId,
    string ObservationId,
    string PartitionId,
    long SequenceNumber,
    double Celsius,
    string ArtifactName,
    DateTimeOffset ObservedUtc,
    string ETag = "");

/// <summary>One page of a journal query, with what the page cost.</summary>
/// <param name="Entries">The entries on this page.</param>
/// <param name="ContinuationToken">The token for the next page, or <c>null</c> at the end.</param>
/// <param name="RequestCharge">Request units this page consumed.</param>
/// <remarks>
/// A short page is not the end of a query. Cosmos may cut a page at a size or
/// time budget and still have more to give, and the only end-of-results signal is
/// a null continuation token.
/// </remarks>
public sealed record JournalPage(
    IReadOnlyList<JournalEntry> Entries,
    string? ContinuationToken,
    double RequestCharge);

/// <summary>What one projection write did.</summary>
public enum ProjectionOutcome
{
    /// <summary>The entry was stored.</summary>
    Written,

    /// <summary>The stored entry already carried this position or a later one.</summary>
    Superseded,

    /// <summary>The caller's version had moved on; re-read and decide again.</summary>
    Stale,
}

/// <summary>The result of one projection write, with what it cost.</summary>
/// <param name="Outcome">What the service did.</param>
/// <param name="ETag">The new version, when one was written.</param>
/// <param name="RequestCharge">Request units the attempt consumed, charged even when it lost.</param>
public sealed record ProjectionResult(ProjectionOutcome Outcome, string? ETag, double RequestCharge);

/// <summary>
/// A request the service refused for rate limiting, carrying how long it asked
/// the caller to wait.
/// </summary>
/// <remarks>
/// This is a domain exception on purpose. A throttle is a normal operating
/// condition of a provisioned-throughput service, not a defect, and the retry
/// decision belongs above the adapter — so the adapter translates the service's
/// 429 into this rather than leaking <c>CosmosException</c> upwards.
/// </remarks>
public sealed class ThrottledException : Exception
{
    /// <summary>Creates a throttle carrying the service's requested delay and charge.</summary>
    /// <param name="retryAfter">How long the service asked the caller to wait.</param>
    /// <param name="requestCharge">Request units the refused attempt still consumed.</param>
    public ThrottledException(TimeSpan retryAfter, double requestCharge)
        : base($"The request was rate limited; retry after {retryAfter.TotalMilliseconds:0} ms.")
    {
        RetryAfter = retryAfter;
        RequestCharge = requestCharge;
    }

    /// <summary>Creates a throttle with a message, the service's delay, and its charge.</summary>
    /// <param name="message">The message.</param>
    /// <param name="retryAfter">How long the service asked the caller to wait.</param>
    /// <param name="requestCharge">Request units the refused attempt still consumed.</param>
    public ThrottledException(string message, TimeSpan retryAfter, double requestCharge)
        : base(message)
    {
        RetryAfter = retryAfter;
        RequestCharge = requestCharge;
    }

    /// <summary>Creates a throttle with no service-supplied delay.</summary>
    public ThrottledException()
        : this(TimeSpan.Zero, 0)
    {
    }

    /// <summary>Creates a throttle with a message and no service-supplied delay.</summary>
    /// <param name="message">The message.</param>
    public ThrottledException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a throttle with a message and an inner cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ThrottledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>How long the service asked the caller to wait.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>Request units the refused attempt still consumed.</summary>
    public double RequestCharge { get; }
}

/// <summary>The telemetry stream operations the journal needs, and no others.</summary>
public interface ITelemetryFeed
{
    /// <summary>Publishes readings as keyed batches.</summary>
    /// <param name="readings">The readings to publish.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>What was sent, by partition key.</returns>
    Task<PublishReceipt> PublishAsync(
        IReadOnlyList<TelemetryReading> readings,
        CancellationToken cancellationToken);

    /// <summary>The partition ids the stream currently has.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Every partition id.</returns>
    Task<IReadOnlyList<string>> GetPartitionIdsAsync(CancellationToken cancellationToken);

    /// <summary>Reads one partition from just after <paramref name="afterSequenceNumber"/>.</summary>
    /// <param name="partitionId">The partition to read.</param>
    /// <param name="afterSequenceNumber">The last position already handled, or -1 for the start.</param>
    /// <param name="cancellationToken">Cancels the read between events.</param>
    /// <returns>Events in partition order.</returns>
    IAsyncEnumerable<StreamEvent> ReadPartitionAsync(
        string partitionId,
        long afterSequenceNumber,
        CancellationToken cancellationToken);
}

/// <summary>The checkpoint and ownership operations the journal needs, and no others.</summary>
/// <remarks>
/// Both live in Blob Storage in the reference implementation, and both are
/// conditional writes: ownership is an <c>If-Match</c> on a lease blob, and a
/// checkpoint written by a processor that no longer owns the partition is
/// rejected rather than accepted.
/// </remarks>
public interface ICheckpointStore
{
    /// <summary>Claims <paramref name="partitionId"/> for <paramref name="ownerId"/> if it is free or expired.</summary>
    /// <param name="partitionId">The partition to claim.</param>
    /// <param name="ownerId">The processor instance claiming it.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>The claim, or <c>null</c> when another owner holds it.</returns>
    Task<PartitionOwnership?> TryClaimAsync(string partitionId, string ownerId, CancellationToken cancellationToken);

    /// <summary>Reads the last checkpoint of one partition.</summary>
    /// <param name="partitionId">The partition.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint, or <c>null</c> when the partition has never been checkpointed.</returns>
    Task<Checkpoint?> TryReadCheckpointAsync(string partitionId, CancellationToken cancellationToken);

    /// <summary>Writes a checkpoint, but only while <paramref name="ownership"/> is still held.</summary>
    /// <param name="checkpoint">The position to record.</param>
    /// <param name="ownership">The claim the caller believes it holds.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The renewed claim, or <c>null</c> when ownership had already moved on.</returns>
    Task<PartitionOwnership?> TryWriteCheckpointAsync(
        Checkpoint checkpoint,
        PartitionOwnership ownership,
        CancellationToken cancellationToken);

    /// <summary>Removes every ownership and checkpoint record this run created.</summary>
    /// <param name="cancellationToken">Cancels the teardown.</param>
    /// <returns>How many records were removed.</returns>
    Task<int> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>The artifact operations the journal needs, and no others.</summary>
public interface IArtifactVault
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

/// <summary>The work-dispatch operations the journal needs, and no others.</summary>
public interface IWorkBacklog
{
    /// <summary>Enqueues one work order.</summary>
    /// <param name="order">The order to dispatch.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the service accepted the message.</returns>
    Task SendAsync(ArtifactWorkOrder order, CancellationToken cancellationToken);

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

/// <summary>The station registry operations the journal needs, and no others.</summary>
/// <remarks>
/// <see cref="TryInsertAsync"/> is the pipeline's idempotency gate: an insert that
/// loses to an existing row is how a duplicate delivery is detected, so it must
/// report the loss rather than overwrite.
/// </remarks>
public interface IStationRegistry
{
    /// <summary>Point-reads one row, or <c>null</c> when it does not exist.</summary>
    /// <param name="stationId">The partition.</param>
    /// <param name="rowKey">The row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row with its version, or <c>null</c>.</returns>
    Task<StationState?> TryGetAsync(string stationId, string rowKey, CancellationToken cancellationToken);

    /// <summary>Inserts a row only if that partition and row key are still free.</summary>
    /// <param name="state">The row to insert.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The new version, or <c>null</c> when the row already existed.</returns>
    Task<string?> TryInsertAsync(StationState state, CancellationToken cancellationToken);

    /// <summary>Replaces a row only if its stored version is still <paramref name="ifMatch"/>.</summary>
    /// <param name="state">The row to store.</param>
    /// <param name="ifMatch">The version the change was computed from.</param>
    /// <param name="cancellationToken">Cancels the replace.</param>
    /// <returns>The new version, or <c>null</c> when the stored version had moved on.</returns>
    Task<string?> TryReplaceAsync(StationState state, string ifMatch, CancellationToken cancellationToken);

    /// <summary>Reads every row of one station as a single-partition query.</summary>
    /// <param name="stationId">The partition to read.</param>
    /// <param name="cancellationToken">Cancels the query between pages.</param>
    /// <returns>Every row in the partition.</returns>
    IAsyncEnumerable<StationState> QueryStationAsync(string stationId, CancellationToken cancellationToken);

    /// <summary>Deletes one row.</summary>
    /// <param name="stationId">The partition.</param>
    /// <param name="rowKey">The row.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><c>true</c> when a row was deleted.</returns>
    Task<bool> DeleteAsync(string stationId, string rowKey, CancellationToken cancellationToken);
}

/// <summary>The queryable-journal operations the capstone needs, and no others.</summary>
/// <remarks>
/// Every operation names the station, because the station is the partition key.
/// An operation that cannot name it would be a cross-partition query, which costs
/// request units proportional to the number of physical partitions rather than to
/// the number of matching documents.
/// </remarks>
public interface IJournalProjection
{
    /// <summary>Writes an entry, unless the stored one already carries this position or later.</summary>
    /// <param name="entry">The entry to store.</param>
    /// <param name="ifMatch">The version the decision was computed from, or <c>null</c> to insert.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened and what it cost.</returns>
    /// <exception cref="ThrottledException">The service rate limited the request.</exception>
    Task<ProjectionResult> WriteAsync(JournalEntry entry, string? ifMatch, CancellationToken cancellationToken);

    /// <summary>Point-reads one entry by its partition key and id.</summary>
    /// <param name="stationId">The partition key.</param>
    /// <param name="id">The item id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entry, or <c>null</c> when it does not exist.</returns>
    /// <exception cref="ThrottledException">The service rate limited the request.</exception>
    Task<JournalEntry?> TryReadAsync(string stationId, string id, CancellationToken cancellationToken);

    /// <summary>Reads one page of a single-partition query.</summary>
    /// <param name="stationId">The partition key the query is scoped to.</param>
    /// <param name="pageSize">How many entries the page is asked for.</param>
    /// <param name="continuationToken">The token from the previous page, or <c>null</c> to start.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The page, its continuation token, and its charge.</returns>
    /// <exception cref="ThrottledException">The service rate limited the request.</exception>
    Task<JournalPage> QueryStationAsync(
        string stationId,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    /// <summary>Deletes one entry.</summary>
    /// <param name="stationId">The partition key.</param>
    /// <param name="id">The item id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><c>true</c> when an entry was deleted.</returns>
    /// <exception cref="ThrottledException">The service rate limited the request.</exception>
    Task<bool> DeleteAsync(string stationId, string id, CancellationToken cancellationToken);
}
