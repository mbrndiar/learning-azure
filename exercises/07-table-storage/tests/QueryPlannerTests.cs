namespace LearningAzure.Exercises.TableStorage.Tests;

public sealed class QueryPlannerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BothKeysKnownIsAPointRead()
    {
        var intent = new LookupIntent("station-bravo", Noon, null);

        Assert.Equal(QueryShape.PointRead, QueryPlanner.Classify(intent));
    }

    [Fact]
    public void OnlyTheStationKnownIsAPartitionScan()
    {
        var intent = new LookupIntent("station-bravo", null, null);

        Assert.Equal(QueryShape.PartitionScan, QueryPlanner.Classify(intent));
    }

    [Fact]
    public void AStationAndATimeRangeIsAPartitionScan()
    {
        var intent = new LookupIntent("station-bravo", null, Noon.AddHours(-2));

        Assert.Equal(QueryShape.PartitionScan, QueryPlanner.Classify(intent));
    }

    [Fact]
    public void ATimeRangeWithoutAStationIsATableScan()
    {
        var intent = new LookupIntent(null, null, Noon.AddHours(-2));

        Assert.Equal(QueryShape.TableScan, QueryPlanner.Classify(intent));
    }

    [Fact]
    public void AnExactInstantWithoutAStationIsStillATableScan()
    {
        // The row key alone does not narrow anything: the service has no index
        // that spans partitions.
        var intent = new LookupIntent(null, Noon, null);

        Assert.Equal(QueryShape.TableScan, QueryPlanner.Classify(intent));
    }

    [Fact]
    public void AnEmptyIntentIsATableScan()
    {
        Assert.Equal(QueryShape.TableScan, QueryPlanner.Classify(new LookupIntent(null, null, null)));
    }

    [Fact]
    public void AWhitespaceStationDoesNotCountAsKnown()
    {
        Assert.Equal(QueryShape.TableScan, QueryPlanner.Classify(new LookupIntent("   ", null, null)));
    }

    [Fact]
    public void ClassifyRejectsANullIntent()
    {
        Assert.Throws<ArgumentNullException>(() => QueryPlanner.Classify(null!));
    }

    [Fact]
    public void APointReadFiltersOnBothKeyNames()
    {
        var filter = QueryPlanner.BuildFilter(new LookupIntent("station-bravo", Noon, null));

        Assert.Contains("PartitionKey eq", filter, StringComparison.Ordinal);
        Assert.Contains("RowKey eq", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void APointReadFilterCarriesTheComputedKeys()
    {
        var filter = QueryPlanner.BuildFilter(new LookupIntent("station-bravo", Noon, null));

        Assert.Contains(ObservationKeys.PartitionKeyFor("station-bravo", Noon), filter, StringComparison.Ordinal);
        Assert.Contains(ObservationKeys.RowKeyFor(Noon), filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeFilterUsesARowKeyInequalityNotAPropertyOne()
    {
        var filter = QueryPlanner.BuildFilter(new LookupIntent("station-bravo", null, Noon));

        Assert.Contains("RowKey ge", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservedAt", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeFilterAlsoPinsThePartition()
    {
        var filter = QueryPlanner.BuildFilter(new LookupIntent("station-bravo", null, Noon));

        Assert.Contains("PartitionKey eq", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyIntentProducesAnEmptyFilter()
    {
        Assert.Equal(string.Empty, QueryPlanner.BuildFilter(new LookupIntent(null, null, null)));
    }

    [Fact]
    public void BuildFilterRejectsANullIntent()
    {
        Assert.Throws<ArgumentNullException>(() => QueryPlanner.BuildFilter(null!));
    }

    [Fact]
    public void EveryFilterThatClassifiesAsAPointReadPinsBothKeys()
    {
        LookupIntent[] intents =
        [
            new("station-bravo", Noon, null),
            new("station-delta", Noon.AddDays(3), Noon),
        ];

        foreach (var intent in intents)
        {
            Assert.Equal(QueryShape.PointRead, QueryPlanner.Classify(intent));
            var filter = QueryPlanner.BuildFilter(intent);
            Assert.Contains("PartitionKey eq", filter, StringComparison.Ordinal);
            Assert.Contains("RowKey eq", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void APointReadScansExactlyOneEntity()
    {
        var cost = QueryPlanner.Measure(QueryShape.PointRead, tableSize: 1_000_000, partitionSize: 1440, matched: 1);

        Assert.Equal(1, cost.EntitiesScanned);
    }

    [Fact]
    public void APointReadCostsTheSameOnATableAThousandTimesLarger()
    {
        var small = QueryPlanner.Measure(QueryShape.PointRead, 1_000, 100, 1);
        var large = QueryPlanner.Measure(QueryShape.PointRead, 1_000_000, 100_000, 1);

        Assert.Equal(small.EntitiesScanned, large.EntitiesScanned);
    }

    [Fact]
    public void APartitionScanCostsThePartitionNotTheTable()
    {
        var cost = QueryPlanner.Measure(QueryShape.PartitionScan, 1_000_000, 1440, 12);

        Assert.Equal(1440, cost.EntitiesScanned);
    }

    [Fact]
    public void ATableScanCostsTheWholeTable()
    {
        var cost = QueryPlanner.Measure(QueryShape.TableScan, 1_000_000, 1440, 12);

        Assert.Equal(1_000_000, cost.EntitiesScanned);
    }

    [Fact]
    public void ScannedNeverDropsBelowReturned()
    {
        var cost = QueryPlanner.Measure(QueryShape.PartitionScan, 1_000, 500, 500);

        Assert.True(cost.EntitiesScanned >= cost.EntitiesReturned);
    }

    [Fact]
    public void WasteIsOneWhenEverythingScannedWasWanted()
    {
        var cost = QueryPlanner.Measure(QueryShape.PartitionScan, 1_000, 500, 500);

        Assert.Equal(1.0, cost.Waste);
    }

    [Fact]
    public void WasteMakesTheTableScanPenaltyVisible()
    {
        var point = QueryPlanner.Measure(QueryShape.PointRead, 1_000_000, 1440, 1);
        var scan = QueryPlanner.Measure(QueryShape.TableScan, 1_000_000, 1440, 1);

        Assert.Equal(1.0, point.Waste);
        Assert.Equal(1_000_000.0, scan.Waste);
    }

    [Fact]
    public void AQueryThatMatchesNothingStillCostsWhatItScanned()
    {
        var cost = QueryPlanner.Measure(QueryShape.TableScan, 50_000, 100, 0);

        Assert.Equal(50_000, cost.EntitiesScanned);
        Assert.Equal(50_000.0, cost.Waste);
    }

    [Fact]
    public void MeasureRejectsAPartitionLargerThanItsTable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QueryPlanner.Measure(QueryShape.PartitionScan, 10, 100, 1));
    }

    [Fact]
    public void MeasureRejectsNegativeSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QueryPlanner.Measure(QueryShape.TableScan, -1, 0, 0));
    }

    [Fact]
    public void TheCostRecordCarriesTheShapeItMeasured()
    {
        Assert.Equal(
            QueryShape.PartitionScan,
            QueryPlanner.Measure(QueryShape.PartitionScan, 100, 10, 1).Shape);
    }
}
