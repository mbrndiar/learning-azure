namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>
/// Lag is the number an on-call engineer looks at. These checks pin the two
/// ways it is usually computed wrongly: the unclaimed partition and the
/// negative clamp.
/// </summary>
public sealed class LagCalculatorTests
{
    private static readonly PartitionSnapshot[] FourPartitions =
    [
        new("0", 269),
        new("1", 739),
        new("2", 59),
        new("3", 149),
    ];

    [Fact]
    public void BothArgumentsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => LagCalculator.Measure(null!, new CheckpointLedger()));
        Assert.Throws<ArgumentNullException>(() => LagCalculator.Measure(FourPartitions, null!));
    }

    [Fact]
    public void ACaughtUpPartitionHasNoLag()
    {
        var ledger = new CheckpointLedger();
        ledger.Record("1", 739);

        var lag = LagCalculator.Measure([new PartitionSnapshot("1", 739)], ledger);

        Assert.Equal(0, lag.TotalLag);
        Assert.Equal(0, lag.PartitionsWithoutCheckpoint);
        Assert.True(lag.Partitions[0].HasCheckpoint);
    }

    [Fact]
    public void ABacklogIsTheDistanceToTheLastEnqueuedEvent()
    {
        var ledger = new CheckpointLedger();
        ledger.Record("1", 700);

        var lag = LagCalculator.Measure([new PartitionSnapshot("1", 739)], ledger);

        Assert.Equal(39, lag.TotalLag);
    }

    [Fact]
    public void APartitionWithNoCheckpointIsMaximallyBehind()
    {
        var lag = LagCalculator.Measure([new PartitionSnapshot("2", 59)], new CheckpointLedger());

        Assert.Equal(60, lag.TotalLag);
        Assert.Equal(1, lag.PartitionsWithoutCheckpoint);
        Assert.False(lag.Partitions[0].HasCheckpoint);
        Assert.Equal(-1, lag.Partitions[0].CheckpointedSequenceNumber);
    }

    [Fact]
    public void AnEmptyPartitionWithNoCheckpointHasNoLag()
    {
        var lag = LagCalculator.Measure([new PartitionSnapshot("3", -1)], new CheckpointLedger());

        Assert.Equal(0, lag.TotalLag);
        Assert.Equal(1, lag.PartitionsWithoutCheckpoint);
    }

    [Fact]
    public void AStaleSnapshotNeverProducesNegativeLag()
    {
        var ledger = new CheckpointLedger();
        ledger.Record("1", 800);

        var lag = LagCalculator.Measure([new PartitionSnapshot("1", 739)], ledger);

        Assert.Equal(0, lag.TotalLag);
        Assert.Equal(0, lag.Partitions[0].Lag);
    }

    [Fact]
    public void TheTotalIsTheSumOfThePartitions()
    {
        var ledger = new CheckpointLedger();
        ledger.Record("1", 739);

        var lag = LagCalculator.Measure(FourPartitions, ledger);

        Assert.Equal(270 + 0 + 60 + 150, lag.TotalLag);
        Assert.Equal(3, lag.PartitionsWithoutCheckpoint);
    }

    [Fact]
    public void ThePartitionsAreOrderedSoTheReportIsStable()
    {
        var ledger = new CheckpointLedger();

        var lag = LagCalculator.Measure(
            [new PartitionSnapshot("3", 1), new PartitionSnapshot("0", 1), new PartitionSnapshot("1", 1)],
            ledger);

        Assert.Equal(["0", "1", "3"], lag.Partitions.Select(partition => partition.PartitionId));
    }

    [Fact]
    public void OneUnreadPartitionCanNotBeHiddenByThreeHealthyOnes()
    {
        var ledger = new CheckpointLedger();
        ledger.Record("0", 269);
        ledger.Record("1", 739);
        ledger.Record("3", 149);

        var lag = LagCalculator.Measure(FourPartitions, ledger);

        Assert.Equal(60, lag.TotalLag);
        Assert.Equal(1, lag.PartitionsWithoutCheckpoint);
    }

    [Fact]
    public void EachPartitionCarriesItsOwnNumbers()
    {
        var ledger = new CheckpointLedger();
        ledger.Record("0", 200);

        var lag = LagCalculator.Measure(FourPartitions, ledger);
        var zero = lag.Partitions.Single(partition => partition.PartitionId == "0");

        Assert.Equal(200, zero.CheckpointedSequenceNumber);
        Assert.Equal(269, zero.LastEnqueuedSequenceNumber);
        Assert.Equal(69, zero.Lag);
    }
}
