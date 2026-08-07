namespace LearningAzure.Exercises.EventHubsModel.Tests;

public sealed class CapacityPlannerTests
{
    [Fact]
    public void ASmallWorkloadNeedsOneThroughputUnit()
    {
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 500,
            AverageEventBytes: 400,
            IndependentReaderCount: 1,
            ConcurrentProcessorCount: 1));

        Assert.Equal(1, plan.ThroughputUnits);
    }

    [Fact]
    public void EventCountCanBindBeforeBytesDo()
    {
        // 5,000 events per second at 200 bytes is 1 MB/s — one unit by bytes,
        // five by event count. Sizing on megabytes alone throttles this
        // workload at a fifth of its planned rate.
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 5_000,
            AverageEventBytes: 200,
            IndependentReaderCount: 1,
            ConcurrentProcessorCount: 1));

        Assert.Equal(5, plan.ThroughputUnits);
        Assert.Equal("event count", plan.LimitedBy);
    }

    [Fact]
    public void BytesCanBindBeforeEventCountDoes()
    {
        // 500 events per second at 20 KB is 10 MB/s: ten units by bytes, one by
        // event count.
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 500,
            AverageEventBytes: 20_000,
            IndependentReaderCount: 1,
            ConcurrentProcessorCount: 1));

        Assert.Equal(10, plan.ThroughputUnits);
        Assert.Equal("ingress bytes", plan.LimitedBy);
    }

    [Fact]
    public void EverySizingRoundsUp()
    {
        // 1.2 MB/s does not fit in one unit. Rounding to the nearest is how a
        // namespace is provisioned 20% short.
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 600,
            AverageEventBytes: 2_000,
            IndependentReaderCount: 1,
            ConcurrentProcessorCount: 1));

        Assert.Equal(2, plan.ThroughputUnits);
    }

    [Fact]
    public void AConsumerGroupIsNotFree()
    {
        // 4 MB/s in, read by five consumer groups, is 20 MB/s out: ten units
        // for egress against four for ingress.
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 200,
            AverageEventBytes: 20_000,
            IndependentReaderCount: 5,
            ConcurrentProcessorCount: 1));

        Assert.Equal(10, plan.ThroughputUnits);
        Assert.Equal("egress across consumer groups", plan.LimitedBy);
    }

    [Fact]
    public void OneReaderDoesNotMultiplyEgress()
    {
        var one = CapacityPlanner.Size(new IngestProfile(200, 20_000, 1, 1));
        var five = CapacityPlanner.Size(new IngestProfile(200, 20_000, 5, 1));

        Assert.True(five.ThroughputUnits > one.ThroughputUnits);
    }

    [Fact]
    public void PartitionsCoverTheIngestRate()
    {
        // 6 MB/s over partitions that sustain 1 MB/s each.
        var plan = CapacityPlanner.Size(new IngestProfile(300, 20_000, 1, 1));

        Assert.Equal(6, plan.Partitions);
    }

    [Fact]
    public void PartitionsAreNeverFewerThanProcessors()
    {
        // A partition has exactly one owner per consumer group, so a hub with
        // fewer partitions than processors leaves processors permanently idle.
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 100,
            AverageEventBytes: 400,
            IndependentReaderCount: 1,
            ConcurrentProcessorCount: 8));

        Assert.Equal(8, plan.Partitions);
    }

    [Fact]
    public void ThroughputStillWinsWhenItNeedsMorePartitions()
    {
        var plan = CapacityPlanner.Size(new IngestProfile(600, 20_000, 1, 4));

        Assert.Equal(12, plan.Partitions);
    }

    [Fact]
    public void TheStandardTierCeilingsAreRespected()
    {
        var plan = CapacityPlanner.Size(new IngestProfile(
            EventsPerSecond: 100_000,
            AverageEventBytes: 20_000,
            IndependentReaderCount: 4,
            ConcurrentProcessorCount: 200));

        Assert.Equal(CapacityPlanner.MaximumThroughputUnits, plan.ThroughputUnits);
        Assert.Equal(CapacityPlanner.MaximumPartitions, plan.Partitions);
    }

    [Theory]
    [InlineData(0, 400, 1, 1)]
    [InlineData(100, 0, 1, 1)]
    [InlineData(100, 400, 0, 1)]
    [InlineData(100, 400, 1, 0)]
    public void ANonsensicalProfileIsRejected(int events, int bytes, int readers, int processors)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(
            () => CapacityPlanner.Size(new IngestProfile(events, bytes, readers, processors)));
    }

    [Fact]
    public void AProfileIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => CapacityPlanner.Size(null!));
    }

    [Theory]
    [InlineData(EventHubsTier.Basic)]
    [InlineData(EventHubsTier.Standard)]
    [InlineData(EventHubsTier.Premium)]
    public void ThroughputUnitsAreAlwaysAdjustable(EventHubsTier tier)
    {
        Assert.True(CapacityPlanner.CanChange(HubChange.ChangeThroughputUnits, tier).AllowedInPlace);
    }

    [Fact]
    public void StandardRetentionIsAdjustable()
    {
        Assert.True(CapacityPlanner.CanChange(HubChange.ChangeRetention, EventHubsTier.Standard).AllowedInPlace);
    }

    [Fact]
    public void BasicAllowsOnlyOneConsumerGroup()
    {
        var verdict = CapacityPlanner.CanChange(HubChange.AddConsumerGroup, EventHubsTier.Basic);

        Assert.False(verdict.AllowedInPlace);
    }

    [Fact]
    public void StandardAllowsMoreConsumerGroups()
    {
        Assert.True(CapacityPlanner.CanChange(HubChange.AddConsumerGroup, EventHubsTier.Standard).AllowedInPlace);
    }

    [Fact]
    public void ThePartitionCountIsFixedOnStandard()
    {
        // This is the decision made on day one and understood on day four
        // hundred. There is no flag, no support ticket, and no scale operation.
        var verdict = CapacityPlanner.CanChange(HubChange.IncreasePartitionCount, EventHubsTier.Standard);

        Assert.False(verdict.AllowedInPlace);
        Assert.Contains("new hub", verdict.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PremiumAllowsAnIncreaseAndSaysWhatItCosts()
    {
        var verdict = CapacityPlanner.CanChange(HubChange.IncreasePartitionCount, EventHubsTier.Premium);

        Assert.True(verdict.AllowedInPlace);

        // An increase remaps keys to partitions, so ordering across the change
        // is not preserved. A verdict that says "yes" without saying that is
        // the dangerous half of the answer.
        Assert.Contains("order", verdict.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EventHubsTier.Basic)]
    [InlineData(EventHubsTier.Standard)]
    [InlineData(EventHubsTier.Premium)]
    public void NoTierAllowsFewerPartitions(EventHubsTier tier)
    {
        Assert.False(CapacityPlanner.CanChange(HubChange.DecreasePartitionCount, tier).AllowedInPlace);
    }

    [Fact]
    public void EveryVerdictExplainsItself()
    {
        foreach (var change in Enum.GetValues<HubChange>())
        {
            foreach (var tier in Enum.GetValues<EventHubsTier>())
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(CapacityPlanner.CanChange(change, tier).Consequence),
                    $"{change} on {tier} has no consequence recorded");
            }
        }
    }
}
