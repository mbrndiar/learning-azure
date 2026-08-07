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

        var results = new List<PartitionLag>();
        long total = 0;
        var withoutCheckpoint = 0;

        foreach (var partition in partitions.OrderBy(item => item.PartitionId, StringComparer.Ordinal))
        {
            var hasCheckpoint = ledger.TryGetCheckpoint(partition.PartitionId, out var checkpointed);

            if (!hasCheckpoint)
            {
                // GAP 9: an unclaimed partition is maximally behind, not caught up.
                //
                // Treating "no checkpoint" as zero lag is the failure mode that
                // makes a dashboard green while an entire partition goes unread.
                // The backlog is every event the partition holds.
                // See lessons/09-event-hubs-processing/README.md#lag-is-measured-against-the-checkpoint
                checkpointed = -1;
                withoutCheckpoint++;
            }

            // GAP 10: lag is the distance between the recorded position and the
            // last enqueued one — and it can never be negative.
            //
            // A checkpoint ahead of the last enqueued sequence number is not an
            // error to propagate: it happens when the snapshot is a moment older
            // than the ledger. Clamping keeps a monitoring signal from going
            // negative and being silently dropped by an alert rule.
            var lag = Math.Max(0, partition.LastEnqueuedSequenceNumber - checkpointed);

            total += lag;
            results.Add(new PartitionLag(
                partition.PartitionId,
                checkpointed,
                partition.LastEnqueuedSequenceNumber,
                lag,
                hasCheckpoint));
        }

        return new ConsumerLag(results, total, withoutCheckpoint);
    }
}
