namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>A clock the tests move by hand, so the time bound is exact.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Gets or sets how far the clock jumps on every read.</summary>
    public TimeSpan AutoAdvance { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        var now = _now;
        _now = _now.Add(AutoAdvance);
        return now;
    }
}

/// <summary>Fixed inputs the checks share.</summary>
internal static class Fixtures
{
    public const string PartitionZero = "0";
    public const string PartitionOne = "1";

    /// <summary>Builds a run of events on one partition, numbered from <paramref name="from"/>.</summary>
    public static List<HandledEvent> Run(string partitionId, long from, int count, string body = "reading") =>
        [.. Enumerable.Range(0, count).Select(offset =>
            new HandledEvent(partitionId, from + offset, body))];

    /// <summary>A policy that never fires on time, so only the event bound matters.</summary>
    public static CheckpointPolicy EveryNEvents(int events) =>
        new(events, TimeSpan.FromHours(24));

    /// <summary>A policy that never fires on count, so only the time bound matters.</summary>
    public static CheckpointPolicy EveryInterval(TimeSpan interval) =>
        new(int.MaxValue, interval);

    /// <summary>A policy that never fires at all, so only the closing checkpoint is written.</summary>
    public static CheckpointPolicy Never() =>
        new(int.MaxValue, TimeSpan.FromDays(365));

    /// <summary>
    /// Streams events and pulls the plug after <paramref name="after"/> of them,
    /// which is what a rolling deployment looks like from inside a handler.
    /// </summary>
    public static async IAsyncEnumerable<HandledEvent> CancelAfter(
        IEnumerable<HandledEvent> events,
        int after,
        CancellationTokenSource cancellation)
    {
        var delivered = 0;

        foreach (var handled in events)
        {
            yield return handled;
            await Task.Yield();

            if (++delivered == after)
            {
                await cancellation.CancelAsync();
            }
        }
    }
}
