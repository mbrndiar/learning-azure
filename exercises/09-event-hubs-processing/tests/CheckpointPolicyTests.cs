namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>
/// The policy is where the duplicate count is chosen. These checks pin both
/// bounds and the shutdown case, because a policy that only counts events is a
/// policy that abandons quiet partitions.
/// </summary>
public sealed class CheckpointPolicyTests
{
    private static readonly TimeSpan ThirtySeconds = TimeSpan.FromSeconds(30);

    [Fact]
    public void BothBoundsMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CheckpointPolicy(0, ThirtySeconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CheckpointPolicy(-1, ThirtySeconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CheckpointPolicy(25, TimeSpan.Zero));
    }

    [Fact]
    public void ANegativeHandledCountIsRefused()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.Evaluate(-1, TimeSpan.Zero, isPartitionClosing: false));
    }

    [Fact]
    public void NothingIsDueBeforeEitherBoundIsReached()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.None,
            policy.Evaluate(24, TimeSpan.FromSeconds(29), isPartitionClosing: false));
    }

    [Fact]
    public void TheEventBoundFires()
    {
        var policy = Fixtures.EveryNEvents(25);

        Assert.Equal(
            CheckpointReason.EventCount,
            policy.Evaluate(25, TimeSpan.Zero, isPartitionClosing: false));
    }

    [Fact]
    public void TheEventBoundFiresWhenOvershot()
    {
        var policy = Fixtures.EveryNEvents(25);

        Assert.Equal(
            CheckpointReason.EventCount,
            policy.Evaluate(400, TimeSpan.Zero, isPartitionClosing: false));
    }

    [Fact]
    public void TheTimeBoundFiresOnAQuietPartition()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.Elapsed,
            policy.Evaluate(1, TimeSpan.FromSeconds(31), isPartitionClosing: false));
    }

    [Fact]
    public void APolicyWithOnlyATimeBoundStillProtectsAQuietPartition()
    {
        var policy = Fixtures.EveryInterval(ThirtySeconds);

        Assert.Equal(
            CheckpointReason.Elapsed,
            policy.Evaluate(1, ThirtySeconds, isPartitionClosing: false));
    }

    [Fact]
    public void TheEventBoundOutranksTheTimeBound()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.EventCount,
            policy.Evaluate(25, TimeSpan.FromMinutes(5), isPartitionClosing: false));
    }

    [Fact]
    public void TimePassingWithNoEventsIsNotACheckpoint()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.None,
            policy.Evaluate(0, TimeSpan.FromHours(1), isPartitionClosing: false));
    }

    [Fact]
    public void ClosingCheckpointsWhateverIsOutstanding()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.PartitionClosing,
            policy.Evaluate(1, TimeSpan.Zero, isPartitionClosing: true));
    }

    [Fact]
    public void ClosingWithNothingOutstandingWritesNothing()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.None,
            policy.Evaluate(0, TimeSpan.FromHours(1), isPartitionClosing: true));
    }

    [Fact]
    public void ClosingOutranksBothBounds()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(
            CheckpointReason.PartitionClosing,
            policy.Evaluate(100, TimeSpan.FromHours(1), isPartitionClosing: true));
    }

    [Fact]
    public void TheBoundsAreVisibleToCallers()
    {
        var policy = new CheckpointPolicy(25, ThirtySeconds);

        Assert.Equal(25, policy.EveryEvents);
        Assert.Equal(ThirtySeconds, policy.EveryInterval);
    }
}
