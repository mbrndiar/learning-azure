using LearningAzure.Exercises.CosmosModeling;

namespace LearningAzure.Exercises.CosmosModeling.Tests;

/// <summary>
/// Checks the arithmetic that lets two data models be compared on paper, before
/// either of them costs anything.
/// </summary>
public sealed class QueryCostModelTests
{
    [Fact]
    public void APointReadIsTheUnit() =>
        Assert.Equal(1.0, QueryCostModel.PointReadRequestUnits);

    [Fact]
    public void AQueryThatReturnsWhatItExaminedHasAmplificationOfOne() =>
        Assert.Equal(1.0, QueryCostModel.ReadAmplification(25, 25), 6);

    [Fact]
    public void AQueryThatExaminesEightTimesWhatItReturnsSaysSo() =>
        Assert.Equal(8.0, QueryCostModel.ReadAmplification(25, 200), 6);

    [Fact]
    public void AQueryThatReturnsNothingStillReportsTheWorkItDid()
    {
        // The most expensive query an application can run is the scan that
        // finds nothing. Reporting 1.0, or infinity, hides it.
        Assert.Equal(10_000.0, QueryCostModel.ReadAmplification(0, 10_000), 6);
    }

    [Fact]
    public void AQueryThatExaminedNothingAndReturnedNothingIsFree() =>
        Assert.Equal(0.0, QueryCostModel.ReadAmplification(0, 0), 6);

    [Fact]
    public void AmplificationRefusesNegativeCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryCostModel.ReadAmplification(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryCostModel.ReadAmplification(10, -1));
    }

    [Fact]
    public void AKeyedQueryTouchesExactlyOnePartition()
    {
        var model = new QueryCostModel(physicalPartitions: 6);
        var pattern = new AccessPattern("readings for a station", true, 25, 1);

        Assert.Equal(1, model.Estimate(pattern, 25).PartitionsTouched);
    }

    [Fact]
    public void AnUnkeyedQueryTouchesEveryPhysicalPartition()
    {
        var model = new QueryCostModel(physicalPartitions: 6);
        var pattern = new AccessPattern("readings above a threshold", false, 25, 1);

        Assert.Equal(6, model.Estimate(pattern, 25).PartitionsTouched);
    }

    [Fact]
    public void TheKeyedEstimateIsOverheadPlusDocuments()
    {
        var model = new QueryCostModel(physicalPartitions: 6);
        var pattern = new AccessPattern("keyed", true, 25, 1);

        // 2.5 for the one partition, plus 0.1 per document examined.
        Assert.Equal(5.0, model.Estimate(pattern, 25).RequestUnits, 6);
    }

    [Fact]
    public void FanOutMultipliesTheOverheadOnly()
    {
        var model = new QueryCostModel(physicalPartitions: 6);
        var pattern = new AccessPattern("unkeyed", false, 25, 1);

        // 2.5 x 6 partitions = 15, plus 0.1 x 25 documents = 2.5.
        Assert.Equal(17.5, model.Estimate(pattern, 25).RequestUnits, 6);
    }

    [Fact]
    public void FanOutDoesNotMultiplyTheDocuments()
    {
        // The documents exist once, wherever they live. If they were multiplied
        // too, this would come out at 2.5 x 6 + 0.1 x 25 x 6 = 30.
        var model = new QueryCostModel(physicalPartitions: 6);
        var pattern = new AccessPattern("unkeyed", false, 25, 1);

        Assert.NotEqual(30.0, model.Estimate(pattern, 25).RequestUnits, 6);
    }

    [Fact]
    public void ACrossPartitionQueryReturningOneDocumentIsStillExpensive()
    {
        var model = new QueryCostModel(physicalPartitions: 20);
        var needle = new AccessPattern("find one", false, 1, 1);
        var keyed = new AccessPattern("read one", true, 1, 1);

        Assert.True(model.Estimate(needle, 1).RequestUnits > model.Estimate(keyed, 1).RequestUnits * 5);
    }

    [Fact]
    public void GrowthInPartitionsMakesUnkeyedQueriesWorseAndKeyedOnesUnchanged()
    {
        var small = new QueryCostModel(physicalPartitions: 2);
        var large = new QueryCostModel(physicalPartitions: 40);

        var keyed = new AccessPattern("keyed", true, 10, 1);
        var unkeyed = new AccessPattern("unkeyed", false, 10, 1);

        Assert.Equal(
            small.Estimate(keyed, 10).RequestUnits,
            large.Estimate(keyed, 10).RequestUnits,
            6);

        Assert.True(large.Estimate(unkeyed, 10).RequestUnits > small.Estimate(unkeyed, 10).RequestUnits);
    }

    [Fact]
    public void TheEstimateCarriesTheDocumentsItWasGiven()
    {
        var model = new QueryCostModel(physicalPartitions: 3);

        Assert.Equal(77, model.Estimate(new AccessPattern("q", true, 5, 1), 77).DocumentsExamined);
    }

    [Fact]
    public void EstimatingRefusesANullPattern()
    {
        var model = new QueryCostModel(physicalPartitions: 3);

        Assert.Throws<ArgumentNullException>(() => model.Estimate(null!, 10));
    }

    [Fact]
    public void EstimatingRefusesANegativeDocumentCount()
    {
        var model = new QueryCostModel(physicalPartitions: 3);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => model.Estimate(new AccessPattern("q", true, 1, 1), -1));
    }

    [Fact]
    public void AContainerWithNoPartitionsIsNotAContainer() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryCostModel(0));

    [Fact]
    public void TheModelRemembersItsPartitionCount() =>
        Assert.Equal(7, new QueryCostModel(7).PhysicalPartitions);

    [Fact]
    public void AWorkloadIsPricedByFrequency()
    {
        var model = new QueryCostModel(physicalPartitions: 4);

        // 5 RU x 100/s = 500 RU/s.
        var hot = new AccessPattern("hot", true, 25, 100);

        Assert.Equal(500.0, model.RequestUnitsPerSecond([(hot, 25)]), 6);
    }

    [Fact]
    public void ARareExpensiveQueryCostsLessPerSecondThanACommonCheapOne()
    {
        var model = new QueryCostModel(physicalPartitions: 10);

        var report = new AccessPattern("hourly report", false, 5_000, 1.0 / 3_600);
        var read = new AccessPattern("read a station", true, 25, 200);

        var reportCost = model.RequestUnitsPerSecond([(report, 100_000)]);
        var readCost = model.RequestUnitsPerSecond([(read, 25)]);

        Assert.True(readCost > reportCost);
    }

    [Fact]
    public void AnEmptyWorkloadCostsNothing()
    {
        var model = new QueryCostModel(physicalPartitions: 4);

        Assert.Equal(0.0, model.RequestUnitsPerSecond([]), 6);
    }

    [Fact]
    public void AWorkloadIsTheSumOfItsPatterns()
    {
        var model = new QueryCostModel(physicalPartitions: 4);

        var one = new AccessPattern("one", true, 10, 10);
        var two = new AccessPattern("two", true, 10, 20);

        var separate = model.RequestUnitsPerSecond([(one, 10)])
            + model.RequestUnitsPerSecond([(two, 10)]);

        Assert.Equal(separate, model.RequestUnitsPerSecond([(one, 10), (two, 10)]), 6);
    }

    [Fact]
    public void PricingAWorkloadRefusesANullSequence()
    {
        var model = new QueryCostModel(physicalPartitions: 4);

        Assert.Throws<ArgumentNullException>(() => model.RequestUnitsPerSecond(null!));
    }
}
