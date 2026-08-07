using LearningAzure.Exercises.CosmosModeling;

namespace LearningAzure.Exercises.CosmosModeling.Tests;

/// <summary>
/// Checks the difference between a throughput number that adds up and a
/// throughput number that can actually be spent.
/// </summary>
public sealed class ThroughputPlannerTests
{
    [Fact]
    public void ThroughputIsDividedEvenlyAcrossPhysicalPartitions()
    {
        var plan = new ThroughputPlan(400, IsAutoscale: false, PhysicalPartitions: 4);

        Assert.Equal(100.0, ThroughputPlanner.PerPhysicalPartition(plan), 6);
    }

    [Fact]
    public void OnePhysicalPartitionGetsTheWholeAllocation()
    {
        var plan = new ThroughputPlan(400, IsAutoscale: false, PhysicalPartitions: 1);

        Assert.Equal(400.0, ThroughputPlanner.PerPhysicalPartition(plan), 6);
    }

    [Fact]
    public void SplittingAContainerHalvesWhatEachPartitionMaySpend()
    {
        var before = new ThroughputPlan(10_000, IsAutoscale: false, PhysicalPartitions: 2);
        var after = new ThroughputPlan(10_000, IsAutoscale: false, PhysicalPartitions: 4);

        Assert.Equal(
            ThroughputPlanner.PerPhysicalPartition(before) / 2,
            ThroughputPlanner.PerPhysicalPartition(after),
            6);
    }

    [Fact]
    public void DivisionRefusesANullPlan() =>
        Assert.Throws<ArgumentNullException>(() => ThroughputPlanner.PerPhysicalPartition(null!));

    [Fact]
    public void AWorkloadInsideBothBoundsIsNotThrottled()
    {
        var plan = new ThroughputPlan(10_000, IsAutoscale: false, PhysicalPartitions: 10);

        Assert.False(ThroughputPlanner.WillThrottle(plan, 800, 6_000));
    }

    [Fact]
    public void AWorkloadOverTheContainerTotalIsThrottled()
    {
        var plan = new ThroughputPlan(10_000, IsAutoscale: false, PhysicalPartitions: 10);

        Assert.True(ThroughputPlanner.WillThrottle(plan, 500, 12_000));
    }

    [Fact]
    public void AHotPartitionIsThrottledWhileTheContainerLooksIdle()
    {
        // The support case: provisioned 10,000, consuming 900, throttled.
        var plan = new ThroughputPlan(10_000, IsAutoscale: false, PhysicalPartitions: 10);

        Assert.True(ThroughputPlanner.WillThrottle(plan, 1_400, 900));
    }

    [Fact]
    public void ExactlyAtAPartitionsShareIsNotYetThrottled()
    {
        var plan = new ThroughputPlan(400, IsAutoscale: false, PhysicalPartitions: 4);

        Assert.False(ThroughputPlanner.WillThrottle(plan, 100, 400));
        Assert.True(ThroughputPlanner.WillThrottle(plan, 100.1, 400));
    }

    [Fact]
    public void AddingThroughputDoesNotHelpAHotPartitionOnceItSplits()
    {
        // Doubling RU/s while the partition count doubles leaves the hot
        // partition exactly where it was.
        var before = new ThroughputPlan(10_000, IsAutoscale: false, PhysicalPartitions: 10);
        var after = new ThroughputPlan(20_000, IsAutoscale: false, PhysicalPartitions: 20);

        Assert.True(ThroughputPlanner.WillThrottle(before, 1_400, 900));
        Assert.True(ThroughputPlanner.WillThrottle(after, 1_400, 900));
    }

    [Fact]
    public void ThrottlingRefusesANullPlan() =>
        Assert.Throws<ArgumentNullException>(() => ThroughputPlanner.WillThrottle(null!, 1, 1));

    [Fact]
    public void ThrottlingRefusesNegativeDemand()
    {
        var plan = new ThroughputPlan(400, IsAutoscale: false, PhysicalPartitions: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => ThroughputPlanner.WillThrottle(plan, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThroughputPlanner.WillThrottle(plan, 10, -1));
    }

    [Fact]
    public void TheAutoscaleFloorIsATenthOfTheMaximum() =>
        Assert.Equal(0.1, ThroughputPlanner.AutoscaleFloorFraction);

    [Fact]
    public void AnAutoscaleRequestUnitCostsHalfAsMuchAgain() =>
        Assert.Equal(1.5, ThroughputPlanner.AutoscalePriceMultiplier);

    [Fact]
    public void AFlatWorkloadIsCheaperOnManualThroughput()
    {
        // Running at the peak all day: autoscale bills the same usage at 1.5x.
        Assert.False(ThroughputPlanner.AutoscaleIsCheaper(1_000, [1_000]));
    }

    [Fact]
    public void ASpikyWorkloadIsCheaperOnAutoscale()
    {
        // One busy hour and 23 idle hours: every hour is billed separately.
        Assert.True(ThroughputPlanner.AutoscaleIsCheaper(
            10_000,
            [10_000, .. Enumerable.Repeat(0.0, 23)]));
    }

    [Fact]
    public void TheFloorStopsAutoscaleFromEverBeingFree()
    {
        // A workload that is idle almost all the time still bills 10% of the
        // peak, times 1.5: 15% of the manual bill, not the 0.15% that raw usage
        // would suggest.
        Assert.Equal(0.15, ThroughputPlanner.RelativeAutoscaleCost(10_000, [10]), 6);
    }

    [Fact]
    public void ACompletelyIdleWorkloadStillCostsFifteenPercent()
    {
        Assert.Equal(0.15, ThroughputPlanner.RelativeAutoscaleCost(10_000, [0]), 6);
    }

    [Fact]
    public void BelowTheFloorTheBillStopsFalling()
    {
        // Everything at or under 10% of the peak bills the same.
        Assert.Equal(
            ThroughputPlanner.RelativeAutoscaleCost(10_000, [0]),
            ThroughputPlanner.RelativeAutoscaleCost(10_000, [1_000]),
            6);

        // Above the floor it starts moving again.
        Assert.True(
            ThroughputPlanner.RelativeAutoscaleCost(10_000, [2_000])
            > ThroughputPlanner.RelativeAutoscaleCost(10_000, [1_000]));
    }

    [Fact]
    public void AFlatWorkloadCostsHalfAsMuchAgainOnAutoscale() =>
        Assert.Equal(1.5, ThroughputPlanner.RelativeAutoscaleCost(1_000, [1_000]), 6);

    [Fact]
    public void APeakOfZeroIsNotAWorkload() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThroughputPlanner.RelativeAutoscaleCost(0, [0]));

    [Fact]
    public void MovingTheSameUsageBetweenHoursChangesTheBill()
    {
        var oneBusyHour = ThroughputPlanner.RelativeAutoscaleCost(10_000, [10_000, 0]);
        var twoHalfBusyHours = ThroughputPlanner.RelativeAutoscaleCost(10_000, [5_000, 5_000]);

        Assert.True(oneBusyHour > twoHalfBusyHours);
    }

    [Fact]
    public void AnAverageAboveThePeakIsNotAWorkload() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThroughputPlanner.AutoscaleIsCheaper(1_000, [1_001]));

    [Fact]
    public void NegativeRatesAreNotWorkloads()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThroughputPlanner.AutoscaleIsCheaper(-1, [0]));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThroughputPlanner.AutoscaleIsCheaper(1_000, [-1]));
    }

    [Fact]
    public void MultipleWriteRegionsDoNotApplyTheSingleRegionMultiplier()
    {
        Assert.Equal(
            1.0,
            ThroughputPlanner.RelativeAutoscaleCost(1_000, [1_000], multipleWriteRegions: true),
            6);
    }

    [Fact]
    public void NoBilledHoursIsNotAWorkload()
    {
        Assert.Throws<ArgumentException>(
            () => ThroughputPlanner.RelativeAutoscaleCost(1_000, []));
    }
}
