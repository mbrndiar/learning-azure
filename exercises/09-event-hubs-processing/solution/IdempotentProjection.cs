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

        // GAP 6: deduplicate on (partition, sequence number), and on nothing else.
        //
        // The payload is not a key: two identical readings a second apart are
        // two events. The message id is not a key either, because a redelivery
        // and a genuine resend are indistinguishable by it. Position within a
        // partition is the only identity the service actually guarantees.
        // See lessons/09-event-hubs-processing/README.md#at-least-once-is-a-number
        if (_highWaterMarks.TryGetValue(handled.PartitionId, out var mark) && handled.SequenceNumber <= mark)
        {
            Skipped++;
            return false;
        }

        // GAP 7: the high-water mark moves before the side effect is recorded as
        // done, and it moves to THIS event, not by one.
        //
        // Sequence numbers are increasing, not contiguous: the service is free
        // to leave gaps. A projection that assumes +1 stalls forever the first
        // time it sees one.
        _highWaterMarks[handled.PartitionId] = handled.SequenceNumber;

        _countsByBody[handled.Body] = _countsByBody.TryGetValue(handled.Body, out var count) ? count + 1 : 1;
        Applied++;
        return true;
    }

    /// <summary>Gets the highest sequence number applied for a partition.</summary>
    /// <param name="partitionId">The partition to look up.</param>
    /// <returns>The high-water mark, or -1 when the partition has no applied events.</returns>
    /// <exception cref="ArgumentException"><paramref name="partitionId"/> is empty.</exception>
    public long HighWaterMark(string partitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);

        // GAP 8: an unseen partition is -1, not 0.
        //
        // Zero is a real sequence number. Returning it for "nothing seen" makes
        // the first event of every partition look like a duplicate.
        return _highWaterMarks.TryGetValue(partitionId, out var mark) ? mark : -1;
    }

    /// <summary>Describes the projection, for the lesson output.</summary>
    /// <returns>A one-line summary.</returns>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"applied {Applied}, skipped {Skipped}, distinct bodies {_countsByBody.Count}");
}
