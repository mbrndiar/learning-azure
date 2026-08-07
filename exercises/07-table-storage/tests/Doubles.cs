using Azure;

namespace LearningAzure.Exercises.TableStorage.Tests;

/// <summary>
/// An in-memory table that enforces ETag preconditions the way the service does,
/// and can be told to let a competing writer land between a read and a write.
/// </summary>
internal sealed class RacingTable : IObservationTable
{
    private readonly Dictionary<string, ObservationEntity> _rows = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>Writes a competing writer should perform, one per read.</summary>
    /// <remarks>Each entry is applied immediately after a read returns.</remarks>
    public Queue<Action<ObservationEntity>> CompetingWrites { get; } = new();

    /// <summary>Every ETag a caller bet on, in order.</summary>
    public List<string> EtagsBetOn { get; } = [];

    /// <summary>How many reads have been served.</summary>
    public int Reads { get; private set; }

    /// <summary>How many writes were attempted.</summary>
    public int Writes { get; private set; }

    public void Seed(ObservationEntity entity)
    {
        entity.ETag = NextEtag();
        _rows[Key(entity.PartitionKey, entity.RowKey)] = Clone(entity);
    }

    public ObservationEntity? Peek(string partitionKey, string rowKey) =>
        _rows.TryGetValue(Key(partitionKey, rowKey), out var stored) ? Clone(stored) : null;

    public Task<ObservationEntity?> TryGetAsync(
        string partitionKey,
        string rowKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;

        if (!_rows.TryGetValue(Key(partitionKey, rowKey), out var stored))
        {
            return Task.FromResult<ObservationEntity?>(null);
        }

        var handed = Clone(stored);

        if (CompetingWrites.Count > 0)
        {
            var competitor = CompetingWrites.Dequeue();
            competitor(stored);
            stored.ETag = NextEtag();
        }

        return Task.FromResult<ObservationEntity?>(handed);
    }

    public Task<bool> TryReplaceAsync(ObservationEntity entity, ETag ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes++;
        EtagsBetOn.Add(ifMatch.ToString());

        if (!_rows.TryGetValue(Key(entity.PartitionKey, entity.RowKey), out var stored))
        {
            return Task.FromResult(false);
        }

        if (ifMatch != ETag.All && stored.ETag != ifMatch)
        {
            return Task.FromResult(false);
        }

        var replacement = Clone(entity);
        replacement.ETag = NextEtag();
        _rows[Key(entity.PartitionKey, entity.RowKey)] = replacement;
        return Task.FromResult(true);
    }

    private ETag NextEtag() => new($"W/\"0x{++_version:X}\"");

    private static string Key(string partitionKey, string rowKey) => $"{partitionKey}\u0000{rowKey}";

    private static ObservationEntity Clone(ObservationEntity source) => new()
    {
        PartitionKey = source.PartitionKey,
        RowKey = source.RowKey,
        Timestamp = source.Timestamp,
        ETag = source.ETag,
        StationId = source.StationId,
        ObservedAt = source.ObservedAt,
        TemperatureC = source.TemperatureC,
        Status = source.Status,
    };
}
