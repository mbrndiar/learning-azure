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

        // GAP 9: provisioned throughput is divided, not pooled.
        //
        // The number on the container is a total, and Cosmos splits it evenly
        // across physical partitions whether or not the traffic is even. A
        // partition cannot borrow from an idle neighbour, which is the whole
        // reason a hot partition throttles at 100 RU/s while the container
        // chart shows 400 RU/s provisioned and 120 consumed.
        // See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent
        return (double)plan.ProvisionedRequestUnits / plan.PhysicalPartitions;
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

        // GAP 10: either bound throttles, and the local one usually gets there first.
        //
        // Checking only the total is the mistake that produces the classic
        // support case: "we are provisioned for 10,000 RU/s and consuming 900,
        // and we are being throttled". Checking only the hottest partition
        // misses the ordinary case of a container that is simply too small.
        // Both are real ceilings and either one is enough.
        return totalRequestUnitsPerSecond > plan.ProvisionedRequestUnits
            || hottestPartitionRequestUnitsPerSecond > PerPhysicalPartition(plan);
    }

    /// <summary>
    /// Prices a workload under an autoscale allocation, relative to what the
    /// equivalent manual allocation would cost.
    /// </summary>
    /// <param name="autoscaleMaximumRequestUnitsPerSecond">The configured autoscale maximum.</param>
    /// <param name="hourlyPeakRequestUnitsPerSecond">
    /// The highest RU/s reached in each billed hour.
    /// </param>
    /// <param name="multipleWriteRegions">
    /// Whether the account uses the multiple-write-region meter, where manual
    /// and autoscale use the same per-RU rate.
    /// </param>
    /// <returns>
    /// The autoscale bill divided by the manual bill. Below 1.0 autoscale is
    /// cheaper; above 1.0 it is not.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The maximum or an hourly peak is invalid.</exception>
    /// <exception cref="ArgumentException">No billed hours were supplied.</exception>
    public static double RelativeAutoscaleCost(
        double autoscaleMaximumRequestUnitsPerSecond,
        IReadOnlyList<double> hourlyPeakRequestUnitsPerSecond,
        bool multipleWriteRegions = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(autoscaleMaximumRequestUnitsPerSecond);
        ArgumentNullException.ThrowIfNull(hourlyPeakRequestUnitsPerSecond);
        if (hourlyPeakRequestUnitsPerSecond.Count == 0)
        {
            throw new ArgumentException("At least one billed hour is required.", nameof(hourlyPeakRequestUnitsPerSecond));
        }

        // GAP 11: bill each hour's maximum, with the floor applied per hour.
        //
        // Averaging raw requests over a day erases the very peak that autoscale
        // bills. Each hour contributes max(hourly peak, 10% of Tmax). A
        // single-write-region autoscale meter is 1.5x the manual rate; the
        // multiple-write-region meters use the same per-RU rate.
        // See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent
        var billed = 0.0;
        foreach (var hourlyPeak in hourlyPeakRequestUnitsPerSecond)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(hourlyPeak);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                hourlyPeak,
                autoscaleMaximumRequestUnitsPerSecond);
            billed += Math.Max(
                hourlyPeak,
                autoscaleMaximumRequestUnitsPerSecond * AutoscaleFloorFraction);
        }

        var multiplier = multipleWriteRegions ? 1.0 : AutoscalePriceMultiplier;
        var manual = autoscaleMaximumRequestUnitsPerSecond * hourlyPeakRequestUnitsPerSecond.Count;
        return billed * multiplier / manual;
    }

    /// <summary>Decides which allocation is cheaper for this shape of load.</summary>
    /// <param name="autoscaleMaximumRequestUnitsPerSecond">The configured autoscale maximum.</param>
    /// <param name="hourlyPeakRequestUnitsPerSecond">The highest RU/s reached in each billed hour.</param>
    /// <param name="multipleWriteRegions">Whether the multiple-write-region meter applies.</param>
    /// <returns>True when autoscale is strictly cheaper.</returns>
    public static bool AutoscaleIsCheaper(
        double autoscaleMaximumRequestUnitsPerSecond,
        IReadOnlyList<double> hourlyPeakRequestUnitsPerSecond,
        bool multipleWriteRegions = false) =>
        RelativeAutoscaleCost(
            autoscaleMaximumRequestUnitsPerSecond,
            hourlyPeakRequestUnitsPerSecond,
            multipleWriteRegions) < 1.0;
}
