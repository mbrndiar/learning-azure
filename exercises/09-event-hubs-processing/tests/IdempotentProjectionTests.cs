namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>
/// At-least-once delivery is the service's contract. These checks pin what a
/// handler has to do about it, including the cases where the naive
/// deduplication key looks like it works.
/// </summary>
public sealed class IdempotentProjectionTests
{
    [Fact]
    public void AnEventIsRequired()
    {
        var projection = new IdempotentProjection();

        Assert.Throws<ArgumentNullException>(() => projection.Apply(null!));
        Assert.Throws<ArgumentException>(() => projection.HighWaterMark(" "));
    }

    [Fact]
    public void AFirstEventIsApplied()
    {
        var projection = new IdempotentProjection();

        Assert.True(projection.Apply(new HandledEvent(Fixtures.PartitionZero, 0, "reading")));
        Assert.Equal(1, projection.Applied);
        Assert.Equal(0, projection.Skipped);
    }

    [Fact]
    public void AnUnseenPartitionHasNoHighWaterMark()
    {
        var projection = new IdempotentProjection();

        Assert.Equal(-1, projection.HighWaterMark(Fixtures.PartitionZero));
    }

    [Fact]
    public void SequenceNumberZeroIsNotADuplicate()
    {
        var projection = new IdempotentProjection();

        Assert.True(projection.Apply(new HandledEvent(Fixtures.PartitionZero, 0, "reading")));
        Assert.Equal(0, projection.Skipped);
    }

    [Fact]
    public void AReplayedEventChangesNothing()
    {
        var projection = new IdempotentProjection();
        var handled = new HandledEvent(Fixtures.PartitionZero, 7, "reading");

        projection.Apply(handled);

        Assert.False(projection.Apply(handled));
        Assert.Equal(1, projection.Applied);
        Assert.Equal(1, projection.Skipped);
        Assert.Equal(1, projection.Totals["reading"]);
    }

    [Fact]
    public void AWholeReplayedRunChangesNothing()
    {
        var projection = new IdempotentProjection();
        var run = Fixtures.Run(Fixtures.PartitionZero, from: 100, count: 15);

        foreach (var handled in run.Concat(run))
        {
            projection.Apply(handled);
        }

        Assert.Equal(15, projection.Applied);
        Assert.Equal(15, projection.Skipped);
        Assert.Equal(15, projection.Totals["reading"]);
    }

    [Fact]
    public void TwoDistinctEventsWithTheSameBodyAreBothApplied()
    {
        var projection = new IdempotentProjection();

        projection.Apply(new HandledEvent(Fixtures.PartitionZero, 1, "12.5C"));
        projection.Apply(new HandledEvent(Fixtures.PartitionZero, 2, "12.5C"));

        Assert.Equal(2, projection.Applied);
        Assert.Equal(2, projection.Totals["12.5C"]);
    }

    [Fact]
    public void TheSameSequenceNumberOnAnotherPartitionIsADifferentEvent()
    {
        var projection = new IdempotentProjection();

        projection.Apply(new HandledEvent(Fixtures.PartitionZero, 42, "reading"));

        Assert.True(projection.Apply(new HandledEvent(Fixtures.PartitionOne, 42, "reading")));
        Assert.Equal(2, projection.Applied);
        Assert.Equal(0, projection.Skipped);
        Assert.Equal(2, projection.TrackedPartitions);
    }

    [Fact]
    public void GapsInSequenceNumbersAreNormal()
    {
        var projection = new IdempotentProjection();

        projection.Apply(new HandledEvent(Fixtures.PartitionZero, 10, "a"));

        Assert.True(projection.Apply(new HandledEvent(Fixtures.PartitionZero, 400, "b")));
        Assert.Equal(400, projection.HighWaterMark(Fixtures.PartitionZero));
    }

    [Fact]
    public void TheHighWaterMarkTracksTheLastAppliedEvent()
    {
        var projection = new IdempotentProjection();

        foreach (var handled in Fixtures.Run(Fixtures.PartitionZero, from: 5, count: 6))
        {
            projection.Apply(handled);
        }

        Assert.Equal(10, projection.HighWaterMark(Fixtures.PartitionZero));
    }

    [Fact]
    public void ADuplicateDoesNotMoveTheHighWaterMarkBackwards()
    {
        var projection = new IdempotentProjection();

        projection.Apply(new HandledEvent(Fixtures.PartitionZero, 50, "a"));
        projection.Apply(new HandledEvent(Fixtures.PartitionZero, 20, "b"));

        Assert.Equal(50, projection.HighWaterMark(Fixtures.PartitionZero));
        Assert.False(projection.Totals.ContainsKey("b"));
    }

    [Fact]
    public void TheDescriptionReportsBothCounts()
    {
        var projection = new IdempotentProjection();
        var handled = new HandledEvent(Fixtures.PartitionZero, 1, "reading");

        projection.Apply(handled);
        projection.Apply(handled);

        Assert.Equal("applied 1, skipped 1, distinct bodies 1", projection.Describe());
    }
}
