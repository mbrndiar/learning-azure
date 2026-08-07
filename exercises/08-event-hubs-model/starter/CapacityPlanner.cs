namespace LearningAzure.Exercises.EventHubsModel;

/// <summary>
/// Sizes a Standard-tier hub from a measured workload, and says which of the
/// resulting decisions can still be changed afterwards.
/// </summary>
/// <remarks>
/// Standard-tier figures, from the Event Hubs quotas and scalability
/// documentation: one throughput unit admits 1 MB/s or 1,000 events per second
/// of ingress and 2 MB/s of egress; one partition sustains roughly 1 MB/s
/// ingress and 2 MB/s egress; a namespace holds at most 40 throughput units and
/// a hub at most 32 partitions.
/// </remarks>
public static class CapacityPlanner
{
    /// <summary>Ingress bytes per second admitted by one throughput unit.</summary>
    public const int IngressBytesPerThroughputUnit = 1_000_000;

    /// <summary>Ingress events per second admitted by one throughput unit.</summary>
    public const int IngressEventsPerThroughputUnit = 1_000;

    /// <summary>Egress bytes per second admitted by one throughput unit.</summary>
    public const int EgressBytesPerThroughputUnit = 2_000_000;

    /// <summary>Ingress bytes per second one partition sustains.</summary>
    public const int IngressBytesPerPartition = 1_000_000;

    /// <summary>Maximum throughput units on a Standard-tier namespace.</summary>
    public const int MaximumThroughputUnits = 40;

    /// <summary>Maximum partitions on a Standard-tier hub.</summary>
    public const int MaximumPartitions = 32;

    /// <summary>Sizes a hub for a measured ingest profile.</summary>
    /// <param name="profile">The workload's peak shape.</param>
    /// <returns>The throughput units and partitions the workload needs.</returns>
    public static CapacityPlan Size(IngestProfile profile) =>
        // GAP 10 — A throughput unit is bounded by BYTES and by EVENT COUNT at
        // the same time, and the binding limit is whichever needs more units.
        // Sizing on megabytes alone is how a 200-byte-per-event workload gets
        // throttled at a fifth of its planned rate.
        //
        // GAP 11 — Every consumer group reads the WHOLE stream, so egress is
        // multiplied by IndependentReaderCount while ingress is not. A second
        // consumer group is not free. Divide the multiplied egress by
        // EgressBytesPerThroughputUnit and take the maximum of the three.
        //
        // GAP 12 — Partitions are bounded from below by throughput AND by
        // ConcurrentProcessorCount. A hub with fewer partitions than processors
        // leaves processors permanently idle: a partition has exactly one owner
        // per consumer group.
        //
        // Round every division UP, clamp to MaximumThroughputUnits and
        // MaximumPartitions, and report LimitedBy as "ingress bytes",
        // "event count", or "egress across consumer groups".
        throw new NotImplementedException(
            "GAP 10-12: implement CapacityPlanner.Size. See "
            + "lessons/08-event-hubs-model/README.md#capacity-is-two-limits-not-one.");

    /// <summary>Says whether a running hub can absorb a proposed change.</summary>
    /// <param name="change">What somebody wants to change.</param>
    /// <param name="tier">The tier the namespace runs on.</param>
    /// <returns>The verdict and its consequence.</returns>
    public static ChangeVerdict CanChange(HubChange change, EventHubsTier tier) =>
        // GAP 13 — Exactly one of these decisions is permanent on the tier this
        // course uses, and it is the one made first and understood last.
        //
        //   ChangeThroughputUnits  → allowed on every tier; a namespace dial.
        //   ChangeRetention        → allowed, except Basic, which is fixed at
        //                            one day, so the change is a tier change.
        //   AddConsumerGroup       → allowed, except Basic, which allows
        //                            exactly one ($Default). Each group
        //                            multiplies egress.
        //   IncreasePartitionCount → Premium only, and it REMAPS keys to
        //                            partitions. Basic and Standard fix the
        //                            count at creation: the only route is a new
        //                            hub and a migration that re-reads.
        //   DecreasePartitionCount → never; a partition is a durable log.
        //
        // Each Consequence must say what actually happens; the evaluator reads
        // it.
        throw new NotImplementedException(
            "GAP 13: implement CapacityPlanner.CanChange. See "
            + "lessons/08-event-hubs-model/README.md#what-you-cannot-change-afterwards.");
}
