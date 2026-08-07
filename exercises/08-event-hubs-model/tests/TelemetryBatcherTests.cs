namespace LearningAzure.Exercises.EventHubsModel.Tests;

public sealed class TelemetryBatcherTests
{
    private static readonly string[] FiveStations =
        ["station-01", "station-02", "station-03", "station-04", "station-05"];

    private static readonly int[] ExpectedCounts = [4, 4, 2];

    [Fact]
    public void EveryReadingIsBatched()
    {
        var readings = Fixtures.Readings(FiveStations, 20);
        var factory = new RecordingBatchFactory(1_048_576);

        var batches = TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        Assert.Equal(readings.Count, batches.Sum(batch => batch.Count));
    }

    [Fact]
    public void NoBatchMixesPartitionKeys()
    {
        var readings = Fixtures.Readings(FiveStations, 20);
        var factory = new RecordingBatchFactory(1_048_576);

        TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        // A batch carries one key for every event inside it. Reusing a single
        // "current batch" across stations is the whole failure.
        Assert.Equal(
            FiveStations.Length,
            factory.Created.Select(batch => batch.PartitionKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryBatchCarriesAKey()
    {
        var readings = Fixtures.Readings(FiveStations, 4);
        var factory = new RecordingBatchFactory(1_048_576);

        TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        Assert.All(factory.Created, batch => Assert.False(string.IsNullOrEmpty(batch.PartitionKey)));
    }

    [Fact]
    public void ReadingsForOneStationStayInSendOrder()
    {
        // Readings are interleaved across stations on the way in. Per station,
        // the sizes encode the order, so a batcher that reorders is caught.
        var readings = Enumerable
            .Range(0, 30)
            .Select(index => Fixtures.Reading("station-01", index, 200 + index))
            .ToArray();

        var factory = new RecordingBatchFactory(1_048_576);

        TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        var accepted = factory.Created.SelectMany(batch => batch.Bodies).ToArray();

        Assert.Equal<IEnumerable<int>>(readings.Select(reading => reading.BodyBytes), accepted);
    }

    [Fact]
    public void OneStationUnderTheBudgetProducesOneBatch()
    {
        var readings = Enumerable.Range(0, 10).Select(index => Fixtures.Reading("station-01", index)).ToArray();
        var factory = new RecordingBatchFactory(1_048_576);

        var batches = TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        Assert.Single(batches);
    }

    [Fact]
    public void AFullBatchIsClosedAndAnotherIsOpened()
    {
        // Exactly four 200-byte bodies (216 bytes each on the wire) fit in 900
        // bytes, so ten readings need three batches.
        var readings = Enumerable.Range(0, 10).Select(index => Fixtures.Reading("station-01", index)).ToArray();
        var factory = new RecordingBatchFactory(900);

        var batches = TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        Assert.Equal(3, batches.Count);
        Assert.Equal<IEnumerable<int>>(ExpectedCounts, batches.Select(batch => batch.Count));
    }

    [Fact]
    public void NothingIsLostWhenABatchFillsUp()
    {
        var readings = Fixtures.Readings(FiveStations, 40);
        var factory = new RecordingBatchFactory(900);

        var batches = TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        // The refused TryAdd must be retried against the new batch, not
        // dropped. Losing the refused event is the silent failure this whole
        // check exists for.
        Assert.Equal(readings.Count, batches.Sum(batch => batch.Count));
        Assert.True(factory.Created.Sum(batch => batch.Refusals) > 0, "the budget was never actually reached");
    }

    [Fact]
    public void NoBatchExceedsItsBudget()
    {
        var readings = Fixtures.Readings(FiveStations, 40);
        var factory = new RecordingBatchFactory(900);

        TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        Assert.All(factory.Created, batch => Assert.True(batch.SizeInBytes <= batch.MaximumSizeInBytes));
    }

    [Fact]
    public void NoEmptyBatchIsHandedBack()
    {
        var readings = Fixtures.Readings(FiveStations, 40);
        var factory = new RecordingBatchFactory(900);

        var batches = TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken);

        // An empty batch is a send that costs a round trip and moves nothing.
        Assert.All(batches, batch => Assert.True(batch.Count > 0));
    }

    [Fact]
    public void AnEventThatCannotEverFitIsReported()
    {
        var readings = new[] { Fixtures.Reading("station-01", 0, 5_000) };
        var factory = new RecordingBatchFactory(900);

        var failure = Assert.Throws<EventTooLargeException>(
            () => TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken));

        Assert.Equal(readings[0], failure.Reading);
    }

    [Fact]
    public void AnEventThatCannotEverFitIsNotRetriedForever()
    {
        // A batcher that treats "false" as "try again" loops until the process
        // is killed. Bounding the factory proves the loop terminated.
        var readings = new[] { Fixtures.Reading("station-01", 0, 5_000) };
        var factory = new RecordingBatchFactory(900);

        Assert.Throws<EventTooLargeException>(() => TelemetryBatcher.Pack(readings, factory.Factory, TestContext.Current.CancellationToken));

        Assert.True(factory.Created.Count <= 2, $"{factory.Created.Count} batches were opened for one event");
    }

    [Fact]
    public void AnEmptyInputProducesNoBatches()
    {
        var factory = new RecordingBatchFactory(1_048_576);

        Assert.Empty(TelemetryBatcher.Pack([], factory.Factory, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancellationIsHonouredMidPack()
    {
        // The token is cancelled before the call, so a batcher that checks it
        // only once still passes. Cancelling after the first batch is what
        // separates a checked loop from a decorative parameter.
        var readings = Fixtures.Readings(FiveStations, 200);
        using var cancellation = new CancellationTokenSource();
        var created = 0;

        EventBatchFactory factory = partitionKey =>
        {
            if (++created == 3)
            {
                cancellation.Cancel();
            }

            return new BudgetedBatch(partitionKey, 900);
        };

        Assert.Throws<OperationCanceledException>(
            () => TelemetryBatcher.Pack(readings, factory, cancellation.Token));
    }

    [Fact]
    public void AnAlreadyCancelledTokenStopsImmediately()
    {
        var readings = Fixtures.Readings(FiveStations, 20);
        var factory = new RecordingBatchFactory(1_048_576);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => TelemetryBatcher.Pack(readings, factory.Factory, cancellation.Token));

        Assert.Empty(factory.Created);
    }

    [Fact]
    public void ReadingsAreRequired()
    {
        var factory = new RecordingBatchFactory(1_048_576);

        Assert.Throws<ArgumentNullException>(() => TelemetryBatcher.Pack(null!, factory.Factory, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ABatchFactoryIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => TelemetryBatcher.Pack([], null!, TestContext.Current.CancellationToken));
    }
}
