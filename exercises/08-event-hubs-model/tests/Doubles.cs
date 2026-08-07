namespace LearningAzure.Exercises.EventHubsModel.Tests;

/// <summary>
/// A batch with an exact byte budget, modelled on <c>EventDataBatch</c>: one
/// partition key, a fixed maximum, and a <c>TryAdd</c> that refuses rather than
/// throws.
/// </summary>
internal sealed class BudgetedBatch : IEventBatch
{
    /// <summary>Per-event framing overhead the service adds, in bytes.</summary>
    public const int PerEventOverhead = 16;

    private readonly List<int> _bodies = [];

    public BudgetedBatch(string? partitionKey, long maximumSizeInBytes)
    {
        PartitionKey = partitionKey;
        MaximumSizeInBytes = maximumSizeInBytes;
    }

    public string? PartitionKey { get; }

    public int Count => _bodies.Count;

    public long SizeInBytes => _bodies.Sum(body => (long)body + PerEventOverhead);

    public long MaximumSizeInBytes { get; }

    /// <summary>Bodies accepted, in the order they were added.</summary>
    public IReadOnlyList<int> Bodies => _bodies;

    /// <summary>How many times <see cref="TryAdd"/> refused an event.</summary>
    public int Refusals { get; private set; }

    public bool TryAdd(int bodyBytes)
    {
        if (SizeInBytes + bodyBytes + PerEventOverhead > MaximumSizeInBytes)
        {
            Refusals++;
            return false;
        }

        _bodies.Add(bodyBytes);
        return true;
    }
}

/// <summary>Creates <see cref="BudgetedBatch"/> instances and records every one.</summary>
internal sealed class RecordingBatchFactory
{
    private readonly long _maximumSizeInBytes;

    public RecordingBatchFactory(long maximumSizeInBytes) => _maximumSizeInBytes = maximumSizeInBytes;

    /// <summary>Every batch handed out, in creation order.</summary>
    public List<BudgetedBatch> Created { get; } = [];

    /// <summary>The factory delegate to pass to the code under test.</summary>
    public EventBatchFactory Factory => partitionKey =>
    {
        var batch = new BudgetedBatch(partitionKey, _maximumSizeInBytes);
        Created.Add(batch);
        return batch;
    };
}

/// <summary>Fixture readings used across the evaluator.</summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset Noon = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    public static TelemetryReading Reading(string stationId, int minute, int bodyBytes = 200) =>
        new(stationId, Noon.AddMinutes(minute), -3.5, bodyBytes);

    public static IReadOnlyList<TelemetryReading> Readings(
        IReadOnlyList<string> stations,
        int perStation,
        int bodyBytes = 200)
    {
        var readings = new List<TelemetryReading>(stations.Count * perStation);

        for (var index = 0; index < perStation; index++)
        {
            foreach (var station in stations)
            {
                readings.Add(Reading(station, index, bodyBytes));
            }
        }

        return readings;
    }
}
