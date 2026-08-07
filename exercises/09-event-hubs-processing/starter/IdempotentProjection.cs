using System.Globalization;

namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>
/// A projection that can be fed the same event twice without changing its
/// answer. Every Event Hubs consumer needs one, because at-least-once delivery
/// is a guarantee about the service, not a hope about the handler.
/// </summary>
public sealed class IdempotentProjection
{
    private readonly Dictionary<string, long> _highWaterMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _countsByBody = new(StringComparer.Ordinal);

    /// <summary>Gets how many events changed the projection.</summary>
    public int Applied { get; private set; }

    /// <summary>Gets how many events were recognised as already applied.</summary>
    public int Skipped { get; private set; }

    /// <summary>Gets how many partitions this projection has applied an event from.</summary>
    public int TrackedPartitions => _highWaterMarks.Count;

    /// <summary>Gets the running totals this projection maintains.</summary>
    public IReadOnlyDictionary<string, int> Totals => _countsByBody;

    /// <summary>Applies an event, ignoring it when it has already been applied.</summary>
    /// <param name="handled">The event as delivered.</param>
    /// <returns><see langword="true"/> when the projection changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handled"/> is <see langword="null"/>.</exception>
    public bool Apply(HandledEvent handled)
    {
        ArgumentNullException.ThrowIfNull(handled);

        // GAP 6 — Deduplicate on (PartitionId, SequenceNumber) and on nothing
        // else. An event at or below the partition's high-water mark has already
        // been applied: count it in Skipped and return false.
        //
        // The payload is not a key: two identical readings a second apart are
        // two events.
        //
        // GAP 7 — Otherwise move the high-water mark to THIS event's sequence
        // number (not the old one plus one — sequence numbers are increasing,
        // not contiguous), add one to _countsByBody for the event's Body, count
        // it in Applied, and return true.
        throw new NotImplementedException(
            "GAP 6: implement IdempotentProjection.Apply. See "
            + "lessons/09-event-hubs-processing/README.md#at-least-once-is-a-number.");
    }

    /// <summary>Gets the highest sequence number applied for a partition.</summary>
    /// <param name="partitionId">The partition to look up.</param>
    /// <returns>The high-water mark, or -1 when the partition has no applied events.</returns>
    /// <exception cref="ArgumentException"><paramref name="partitionId"/> is empty.</exception>
    public long HighWaterMark(string partitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);

        // GAP 8 — Return the recorded mark, or -1 when the partition has never
        // been seen. Zero is a real sequence number; returning it here makes the
        // first event of every partition look like a duplicate.
        throw new NotImplementedException(
            "GAP 8: implement IdempotentProjection.HighWaterMark. See "
            + "lessons/09-event-hubs-processing/README.md#at-least-once-is-a-number.");
    }

    /// <summary>Describes the projection, for the lesson output.</summary>
    /// <returns>A one-line summary.</returns>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"applied {Applied}, skipped {Skipped}, distinct bodies {_countsByBody.Count}");
}
