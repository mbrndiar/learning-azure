namespace LearningAzure.Exercises.CosmosModeling;

/// <summary>
/// Works out whether a throughput allocation can actually serve a workload,
/// which is a different question from whether the totals add up.
/// </summary>
public static class ThroughputPlanner
{
    /// <summary>The fraction of an autoscale maximum that is always billed.</summary>
    public const double AutoscaleFloorFraction = 0.1;

    /// <summary>What an autoscale RU costs relative to a manual one.</summary>
    public const double AutoscalePriceMultiplier = 1.5;

    /// <summary>How much throughput each physical partition may spend.</summary>
    /// <param name="plan">The throughput allocation.</param>
    /// <returns>Request units per second available to one physical partition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public static double PerPhysicalPartition(ThroughputPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // GAP 9: divide the provisioned rate by the physical partition count.
        //
        // The number on the container is a total, and Cosmos splits it evenly
        // across physical partitions whether or not the traffic is even. A
        // partition cannot borrow from an idle neighbour, which is the whole
        // reason a hot partition throttles at 100 RU/s while the container
        // chart shows 400 RU/s provisioned and 120 consumed.
        // See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent
        throw new NotImplementedException(
            "GAP 9: implement ThroughputPlanner.PerPhysicalPartition. "
            + "See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent.");
    }

    /// <summary>
    /// Decides whether the busiest logical partition will be throttled, given
    /// what the container as a whole is provisioned for.
    /// </summary>
    /// <param name="plan">The throughput allocation.</param>
    /// <param name="hottestPartitionRequestUnitsPerSecond">Demand on the busiest partition.</param>
    /// <param name="totalRequestUnitsPerSecond">Demand on the whole container.</param>
    /// <returns>True when the container will return 429s.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A demand figure is negative.</exception>
    public static bool WillThrottle(
        ThroughputPlan plan,
        double hottestPartitionRequestUnitsPerSecond,
        double totalRequestUnitsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(hottestPartitionRequestUnitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRequestUnitsPerSecond);

        // GAP 10: there are two ceilings, and either one throttles.
        //
        // The container total is one. The busiest logical partition against its
        // physical partition's share is the other, and it is usually the one
        // that gets there first. Checking only the total produces the classic
        // support case: "we are provisioned for 10,000 RU/s, consuming 900, and
        // being throttled". Being exactly at a ceiling is not over it.
        throw new NotImplementedException(
            "GAP 10: implement ThroughputPlanner.WillThrottle. "
            + "See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent.");
    }

    /// <summary>
    /// Prices a workload under an autoscale allocation, relative to what the
    /// equivalent manual allocation would cost.
    /// </summary>
    /// <param name="peakRequestUnitsPerSecond">The highest rate the workload reaches.</param>
    /// <param name="averageRequestUnitsPerSecond">The rate it spends most of its time at.</param>
    /// <returns>
    /// The autoscale bill divided by the manual bill. Below 1.0 autoscale is
    /// cheaper; above 1.0 it is not.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">A rate is negative, the peak is zero, or the average exceeds the peak.</exception>
    public static double RelativeAutoscaleCost(
        double peakRequestUnitsPerSecond,
        double averageRequestUnitsPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peakRequestUnitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(averageRequestUnitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            averageRequestUnitsPerSecond,
            peakRequestUnitsPerSecond);

        // GAP 11: bill the average at AutoscalePriceMultiplier, but never below
        // AutoscaleFloorFraction of the peak — then express it as a fraction of
        // the manual bill, which is the peak.
        //
        // Manual provisioning charges for the peak every second of the day, so
        // it wins on flat load. Autoscale charges for what was used at 1.5x the
        // rate, so it wins on spiky load — but only down to 10% of the maximum,
        // which is why autoscale on a workload that is idle 23 hours a day
        // still costs 15% of the manual bill rather than nothing at all.
        // Forgetting the floor is what makes autoscale look free.
        // See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent
        throw new NotImplementedException(
            "GAP 11: implement ThroughputPlanner.RelativeAutoscaleCost. "
            + "See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent.");
    }

    /// <summary>Decides which allocation is cheaper for this shape of load.</summary>
    /// <param name="peakRequestUnitsPerSecond">The highest rate the workload reaches.</param>
    /// <param name="averageRequestUnitsPerSecond">The rate it spends most of its time at.</param>
    /// <returns>True when autoscale is strictly cheaper.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A rate is negative, the peak is zero, or the average exceeds the peak.</exception>
    public static bool AutoscaleIsCheaper(
        double peakRequestUnitsPerSecond,
        double averageRequestUnitsPerSecond) =>
        RelativeAutoscaleCost(peakRequestUnitsPerSecond, averageRequestUnitsPerSecond) < 1.0;
}
