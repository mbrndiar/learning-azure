namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>
/// The record of how far a consumer group has got in each partition. This is
/// the only thing a restarted processor knows about the past.
/// </summary>
public sealed class CheckpointLedger
{
    private readonly Dictionary<string, long> _positions = new(StringComparer.Ordinal);

    /// <summary>Gets how many checkpoint writes were accepted.</summary>
    public int Writes { get; private set; }

    /// <summary>Gets how many checkpoint writes were rejected for moving backwards.</summary>
    public int RejectedRewinds { get; private set; }

    /// <summary>Records that everything up to and including <paramref name="sequenceNumber"/> is done.</summary>
    /// <param name="partitionId">The partition the position belongs to.</param>
    /// <param name="sequenceNumber">The sequence number of the last handled event.</param>
    /// <returns><see langword="true"/> when the ledger moved forward.</returns>
    /// <exception cref="ArgumentException"><paramref name="partitionId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceNumber"/> is negative.</exception>
    public bool Record(string partitionId, long sequenceNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceNumber);

        // GAP 1 — Store the position, but only when it moves FORWARD. A
        // sequence number at or below the recorded one is a rewind: count it in
        // RejectedRewinds, leave the ledger alone, and return false. Count
        // accepted writes in Writes.
        //
        // Out-of-order completion is normal when a handler is concurrent, and a
        // checkpoint that moves backwards silently re-delivers everything in
        // between on the next restart.
        throw new NotImplementedException(
            "GAP 1: implement CheckpointLedger.Record. See "
            + "lessons/09-event-hubs-processing/README.md#a-checkpoint-is-a-promise-not-a-position.");
    }

    /// <summary>Gets the recorded position for a partition, if there is one.</summary>
    /// <param name="partitionId">The partition to look up.</param>
    /// <param name="sequenceNumber">The recorded sequence number.</param>
    /// <returns><see langword="true"/> when a position has been recorded.</returns>
    public bool TryGetCheckpoint(string partitionId, out long sequenceNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);

        return _positions.TryGetValue(partitionId, out sequenceNumber);
    }

    /// <summary>Works out where a processor should start reading a partition.</summary>
    /// <param name="partitionId">The partition being claimed.</param>
    /// <returns>The position to start from.</returns>
    /// <exception cref="ArgumentException"><paramref name="partitionId"/> is empty.</exception>
    public ResumePosition ResumeFrom(string partitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);

        // GAP 2 — When a position exists, resume AFTER it: IsInclusive must be
        // false, because the checkpoint means "this one is done".
        //
        // GAP 3 — When no position exists, return SequenceNumber -1 and
        // IsFromStart true. Zero is a real sequence number, so it cannot double
        // as "nothing recorded"; the caller has to choose a starting position
        // deliberately.
        throw new NotImplementedException(
            "GAP 2: implement CheckpointLedger.ResumeFrom. See "
            + "lessons/09-event-hubs-processing/README.md#resume-means-after.");
    }

    /// <summary>Gets a copy of the recorded positions, for reporting.</summary>
    /// <returns>Partition id to recorded sequence number.</returns>
    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_positions, StringComparer.Ordinal);
}
