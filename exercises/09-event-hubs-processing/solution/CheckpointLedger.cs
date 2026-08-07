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

        // GAP 1: a checkpoint may only ever move forward.
        //
        // Out-of-order completion is normal when a handler is concurrent, and a
        // checkpoint that moves backwards silently re-delivers everything in
        // between on the next restart. Rejecting the rewind is the whole job.
        // See lessons/09-event-hubs-processing/README.md#a-checkpoint-is-a-promise-not-a-position
        if (_positions.TryGetValue(partitionId, out var current) && sequenceNumber <= current)
        {
            RejectedRewinds++;
            return false;
        }

        _positions[partitionId] = sequenceNumber;
        Writes++;
        return true;
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

        // GAP 2: resume AFTER the checkpointed event, not AT it.
        //
        // The checkpoint says "this one is done". Starting inclusively replays
        // it every single time the processor restarts, which is the most common
        // way a consumer produces a permanent, low-grade stream of duplicates
        // that nobody notices until the totals are audited.
        // See lessons/09-event-hubs-processing/README.md#resume-means-after
        if (_positions.TryGetValue(partitionId, out var sequenceNumber))
        {
            return new ResumePosition(sequenceNumber, IsInclusive: false, IsFromStart: false);
        }

        // GAP 3: no checkpoint is not the same as position zero.
        //
        // A partition the group has never read has no position at all, and the
        // caller has to choose a default deliberately. Reporting -1 and
        // IsFromStart makes that choice visible instead of accidental.
        return new ResumePosition(-1, IsInclusive: false, IsFromStart: true);
    }

    /// <summary>Gets a copy of the recorded positions, for reporting.</summary>
    /// <returns>Partition id to recorded sequence number.</returns>
    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_positions, StringComparer.Ordinal);
}
