using System.Globalization;
using System.Runtime.CompilerServices;

namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>An in-memory artifact store with real conditional-write semantics.</summary>
/// <remarks>
/// The fake is deliberately strict where the service is strict: a create only
/// lands when the name is free, a replace only lands when the version matches,
/// and the stored version changes on every write. A permissive fake would let a
/// last-write-wins implementation pass, which is the one bug this project exists
/// to prevent.
/// </remarks>
internal sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly Dictionary<string, Entry> _artifacts = new(StringComparer.Ordinal);
    private int _version;

    /// <summary>Every name a write was attempted for, in order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Every name a delete was attempted for, in order.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>Runs immediately before a conditional replace, to steal the race.</summary>
    public Action<string>? BeforeReplace { get; set; }

    /// <summary>The bytes currently stored under <paramref name="name"/>.</summary>
    public byte[] this[string name] => _artifacts[name].Content;

    /// <summary>How many artifacts are stored.</summary>
    public int Count => _artifacts.Count;

    /// <summary>The current version of one artifact.</summary>
    public string ETagOf(string name) => _artifacts[name].ETag;

    /// <summary>Writes an artifact without any precondition, to set up a test.</summary>
    public void Seed(string name, string content)
    {
        _artifacts[name] = new Entry(System.Text.Encoding.UTF8.GetBytes(content), NextETag(), "application/json");
    }

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

        var entry = new Entry(await ReadAsync(content, cancellationToken).ConfigureAwait(false), NextETag(), contentType);
        _artifacts[name] = entry;
        return new ArtifactWriteResult(WriteOutcome.Written, entry.ETag);
    }

    public async Task<ArtifactWriteResult> ReplaceIfUnchangedAsync(
        string name,
        Stream content,
        string contentType,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add(name);
        BeforeReplace?.Invoke(name);

        if (!_artifacts.TryGetValue(name, out var existing) || existing.ETag != ifMatch)
        {
            return new ArtifactWriteResult(WriteOutcome.Stale, null);
        }

        var entry = new Entry(await ReadAsync(content, cancellationToken).ConfigureAwait(false), NextETag(), contentType);
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
        foreach (var name in _artifacts.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
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

    /// <summary>Bumps the stored version of one artifact, as a competing writer would.</summary>
    public void StealRace(string name)
    {
        if (_artifacts.TryGetValue(name, out var entry))
        {
            _artifacts[name] = entry with { ETag = NextETag() };
        }
    }

    private static async Task<byte[]> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private string NextETag() =>
        $"\"0x{(++_version).ToString("X8", CultureInfo.InvariantCulture)}\"";

    private sealed record Entry(byte[] Content, string ETag, string ContentType);
}

/// <summary>An in-memory work backlog that redelivers exactly as a Storage queue does.</summary>
/// <remarks>
/// Visibility is modelled by receive rather than by wall clock: a message that is
/// not deleted comes back on the next receive with its dequeue count increased.
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

    /// <summary>Delete attempts that were rejected because the pop receipt was stale.</summary>
    public int RejectedDeletes { get; private set; }

    /// <summary>Messages still on the backlog.</summary>
    public int Depth => _messages.Count;

    /// <summary>Puts a raw body on the backlog, as a misbehaving producer would.</summary>
    public void SendRaw(string body)
    {
        Sent.Add(body);
        _messages.Add(new Message($"m{++_nextId}", body));
    }

    public Task SendAsync(WorkOrder order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendRaw(WorkOrderCodec.Encode(order));
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
            message.DequeueCount++;
            message.PopReceipt = $"r{++_nextReceipt}";
            batch.Add(new ReceivedWork(message.Id, message.PopReceipt, message.DequeueCount, message.Body));
        }

        return Task.FromResult<IReadOnlyList<ReceivedWork>>(batch);
    }

    public Task DeleteAsync(ReceivedWork work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        public long DequeueCount { get; set; }

        public string PopReceipt { get; set; } = string.Empty;
    }
}

/// <summary>An in-memory status index with conditional insert and replace.</summary>
/// <remarks>
/// Rows are copied in and out. Handing the caller the stored instance would let
/// an implementation "update" a row by mutating the object it read, which passes
/// in memory and loses every write against a real table.
/// </remarks>
internal sealed class InMemoryStatusIndex : IStationStatusIndex
{
    private readonly Dictionary<(string Station, string Row), StationStatus> _rows = [];
    private int _version;

    /// <summary>Rows that a conditional replace rejected as stale.</summary>
    public int StaleReplaces { get; private set; }

    /// <summary>Inserts that lost to an existing row.</summary>
    public int LostInserts { get; private set; }

    /// <summary>Runs immediately before a conditional replace, to steal the race.</summary>
    public Action<string, string>? BeforeReplace { get; set; }

    /// <summary>Every row currently stored.</summary>
    public IReadOnlyCollection<StationStatus> Rows => [.. _rows.Values.Select(Copy)];

    public Task<StationStatus?> TryGetAsync(string stationId, string rowKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_rows.TryGetValue((stationId, rowKey), out var row) ? Copy(row) : null);
    }

    public Task<string?> TryInsertAsync(StationStatus status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(status);

        var key = (status.StationId, status.RowKey);
        if (_rows.ContainsKey(key))
        {
            LostInserts++;
            return Task.FromResult<string?>(null);
        }

        var stored = Copy(status);
        stored.ETag = NextETag();
        _rows[key] = stored;
        return Task.FromResult<string?>(stored.ETag);
    }

    public Task<string?> TryReplaceAsync(StationStatus status, string ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(status);

        BeforeReplace?.Invoke(status.StationId, status.RowKey);

        var key = (status.StationId, status.RowKey);
        if (!_rows.TryGetValue(key, out var existing) || existing.ETag != ifMatch)
        {
            StaleReplaces++;
            return Task.FromResult<string?>(null);
        }

        var stored = Copy(status);
        stored.ETag = NextETag();
        _rows[key] = stored;
        return Task.FromResult<string?>(stored.ETag);
    }

    public async IAsyncEnumerable<StationStatus> QueryStationAsync(
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
    public void StealRace(string stationId, string rowKey)
    {
        if (_rows.TryGetValue((stationId, rowKey), out var row))
        {
            row.ETag = NextETag();
        }
    }

    /// <summary>Adds one to a row's counter behind the caller's back.</summary>
    public void StealIncrement(string stationId, string rowKey)
    {
        if (_rows.TryGetValue((stationId, rowKey), out var row))
        {
            row.ProcessedCount++;
            row.ETag = NextETag();
        }
    }

    private static StationStatus Copy(StationStatus row) => new()
    {
        StationId = row.StationId,
        RowKey = row.RowKey,
        State = row.State,
        ProcessedCount = row.ProcessedCount,
        ArtifactName = row.ArtifactName,
        UpdatedUtc = row.UpdatedUtc,
        ETag = row.ETag,
    };

    private string NextETag() => $"W/\"datetime'{++_version}'\"";
}

/// <summary>A clock that only moves when a test moves it.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>Moves the clock forward.</summary>
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

    /// <summary>The effect, shaped for <see cref="StationWorker"/>.</summary>
    public Task ApplyAsync(WorkOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        if (CancelFor.Contains(order.WorkOrderId))
        {
            throw new OperationCanceledException("The host is shutting down.");
        }

        if (FailFor.Contains(order.WorkOrderId))
        {
            throw new InvalidOperationException($"Checksum failed for {order.WorkOrderId}.");
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

    /// <summary>The observation every suite works with.</summary>
    public const string Observation = "obs-0001";

    /// <summary>The operation every suite dispatches.</summary>
    public const string Operation = "checksum";

    /// <summary>A fixed start instant, so every stamped row is reproducible.</summary>
    public static DateTimeOffset Start { get; } = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The artifact key every suite works with.</summary>
    public static ArtifactKey Key { get; } = new(Station, Observation);

    /// <summary>A work order for <paramref name="observation"/>.</summary>
    public static WorkOrder Order(string observation = Observation)
    {
        var key = new ArtifactKey(Station, observation);
        return new WorkOrder(
            StationNaming.WorkOrderId(key, Operation),
            Station,
            observation,
            StationNaming.ArtifactName(key),
            Operation);
    }

    /// <summary>A received message carrying <paramref name="order"/>.</summary>
    public static ReceivedWork Delivery(WorkOrder order, long dequeueCount = 1, string messageId = "m1") =>
        new(messageId, $"receipt-{dequeueCount}", dequeueCount, WorkOrderCodec.Encode(order));

    /// <summary>A readable stream over <paramref name="content"/>.</summary>
    public static Stream Content(string content) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content), writable: false);
}
