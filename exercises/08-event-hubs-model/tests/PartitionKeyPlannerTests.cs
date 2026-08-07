namespace LearningAzure.Exercises.EventHubsModel.Tests;

public sealed class PartitionKeyPlannerTests
{
    private static readonly string[] SameLengthKeys = ["alpha", "bravo", "delta", "echo1", "foxtr"];

    private static readonly string[] FiveStations =
        ["station-01", "station-02", "station-03", "station-04", "station-05"];

    [Fact]
    public void ThePartitionKeyNamesTheStation()
    {
        var reading = Fixtures.Reading("station-bravo", 0);

        Assert.Equal("station-bravo", PartitionKeyPlanner.PartitionKeyFor(reading));
    }

    [Fact]
    public void TwoReadingsFromOneStationShareAKey()
    {
        var morning = PartitionKeyPlanner.PartitionKeyFor(Fixtures.Reading("station-bravo", 0));
        var evening = PartitionKeyPlanner.PartitionKeyFor(Fixtures.Reading("station-bravo", 600));

        Assert.Equal(morning, evening);
    }

    [Fact]
    public void ReadingsFromDifferentStationsDoNotShareAKey()
    {
        Assert.NotEqual(
            PartitionKeyPlanner.PartitionKeyFor(Fixtures.Reading("station-bravo", 0)),
            PartitionKeyPlanner.PartitionKeyFor(Fixtures.Reading("station-delta", 0)));
    }

    [Fact]
    public void TheKeyDoesNotVaryWithTheInstant()
    {
        // Keying on time is the classic mistake: it spreads one station across
        // every partition and silently discards the ordering guarantee the key
        // exists to buy.
        var keys = Enumerable
            .Range(0, 50)
            .Select(minute => PartitionKeyPlanner.PartitionKeyFor(Fixtures.Reading("station-bravo", minute)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(keys);
    }

    [Fact]
    public void EveryProducedKeyIsUsable()
    {
        Assert.True(PartitionKeyPlanner.IsUsableKey(
            PartitionKeyPlanner.PartitionKeyFor(Fixtures.Reading("station-bravo", 0))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyKeyIsNotAKey(string? candidate)
    {
        Assert.False(PartitionKeyPlanner.IsUsableKey(candidate));
    }

    [Fact]
    public void AKeyOfExactlyTheMaximumLengthIsUsable()
    {
        Assert.True(PartitionKeyPlanner.IsUsableKey(new string('s', PartitionKeyPlanner.MaximumKeyBytes)));
    }

    [Fact]
    public void AnAsciiKeyOneByteOverTheMaximumIsNot()
    {
        Assert.False(PartitionKeyPlanner.IsUsableKey(new string('s', PartitionKeyPlanner.MaximumKeyBytes + 1)));
    }

    [Fact]
    public void TheLimitCountsUtf8BytesNotUtf16Characters()
    {
        Assert.True(PartitionKeyPlanner.IsUsableKey(new string('é', 64)));
        Assert.False(PartitionKeyPlanner.IsUsableKey(new string('é', 65)));
    }

    [Fact]
    public void APartitionIndexIsAlwaysInRange()
    {
        for (var partitionCount = 1; partitionCount <= 32; partitionCount++)
        {
            for (var station = 0; station < 200; station++)
            {
                var index = PartitionKeyPlanner.PartitionFor($"station-{station:D3}", partitionCount);

                Assert.InRange(index, 0, partitionCount - 1);
            }
        }
    }

    [Fact]
    public void TheSameKeyAlwaysLandsOnTheSamePartition()
    {
        var first = PartitionKeyPlanner.PartitionFor("station-bravo", 4);
        var again = PartitionKeyPlanner.PartitionFor("station-bravo", 4);

        Assert.Equal(first, again);
    }

    [Fact]
    public void ThePartitionMappingSurvivesARestart()
    {
        // string.GetHashCode() is randomized per process, so a mapping built on
        // it is stable within one run and different in the next. This check
        // pins the mapping to values recorded from a DIFFERENT process, which
        // is the only way an in-process test can detect the difference.
        //
        // Recorded from FNV-1a over the UTF-8 bytes, modulo 4.
        Assert.Equal(1, PartitionKeyPlanner.PartitionFor("station-01", 4));
        Assert.Equal(0, PartitionKeyPlanner.PartitionFor("station-02", 4));
        Assert.Equal(3, PartitionKeyPlanner.PartitionFor("station-03", 4));
        Assert.Equal(2, PartitionKeyPlanner.PartitionFor("station-04", 4));
        Assert.Equal(1, PartitionKeyPlanner.PartitionFor("station-05", 4));
    }

    [Fact]
    public void ThePartitionMappingIsNotJustTheKeyLength()
    {
        // Two keys of equal length must not be forced onto one partition.
        var indexes = SameLengthKeys
            .Select(key => PartitionKeyPlanner.PartitionFor(key, 8))
            .Distinct()
            .ToArray();

        Assert.True(indexes.Length > 1, "a length-only hash puts every same-length key on one partition");
    }

    [Fact]
    public void AKeyIsRequiredToPlaceIt()
    {
        Assert.ThrowsAny<ArgumentException>(() => PartitionKeyPlanner.PartitionFor("", 4));
    }

    [Fact]
    public void APartitionCountBelowOneIsRejected()
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => PartitionKeyPlanner.PartitionFor("station-01", 0));
    }

    [Fact]
    public void FiveKeysOverFourPartitionsDoNotSpreadEvenly()
    {
        // Five stations over four partitions cannot be even, whatever the hash
        // is: some partition carries two. The companion's live run left one
        // partition idle entirely; this model leaves none idle and one carrying
        // double. Both are the same fact — a small key set does not balance.
        var skew = PartitionKeyPlanner.Spread(FiveStations, 4);

        Assert.Equal(5, skew.KeyCount);
        Assert.Equal(4, skew.PartitionCount);
        Assert.Equal(5, skew.KeysPerPartition.Sum());
        Assert.Equal(2, skew.BusiestPartition);
        Assert.Equal(1.6, skew.SkewFactor, 3);
    }

    [Fact]
    public void RepeatingAKeyDoesNotSpreadItFurther()
    {
        var once = PartitionKeyPlanner.Spread(["station-01", "station-02"], 4);
        var repeatedly = PartitionKeyPlanner.Spread(
            ["station-01", "station-01", "station-02", "station-02", "station-01"],
            4);

        Assert.Equal(once.KeyCount, repeatedly.KeyCount);
        Assert.Equal<IEnumerable<int>>(once.KeysPerPartition, repeatedly.KeysPerPartition);
    }

    [Fact]
    public void OneKeyOverManyPartitionsIsTotallySkewed()
    {
        var skew = PartitionKeyPlanner.Spread(["all-stations"], 32);

        Assert.Equal(31, skew.EmptyPartitions);
        Assert.Equal(1, skew.BusiestPartition);
        Assert.Equal(32.0, skew.SkewFactor, 3);
    }

    [Fact]
    public void ManyKeysSpreadAcrossEveryPartition()
    {
        var keys = Enumerable.Range(0, 500).Select(index => $"station-{index:D4}").ToArray();

        var skew = PartitionKeyPlanner.Spread(keys, 8);

        Assert.Equal(0, skew.EmptyPartitions);
        Assert.True(skew.SkewFactor < 1.3, $"500 keys over 8 partitions skewed {skew.SkewFactor:F2}x");
    }

    [Fact]
    public void TheSkewReportNamesTheIdlePartitions()
    {
        var skew = PartitionKeyPlanner.Spread(["station-01"], 4);

        Assert.Contains("3 idle", PartitionKeyPlanner.Describe(skew), StringComparison.Ordinal);
    }
}
