namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>
/// Turns partition properties and a checkpoint ledger into the one number that
/// says whether a consumer is keeping up.
/// </summary>
public static class LagCalculator
{
    /// <summary>Measures how far behind a consumer group is.</summary>
    /// <param name="partitions">The service's view of each partition.</param>
    /// <param name="ledger">The group's recorded positions.</param>
    /// <returns>Per-partition and total lag.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ConsumerLag Measure(IEnumerable<PartitionSnapshot> partitions, CheckpointLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(ledger);

        // GAP 9 — Order the results by partition id, and treat a partition with
        // no checkpoint as position -1: its whole contents are outstanding.
        // Count those partitions in PartitionsWithoutCheckpoint. Reporting them
        // as zero lag is what makes a dashboard green while an entire partition
        // goes unread.
        //
        // GAP 10 — Lag is LastEnqueuedSequenceNumber minus the checkpointed
        // position, clamped at zero. A checkpoint ahead of the snapshot is not
        // an error to propagate; it just means the snapshot is a moment older
        // than the ledger. TotalLag is the sum of the per-partition lags.
        throw new NotImplementedException(
            "GAP 9: implement LagCalculator.Measure. See "
            + "lessons/09-event-hubs-processing/README.md#lag-is-measured-against-the-checkpoint.");
    }
}
