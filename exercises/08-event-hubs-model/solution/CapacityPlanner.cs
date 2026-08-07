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
    public static CapacityPlan Size(IngestProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.EventsPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.AverageEventBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.IndependentReaderCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.ConcurrentProcessorCount, 1);

        var ingressBytesPerSecond = (long)profile.EventsPerSecond * profile.AverageEventBytes;

        // GAP 10 — A throughput unit is bounded by BYTES and by EVENT COUNT at
        // the same time, and the binding limit is whichever needs more units.
        // Sizing on megabytes alone is how a 200-byte-per-event workload gets
        // throttled at a fifth of its planned rate.
        var unitsForBytes = CeilingDivide(ingressBytesPerSecond, IngressBytesPerThroughputUnit);
        var unitsForEvents = CeilingDivide(profile.EventsPerSecond, IngressEventsPerThroughputUnit);

        // GAP 11 — Every consumer group reads the WHOLE stream, so egress is
        // multiplied by the number of readers while ingress is not. A second
        // consumer group is not free.
        var egressBytesPerSecond = ingressBytesPerSecond * profile.IndependentReaderCount;
        var unitsForEgress = CeilingDivide(egressBytesPerSecond, EgressBytesPerThroughputUnit);

        var units = Math.Max(unitsForBytes, Math.Max(unitsForEvents, unitsForEgress));

        var limitedBy = units == unitsForEvents && unitsForEvents > unitsForBytes && unitsForEvents >= unitsForEgress
            ? "event count"
            : units == unitsForEgress && unitsForEgress > unitsForBytes
                ? "egress across consumer groups"
                : "ingress bytes";

        // GAP 12 — Partitions are bounded from below by throughput AND by the
        // number of processors that must each own work. A hub with fewer
        // partitions than processors leaves processors permanently idle: a
        // partition has exactly one owner per consumer group.
        var partitionsForThroughput = CeilingDivide(ingressBytesPerSecond, IngressBytesPerPartition);
        var partitions = Math.Max(partitionsForThroughput, profile.ConcurrentProcessorCount);

        return new CapacityPlan(
            Math.Min(units, MaximumThroughputUnits),
            Math.Min(partitions, MaximumPartitions),
            limitedBy);
    }

    /// <summary>Says whether a running hub can absorb a proposed change.</summary>
    /// <param name="change">What somebody wants to change.</param>
    /// <param name="tier">The tier the namespace runs on.</param>
    /// <returns>The verdict and its consequence.</returns>
    public static ChangeVerdict CanChange(HubChange change, EventHubsTier tier)
    {
        // GAP 13 — Exactly one of these decisions is permanent on the tier this
        // course uses, and it is the one made first and understood last.
        return change switch
        {
            HubChange.ChangeThroughputUnits => new ChangeVerdict(
                true,
                "throughput units are a namespace dial and may be raised or lowered at any time"),

            HubChange.ChangeRetention => new ChangeVerdict(
                true,
                tier == EventHubsTier.Basic
                    ? "Basic retention is fixed at one day; the change is a tier change"
                    : "retention may be set from 1 to 7 days on Standard at any time"),

            HubChange.AddConsumerGroup => new ChangeVerdict(
                tier != EventHubsTier.Basic,
                tier == EventHubsTier.Basic
                    ? "Basic allows exactly one consumer group ($Default); adding one is a tier change"
                    : "consumer groups are added freely, but each one multiplies egress"),

            HubChange.IncreasePartitionCount => new ChangeVerdict(
                tier == EventHubsTier.Premium,
                tier == EventHubsTier.Premium
                    ? "Premium allows an increase, and it remaps keys to partitions: events for one "
                      + "key move, so relative order across the change is not preserved"
                    : "Basic and Standard fix the partition count at creation; the only route is a "
                      + "new hub and a migration that re-reads the stream"),

            HubChange.DecreasePartitionCount => new ChangeVerdict(
                false,
                "no tier allows a decrease; a partition is a durable log and removing it would "
                + "discard its events"),

            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
    }

    private static int CeilingDivide(long value, long divisor) => (int)((value + divisor - 1) / divisor);
}
