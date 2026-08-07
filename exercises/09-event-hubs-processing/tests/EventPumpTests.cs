namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>
/// The pump is where the other four pieces meet. These checks pin the
/// behaviours that only show up when they are wired together: the shutdown
/// checkpoint, the duplicate that still counts as progress, and cancellation
/// that stops without losing the record of what was done.
/// </summary>
public sealed class EventPumpTests
{
    private static EventPump Pump(
        CheckpointLedger ledger,
        IdempotentProjection projection,
        CheckpointPolicy policy,
        ManualClock clock) =>
        new(ledger, projection, policy, clock);

    [Fact]
    public void EveryCollaboratorIsRequired()
    {
        var ledger = new CheckpointLedger();
        var projection = new IdempotentProjection();
        var policy = Fixtures.Never();
        var clock = new ManualClock();

        Assert.Throws<ArgumentNullException>(() => new EventPump(null!, projection, policy, clock));
        Assert.Throws<ArgumentNullException>(() => new EventPump(ledger, null!, policy, clock));
        Assert.Throws<ArgumentNullException>(() => new EventPump(ledger, projection, null!, clock));
        Assert.Throws<ArgumentNullException>(() => new EventPump(ledger, projection, policy, null!));
    }

    [Fact]
    public async Task AStreamIsRequired()
    {
        var pump = Pump(new CheckpointLedger(), new IdempotentProjection(), Fixtures.Never(), new ManualClock());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => pump.RunAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EveryDeliveredEventReachesTheProjection()
    {
        var pump = Pump(new CheckpointLedger(), new IdempotentProjection(), Fixtures.Never(), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 30), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(30, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task TheEventBoundDrivesTheCadence()
    {
        var ledger = new CheckpointLedger();
        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.EveryNEvents(25), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 100, count: 100), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Checkpoints);
        Assert.All(pump.CheckpointReasons, reason => Assert.Equal(CheckpointReason.EventCount, reason));
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(199, sequence);
    }

    [Fact]
    public async Task ARunThatEndsOnABoundaryWritesNoClosingCheckpoint()
    {
        var pump = Pump(
            new CheckpointLedger(),
            new IdempotentProjection(),
            Fixtures.EveryNEvents(3),
            new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 6), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Checkpoints);
        Assert.Equal(
            [CheckpointReason.EventCount, CheckpointReason.EventCount],
            pump.CheckpointReasons);
    }

    [Fact]
    public async Task WhatWasHandledAfterTheLastBoundIsRecordedOnTheWayOut()
    {
        var ledger = new CheckpointLedger();
        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.EveryNEvents(25), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 100, count: 90), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Checkpoints);
        Assert.Equal(CheckpointReason.PartitionClosing, pump.CheckpointReasons[^1]);
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(189, sequence);
    }

    [Fact]
    public async Task AQuietPartitionIsStillCheckpointed()
    {
        var clock = new ManualClock { AutoAdvance = TimeSpan.FromMinutes(10) };
        var pump = Pump(
            new CheckpointLedger(),
            new IdempotentProjection(),
            Fixtures.EveryInterval(TimeSpan.FromSeconds(30)),
            clock);

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 4), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Checkpoints);
        Assert.All(pump.CheckpointReasons, reason => Assert.Equal(CheckpointReason.Elapsed, reason));
    }

    [Fact]
    public async Task AStandingClockNeverTripsTheTimeBound()
    {
        var pump = Pump(
            new CheckpointLedger(),
            new IdempotentProjection(),
            Fixtures.EveryInterval(TimeSpan.FromSeconds(30)),
            new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 50), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Checkpoints);
        Assert.Equal([CheckpointReason.PartitionClosing], pump.CheckpointReasons);
    }

    [Fact]
    public async Task DuplicatesCountAsProgress()
    {
        var run = Fixtures.Run(Fixtures.PartitionZero, from: 1, count: 3);
        var replay = new List<HandledEvent>(run) { run[0], run[1] };
        replay.Add(new HandledEvent(Fixtures.PartitionZero, 4, "reading"));

        var pump = Pump(
            new CheckpointLedger(),
            new IdempotentProjection(),
            Fixtures.EveryNEvents(3),
            new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(replay, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Skipped);
        Assert.Equal(
            [CheckpointReason.EventCount, CheckpointReason.EventCount],
            pump.CheckpointReasons);
    }

    [Fact]
    public async Task ARestartReplaysOnlyWhatWasNotRecorded()
    {
        var durable = new CheckpointLedger();
        var first = Pump(durable, new IdempotentProjection(), Fixtures.EveryNEvents(25), new ManualClock());

        // The first process is killed rather than stopped, so the closing
        // checkpoint never happens: only what the event bound recorded survives.
        await first.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 100, count: 75), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var resume = durable.ResumeFrom(Fixtures.PartitionZero);
        var redelivered = Fixtures.Run(Fixtures.PartitionZero, from: resume.SequenceNumber + 1, count: 25);

        Assert.Equal(174, resume.SequenceNumber);
        Assert.False(resume.IsInclusive);
        Assert.Equal(175, redelivered[0].SequenceNumber);
    }

    [Fact]
    public async Task PartitionsAreCheckpointedIndependently()
    {
        var ledger = new CheckpointLedger();
        var interleaved = new List<HandledEvent>();

        for (var index = 0; index < 6; index++)
        {
            interleaved.Add(new HandledEvent(Fixtures.PartitionZero, index, "reading"));
            interleaved.Add(new HandledEvent(Fixtures.PartitionOne, 100 + index, "reading"));
        }

        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.EveryNEvents(3), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(interleaved, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Checkpoints);
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var zero));
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionOne, out var one));
        Assert.Equal(5, zero);
        Assert.Equal(105, one);
    }

    [Fact]
    public async Task CancellationStopsThePumpWithoutThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        var ledger = new CheckpointLedger();
        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.Never(), new ManualClock());

        var result = await pump.RunAsync(
            Fixtures.CancelAfter(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 500), 40, cancellation),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(40, result.Applied);
    }

    [Fact]
    public async Task ACancelledRunStillRecordsWhatItDid()
    {
        using var cancellation = new CancellationTokenSource();
        var ledger = new CheckpointLedger();
        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.Never(), new ManualClock());

        var result = await pump.RunAsync(
            Fixtures.CancelAfter(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 500), 40, cancellation),
            cancellation.Token);

        Assert.Equal(1, result.Checkpoints);
        Assert.Equal([CheckpointReason.PartitionClosing], pump.CheckpointReasons);
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(39, sequence);
    }

    [Fact]
    public async Task AnAlreadyCancelledRunHandlesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var ledger = new CheckpointLedger();
        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.Never(), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 50), TestContext.Current.CancellationToken),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Applied);
        Assert.Equal(0, result.Checkpoints);
        Assert.Equal(0, ledger.Writes);
    }

    [Fact]
    public async Task ARewindIsNeverRecordedEvenOnTheWayOut()
    {
        var ledger = new CheckpointLedger();
        ledger.Record(Fixtures.PartitionZero, 500);

        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.Never(), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream(Fixtures.Run(Fixtures.PartitionZero, from: 0, count: 10), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Checkpoints);
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(500, sequence);
    }

    [Fact]
    public async Task AnEmptyStreamWritesNothing()
    {
        var ledger = new CheckpointLedger();
        var pump = Pump(ledger, new IdempotentProjection(), Fixtures.EveryNEvents(1), new ManualClock());

        var result = await pump.RunAsync(
            EventPump.Stream([], TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Applied);
        Assert.Equal(0, result.Checkpoints);
        Assert.False(result.Cancelled);
        Assert.Empty(ledger.Snapshot());
    }
}
