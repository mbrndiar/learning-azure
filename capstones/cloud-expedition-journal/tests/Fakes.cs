using System.Globalization;
using System.Runtime.CompilerServices;

namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>An in-memory artifact vault with real conditional-write semantics.</summary>
/// <remarks>
/// The fake is deliberately strict where the service is strict: a create only
/// lands when the name is free, and the stored version changes on every write. A
/// permissive fake would let a last-write-wins implementation pass, which is one
/// of the faults this capstone exists to prevent.
/// </remarks>
internal sealed class InMemoryVault : IArtifactVault
{
    private readonly Dictionary<string, Entry> _artifacts = new(StringComparer.Ordinal);
    private int _version;

    /// <summary>Every name a write was attempted for, in order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Every name a delete was attempted for, in order.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>How many artifacts are stored.</summary>
    public int Count => _artifacts.Count;

    /// <summary>Every stored name, ordered.</summary>
    public IReadOnlyList<string> Names => [.. _artifacts.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>The bytes currently stored under <paramref name="name"/>.</summary>
    public byte[] this[string name] => _artifacts[name].Content;

    /// <summary>Writes an artifact without any precondition, to set up a test.</summary>
    /// <param name="name">The artifact name.</param>
    /// <param name="content">The stored body.</param>
    public void Seed(string name, string content) =>
        _artifacts[name] = new Entry(System.Text.Encoding.UTF8.GetBytes(content), NextETag(), "application/json");

    public async Task<ArtifactWriteResult> CreateIfAbsentAsync(
        string name,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add(name);

        if (_artifacts.ContainsKey(name))
        {
            return new ArtifactWriteResult(WriteOutcome.AlreadyExists, null);
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var entry = new Entry(buffer.ToArray(), NextETag(), contentType);
        _artifacts[name] = entry;
        return new ArtifactWriteResult(WriteOutcome.Written, entry.ETag);
    }

    public Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_artifacts.TryGetValue(name, out var entry)
            ? new ArtifactRevision([.. entry.Content], entry.ETag, entry.ContentType)
            : null);
    }

    public async IAsyncEnumerable<string> ListNamesAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var name in _artifacts.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return name;
            await Task.Yield();
        }
    }

    public Task<bool> DeleteIfExistsAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deletes.Add(name);
        return Task.FromResult(_artifacts.Remove(name));
    }

    private string NextETag() => $"\"0x{(++_version).ToString("X8", CultureInfo.InvariantCulture)}\"";

    private sealed record Entry(byte[] Content, string ETag, string ContentType);
}

/// <summary>An in-memory checkpoint store where the ETag really is the lease.</summary>
/// <remarks>
/// Claiming, taking over, and checkpointing all go through the same version
/// check the blob adapter puts on the wire, so a processor that keeps a stale
/// ownership handle is refused here exactly as it would be by the service.
/// </remarks>
internal sealed class InMemoryCheckpointStore(TimeProvider clock, TimeSpan leaseDuration) : ICheckpointStore
{
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private int _version;

    /// <summary>Checkpoints that were refused because the lease had moved on.</summary>
    public int RejectedCheckpoints { get; private set; }

    /// <summary>Checkpoints that landed, in order.</summary>
    public List<Checkpoint> Written { get; } = [];

    /// <summary>The owner currently holding a partition, or <c>null</c>.</summary>
    /// <param name="partitionId">The partition.</param>
    /// <returns>The owner id.</returns>
    public string? OwnerOf(string partitionId) =>
        _leases.TryGetValue(partitionId, out var lease) ? lease.OwnerId : null;

    /// <summary>Hands a partition to another host, as a rebalance would.</summary>
    /// <param name="partitionId">The partition to move.</param>
    /// <param name="ownerId">The new owner.</param>
    public void StealOwnership(string partitionId, string ownerId)
    {
        if (_leases.TryGetValue(partitionId, out var lease))
        {
            _leases[partitionId] = lease with
            {
                OwnerId = ownerId,
                ETag = NextETag(),
                ClaimedAt = clock.GetUtcNow(),
            };
        }
    }

    public Task<PartitionOwnership?> TryClaimAsync(
        string partitionId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.GetUtcNow();
        if (_leases.TryGetValue(partitionId, out var lease))
        {
            var expired = now - lease.ClaimedAt >= leaseDuration;
            if (!expired && !string.Equals(lease.OwnerId, ownerId, StringComparison.Ordinal))
            {
                return Task.FromResult<PartitionOwnership?>(null);
            }

            var renewed = lease with { OwnerId = ownerId, ETag = NextETag(), ClaimedAt = now };
            _leases[partitionId] = renewed;
            return Task.FromResult<PartitionOwnership?>(
                new PartitionOwnership(partitionId, ownerId, renewed.ETag));
        }

        var created = new Lease(ownerId, NextETag(), now, null);
        _leases[partitionId] = created;
        return Task.FromResult<PartitionOwnership?>(new PartitionOwnership(partitionId, ownerId, created.ETag));
    }

    public Task<Checkpoint?> TryReadCheckpointAsync(string partitionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_leases.TryGetValue(partitionId, out var lease) ? lease.Checkpoint : null);
    }

    public Task<PartitionOwnership?> TryWriteCheckpointAsync(
        Checkpoint checkpoint,
        PartitionOwnership ownership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(ownership);

        if (!_leases.TryGetValue(checkpoint.PartitionId, out var lease) || lease.ETag != ownership.ETag)
        {
            RejectedCheckpoints++;
            return Task.FromResult<PartitionOwnership?>(null);
        }

        var renewed = lease with
        {
            ETag = NextETag(),
            ClaimedAt = clock.GetUtcNow(),
            Checkpoint = checkpoint,
        };

        _leases[checkpoint.PartitionId] = renewed;
        Written.Add(checkpoint);
        return Task.FromResult<PartitionOwnership?>(ownership with { ETag = renewed.ETag });
    }

    public Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var removed = _leases.Count;
        _leases.Clear();
        return Task.FromResult(removed);
    }

    private string NextETag() => $"\"0xLEASE{(++_version).ToString("X4", CultureInfo.InvariantCulture)}\"";

    private sealed record Lease(string OwnerId, string ETag, DateTimeOffset ClaimedAt, Checkpoint? Checkpoint);
}

/// <summary>An in-memory backlog that redelivers exactly as a Storage queue does.</summary>
/// <remarks>
/// Visibility is modelled by receive rather than by wall clock: a message that is
/// not deleted comes back on the next receive with its delivery count increased.
/// That keeps redelivery tests instant and deterministic, which is the only way
/// an evaluator can assert on it at all.
/// </remarks>
internal sealed class InMemoryBacklog : IWorkBacklog
{
    private readonly List<Message> _messages = [];
    private int _nextId;
    private int _nextReceipt;

    /// <summary>Messages moved aside, with the reason.</summary>
    public List<(PoisonRecord Record, string Body)> Poison { get; } = [];

    /// <summary>Bodies that were sent, in order.</summary>
    public List<string> Sent { get; } = [];

    /// <summary>Delete attempts rejected because the pop receipt was stale.</summary>
    public int RejectedDeletes { get; private set; }

    /// <summary>Messages still on the backlog.</summary>
    public int Depth => _messages.Count;

    /// <summary>Puts a raw body on the backlog, as a misbehaving producer would.</summary>
    /// <param name="body">The raw body.</param>
    public void SendRaw(string body)
    {
        Sent.Add(body);
        _messages.Add(new Message($"m{++_nextId}", body));
    }

    public Task SendAsync(ArtifactWorkOrder order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendRaw(JournalCodec.EncodeWorkOrder(order));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReceivedWork>> ReceiveAsync(
        int maxMessages,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var batch = new List<ReceivedWork>();
        foreach (var message in _messages.Take(maxMessages).ToList())
        {
            message.DeliveryCount++;
            message.PopReceipt = $"r{++_nextReceipt}";
            batch.Add(new ReceivedWork(message.Id, message.PopReceipt, message.DeliveryCount, message.Body));
        }

        return Task.FromResult<IReadOnlyList<ReceivedWork>>(batch);
    }

    public Task DeleteAsync(ReceivedWork work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(work);

        var message = _messages.SingleOrDefault(candidate => candidate.Id == work.MessageId);
        if (message is null)
        {
            return Task.CompletedTask;
        }

        // The receipt proves THIS receive. A stale one must not delete work
        // another consumer is now holding.
        if (message.PopReceipt != work.PopReceipt)
        {
            RejectedDeletes++;
            return Task.CompletedTask;
        }

        _messages.Remove(message);
        return Task.CompletedTask;
    }

    public async Task QuarantineAsync(ReceivedWork work, PoisonRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(work);

        Poison.Add((record, work.Body));
        await DeleteAsync(work, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> ApproximateDepthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_messages.Count);
    }

    private sealed class Message(string id, string body)
    {
        public string Id { get; } = id;

        public string Body { get; } = body;

        public long DeliveryCount { get; set; }

        public string PopReceipt { get; set; } = string.Empty;
    }
}

/// <summary>An in-memory station registry with conditional insert and replace.</summary>
/// <remarks>
/// Rows are copied in and out. Handing the caller the stored instance would let
/// an implementation "update" a row by mutating the object it read, which passes
/// in memory and loses every write against a real table.
/// </remarks>
internal sealed class InMemoryRegistry : IStationRegistry
{
    private readonly Dictionary<(string Station, string Row), StationState> _rows = [];
    private int _version;

    /// <summary>Replaces that were rejected as stale.</summary>
    public int StaleReplaces { get; private set; }

    /// <summary>Inserts that lost to an existing row.</summary>
    public int LostInserts { get; private set; }

    /// <summary>Runs immediately before a conditional replace, to steal the race.</summary>
    public Action<string, string>? BeforeReplace { get; set; }

    /// <summary>Every row currently stored.</summary>
    public IReadOnlyCollection<StationState> Rows => [.. _rows.Values.Select(Copy)];

    public Task<StationState?> TryGetAsync(string stationId, string rowKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_rows.TryGetValue((stationId, rowKey), out var row) ? Copy(row) : null);
    }

    public Task<string?> TryInsertAsync(StationState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(state);

        var key = (state.StationId, state.RowKey);
        if (_rows.ContainsKey(key))
        {
            LostInserts++;
            return Task.FromResult<string?>(null);
        }

        var stored = Copy(state);
        stored.ETag = NextETag();
        _rows[key] = stored;
        return Task.FromResult<string?>(stored.ETag);
    }

    public Task<string?> TryReplaceAsync(StationState state, string ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(state);

        BeforeReplace?.Invoke(state.StationId, state.RowKey);

        var key = (state.StationId, state.RowKey);
        if (!_rows.TryGetValue(key, out var existing) || existing.ETag != ifMatch)
        {
            StaleReplaces++;
            return Task.FromResult<string?>(null);
        }

        var stored = Copy(state);
        stored.ETag = NextETag();
        _rows[key] = stored;
        return Task.FromResult<string?>(stored.ETag);
    }

    public async IAsyncEnumerable<StationState> QueryStationAsync(
        string stationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var row in _rows.Where(entry => entry.Key.Station == stationId)
            .Select(entry => Copy(entry.Value))
            .OrderBy(row => row.RowKey, StringComparer.Ordinal)
            .ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
            await Task.Yield();
        }
    }

    public Task<bool> DeleteAsync(string stationId, string rowKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_rows.Remove((stationId, rowKey)));
    }

    /// <summary>Bumps the stored version of one row, as a competing writer would.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="rowKey">The row.</param>
    public void StealRace(string stationId, string rowKey)
    {
        if (_rows.TryGetValue((stationId, rowKey), out var row))
        {
            row.ETag = NextETag();
        }
    }

    /// <summary>Advances a watermark row behind the caller's back.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="sequenceNumber">The position the competitor recorded.</param>
    /// <param name="journaledDelta">The count the competitor added.</param>
    public void StealAdvance(string stationId, long sequenceNumber, int journaledDelta)
    {
        if (_rows.TryGetValue((stationId, ExpeditionNaming.WatermarkRowKey), out var row))
        {
            row.LastSequenceNumber = Math.Max(row.LastSequenceNumber, sequenceNumber);
            row.JournaledCount += journaledDelta;
            row.ETag = NextETag();
        }
    }

    private static StationState Copy(StationState row) => new()
    {
        StationId = row.StationId,
        RowKey = row.RowKey,
        Phase = row.Phase,
        LastSequenceNumber = row.LastSequenceNumber,
        JournaledCount = row.JournaledCount,
        ArtifactName = row.ArtifactName,
        UpdatedUtc = row.UpdatedUtc,
        ETag = row.ETag,
    };

    private string NextETag() => $"W/\"datetime'{++_version}'\"";
}

/// <summary>An in-memory journal projection with versions, pages, and throttling.</summary>
/// <remarks>
/// Three service behaviours are modelled because the projector's correctness
/// depends on all three: a conditional write really is refused, a page really can
/// be shorter than the requested size while more results remain, and a throttle
/// really is charged for.
/// </remarks>
internal sealed class InMemoryProjection : IJournalProjection
{
    private readonly Dictionary<(string Station, string Id), JournalEntry> _entries = [];
    private int _version;
    private int _throttlesRemaining;

    /// <summary>Request units one successful operation is charged.</summary>
    public double ChargePerOperation { get; set; } = 5.0;

    /// <summary>Request units a refused, throttled attempt is still charged.</summary>
    public double ThrottleCharge { get; set; } = 1.5;

    /// <summary>How long a throttle asks the caller to wait.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Pages that stop short of the requested size while more remain.</summary>
    public bool ShortPages { get; set; }

    /// <summary>Writes that were rejected as stale, in order.</summary>
    public int StaleWrites { get; private set; }

    /// <summary>How many reads have been served.</summary>
    public int Reads { get; private set; }

    /// <summary>How many writes have landed.</summary>
    public int Writes { get; private set; }

    /// <summary>Every entry currently stored.</summary>
    public IReadOnlyCollection<JournalEntry> Entries => [.. _entries.Values];

    /// <summary>Makes the next <paramref name="count"/> operations answer 429.</summary>
    /// <param name="count">How many operations to refuse.</param>
    public void ThrottleNext(int count) => _throttlesRemaining = count;

    /// <summary>Stores an entry without any precondition, to set up a test.</summary>
    /// <param name="entry">The entry to store.</param>
    /// <returns>The stored entry, carrying its version.</returns>
    public JournalEntry Seed(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var stored = entry with { ETag = NextETag() };
        _entries[(entry.StationId, entry.Id)] = stored;
        return stored;
    }

    public Task<ProjectionResult> WriteAsync(JournalEntry entry, string? ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        Throttle();

        var key = (entry.StationId, entry.Id);
        var exists = _entries.TryGetValue(key, out var existing);

        if (ifMatch is null)
        {
            if (exists)
            {
                // 409: somebody created the document between the caller's read
                // and its write.
                return Task.FromResult(new ProjectionResult(
                    ProjectionOutcome.Superseded,
                    null,
                    ChargePerOperation));
            }
        }
        else if (!exists || existing!.ETag != ifMatch)
        {
            StaleWrites++;
            return Task.FromResult(new ProjectionResult(ProjectionOutcome.Stale, null, ChargePerOperation));
        }

        var stored = entry with { ETag = NextETag() };
        _entries[key] = stored;
        Writes++;
        return Task.FromResult(new ProjectionResult(ProjectionOutcome.Written, stored.ETag, ChargePerOperation));
    }

    public Task<JournalEntry?> TryReadAsync(string stationId, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Throttle();
        Reads++;

        return Task.FromResult(_entries.TryGetValue((stationId, id), out var entry) ? entry : null);
    }

    public Task<JournalPage> QueryStationAsync(
        string stationId,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Throttle();

        var ordered = _entries.Values
            .Where(entry => entry.StationId == stationId)
            .OrderBy(entry => entry.SequenceNumber)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var offset = continuationToken is null
            ? 0
            : int.Parse(continuationToken, CultureInfo.InvariantCulture);

        // A page may stop short of the requested size and still have more to
        // give. A reader that treats a short page as the end truncates its answer
        // silently, and only under load.
        var take = ShortPages ? Math.Max(1, pageSize - 1) : pageSize;
        var page = ordered.Skip(offset).Take(take).ToList();
        var next = offset + page.Count;

        return Task.FromResult(new JournalPage(
            page,
            next < ordered.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
            ChargePerOperation));
    }

    public Task<bool> DeleteAsync(string stationId, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.Remove((stationId, id)));
    }

    /// <summary>Rewrites an entry behind the caller's back, as a competitor would.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="id">The document id.</param>
    /// <param name="sequenceNumber">The position the competitor recorded.</param>
    public void StealRace(string stationId, string id, long sequenceNumber)
    {
        if (_entries.TryGetValue((stationId, id), out var entry))
        {
            _entries[(stationId, id)] = entry with { SequenceNumber = sequenceNumber, ETag = NextETag() };
        }
    }

    private void Throttle()
    {
        if (_throttlesRemaining <= 0)
        {
            return;
        }

        _throttlesRemaining--;
        throw new ThrottledException("The container is rate limited.", RetryAfter, ThrottleCharge);
    }

    private string NextETag() => $"\"{++_version}\"";
}

/// <summary>An in-memory telemetry feed with real partitions and stream positions.</summary>
/// <remarks>
/// Events are assigned a partition from their partition key and a sequence number
/// within that partition, which is what makes order, replay, and checkpoint
/// resumption observable without a broker.
/// </remarks>
internal sealed class InMemoryFeed(int partitionCount = 2) : ITelemetryFeed
{
    private readonly Dictionary<string, List<StreamEvent>> _partitions =
        Enumerable.Range(0, partitionCount).ToDictionary(
            index => index.ToString(CultureInfo.InvariantCulture),
            _ => new List<StreamEvent>(),
            StringComparer.Ordinal);

    /// <summary>Publish calls made, one per batch.</summary>
    public List<IReadOnlyList<TelemetryReading>> Published { get; } = [];

    /// <summary>The positions each read was asked to start after.</summary>
    public List<(string PartitionId, long AfterSequenceNumber)> Reads { get; } = [];

    /// <summary>Redelivers everything from position zero, as an at-least-once feed may.</summary>
    public bool RedeliverEverything { get; set; }

    /// <summary>Every event the feed holds, by partition.</summary>
    public IReadOnlyDictionary<string, List<StreamEvent>> Partitions => _partitions;

    /// <summary>The partition a key routes to.</summary>
    /// <param name="partitionKey">The routing key.</param>
    /// <returns>The partition id.</returns>
    public string PartitionFor(string partitionKey) =>
        (Math.Abs(StringComparer.Ordinal.GetHashCode(partitionKey)) % _partitions.Count)
            .ToString(CultureInfo.InvariantCulture);

    public Task<PublishReceipt> PublishAsync(
        IReadOnlyList<TelemetryReading> readings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(readings);

        Published.Add(readings);
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var reading in readings)
        {
            var partitionKey = ExpeditionNaming.PartitionKey(reading.Key);
            var partitionId = PartitionFor(partitionKey);
            var partition = _partitions[partitionId];
            var sequenceNumber = partition.Count;

            partition.Add(new StreamEvent(
                partitionId,
                sequenceNumber,
                $"o{sequenceNumber.ToString(CultureInfo.InvariantCulture)}",
                partitionKey,
                reading));

            byKey[partitionKey] = byKey.GetValueOrDefault(partitionKey) + 1;
        }

        return Task.FromResult(new PublishReceipt(1, readings.Count, byKey));
    }

    public Task<IReadOnlyList<string>> GetPartitionIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<string>>(
            [.. _partitions.Keys.OrderBy(id => id, StringComparer.Ordinal)]);
    }

    public async IAsyncEnumerable<StreamEvent> ReadPartitionAsync(
        string partitionId,
        long afterSequenceNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Reads.Add((partitionId, afterSequenceNumber));

        var from = RedeliverEverything ? -1 : afterSequenceNumber;
        foreach (var streamEvent in _partitions[partitionId].Where(item => item.SequenceNumber > from).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
            await Task.Yield();
        }
    }
}

/// <summary>A clock that only moves when a test moves it.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="amount">How far to move.</param>
    public void Advance(TimeSpan amount) => _now += amount;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>A handler that records every work order it was asked to apply.</summary>
internal sealed class RecordingEffect
{
    /// <summary>Work orders the effect actually ran for, in order.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>Work order ids the effect should fail for.</summary>
    public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

    /// <summary>Work order ids the effect should cancel on.</summary>
    public HashSet<string> CancelFor { get; } = new(StringComparer.Ordinal);

    /// <summary>The effect, shaped for <see cref="ArtifactWorker"/>.</summary>
    /// <param name="order">The work order.</param>
    /// <param name="cancellationToken">The token the worker passed.</param>
    /// <returns>A completed task, unless the order is configured to fail.</returns>
    public Task ApplyAsync(ArtifactWorkOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        if (CancelFor.Contains(order.WorkOrderId))
        {
            throw new OperationCanceledException("The host is shutting down.");
        }

        if (FailFor.Contains(order.WorkOrderId))
        {
            throw new InvalidOperationException($"Summary failed for {order.WorkOrderId}.");
        }

        Applied.Add(order.WorkOrderId);
        return Task.CompletedTask;
    }
}

/// <summary>Fixture values shared by the milestone suites.</summary>
internal static class Fixture
{
    /// <summary>The station every suite works with.</summary>
    public const string Station = "ridge-camp";

    /// <summary>A second station, so partitioning is observable.</summary>
    public const string OtherStation = "delta-camp";

    /// <summary>The observation every suite works with.</summary>
    public const string Observation = "obs-0001";

    /// <summary>A fixed start instant, so every stamped row is reproducible.</summary>
    public static DateTimeOffset Start { get; } = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The observation key every suite works with.</summary>
    public static ObservationKey Key { get; } = new(Station, Observation);

    /// <summary>A reading for <paramref name="observation"/>.</summary>
    /// <param name="observation">The observation id.</param>
    /// <param name="station">The station id.</param>
    /// <param name="celsius">The temperature.</param>
    /// <param name="minutes">Minutes after the fixture start.</param>
    /// <returns>The reading.</returns>
    public static TelemetryReading Reading(
        string observation = Observation,
        string station = Station,
        double celsius = -14.5,
        int minutes = 0) =>
        new(station, observation, celsius, Start.AddMinutes(minutes));

    /// <summary>A work order for <paramref name="observation"/>.</summary>
    /// <param name="observation">The observation id.</param>
    /// <param name="station">The station id.</param>
    /// <returns>The work order.</returns>
    public static ArtifactWorkOrder Order(string observation = Observation, string station = Station)
    {
        var key = new ObservationKey(station, observation);
        return new ArtifactWorkOrder(
            ExpeditionNaming.WorkOrderId(key, WorkOperations.Summarize),
            station,
            observation,
            ExpeditionNaming.ArtifactName(key),
            WorkOperations.Summarize);
    }

    /// <summary>A received message carrying <paramref name="order"/>.</summary>
    /// <param name="order">The work order.</param>
    /// <param name="deliveryCount">Which delivery this is.</param>
    /// <param name="messageId">The message id.</param>
    /// <returns>The received message.</returns>
    public static ReceivedWork Delivery(ArtifactWorkOrder order, long deliveryCount = 1, string messageId = "m1") =>
        new(messageId, $"receipt-{deliveryCount}", deliveryCount, JournalCodec.EncodeWorkOrder(order));

    /// <summary>A journal entry for one stream event.</summary>
    /// <param name="observation">The observation id.</param>
    /// <param name="sequenceNumber">The stream position it was projected from.</param>
    /// <param name="station">The station id.</param>
    /// <param name="celsius">The temperature.</param>
    /// <returns>The entry.</returns>
    public static JournalEntry Entry(
        string observation = Observation,
        long sequenceNumber = 0,
        string station = Station,
        double celsius = -14.5)
    {
        var key = new ObservationKey(station, observation);
        return new JournalEntry(
            ExpeditionNaming.JournalItemId(key),
            station,
            observation,
            "0",
            sequenceNumber,
            celsius,
            ExpeditionNaming.ArtifactName(key),
            Start);
    }
}
