namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>
/// Reads an ownership snapshot and says what is wrong with the deployment, in
/// the order that matters when more than one thing is.
/// </summary>
public static class OwnershipDoctor
{
    /// <summary>How many ownership changes per minute counts as thrashing.</summary>
    public const int ThrashingThreshold = 10;

    /// <summary>Diagnoses a consumer deployment from who owns what.</summary>
    /// <param name="snapshot">The observation.</param>
    /// <returns>The most important problem, or <see cref="OwnershipVerdict.Balanced"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The snapshot has a non-positive partition count.</exception>
    public static OwnershipVerdict Diagnose(OwnershipSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshot.PartitionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.ProcessorCount);

        var owned = snapshot.OwnedPartitionsByProcessor.Values
            .SelectMany(partitions => partitions)
            .Distinct(StringComparer.Ordinal)
            .Count();

        // GAP 11: order the diagnoses by how much data is at risk.
        //
        // Unowned partitions mean events are not being read at all, which
        // outranks everything else. Thrashing means ownership keeps moving, so
        // any snapshot of it looks incomplete — check it before concluding that
        // a processor is idle, or a rebalancing cluster gets misdiagnosed as an
        // over-provisioned one and scaled DOWN into a backlog.
        // Idle processors are last: they are money, not data loss.
        // See lessons/09-event-hubs-processing/README.md#ownership-is-a-lease
        if (owned < snapshot.PartitionCount && snapshot.OwnershipChangesInLastMinute < ThrashingThreshold)
        {
            return OwnershipVerdict.UnownedPartitions;
        }

        if (snapshot.OwnershipChangesInLastMinute >= ThrashingThreshold)
        {
            return OwnershipVerdict.Thrashing;
        }

        return snapshot.ProcessorCount > snapshot.PartitionCount
            ? OwnershipVerdict.IdleProcessors
            : OwnershipVerdict.Balanced;
    }
}
