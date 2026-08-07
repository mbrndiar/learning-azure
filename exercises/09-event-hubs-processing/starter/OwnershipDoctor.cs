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

        // GAP 11 — Return, in this order:
        //
        //   UnownedPartitions   when the distinct owned partitions are fewer
        //                       than PartitionCount and ownership is NOT
        //                       thrashing. Events are not being read at all,
        //                       which outranks everything else.
        //   Thrashing           when OwnershipChangesInLastMinute is at or above
        //                       ThrashingThreshold. Check it before concluding a
        //                       processor is idle: a rebalancing cluster looks
        //                       under-owned in any single snapshot, and
        //                       misdiagnosing it gets it scaled DOWN into a
        //                       backlog.
        //   IdleProcessors      when ProcessorCount exceeds PartitionCount.
        //                       Money, not data loss, so it comes last.
        //   Balanced            otherwise.
        throw new NotImplementedException(
            "GAP 11: implement OwnershipDoctor.Diagnose. See "
            + "lessons/09-event-hubs-processing/README.md#ownership-is-a-lease.");
    }
}
