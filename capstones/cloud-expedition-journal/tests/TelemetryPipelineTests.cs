namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>
/// Milestone 3 — telemetry in, checkpoints out. Judges whether a partition is
/// owned before it is read, whether a restart resumes instead of replaying, and
/// whether progress is recorded only after work actually succeeded.
/// </summary>
[Trait("Milestone", "telemetry-pipeline")]
public sealed class TelemetryPipelineTests
{
    private static readonly string[] ExpectedOrder = ["obs-0001", "obs-0002", "obs-0003"];
    private static readonly string[] ExpectedFailedRunOrder = ["obs-0001", "obs-0002"];
    private static readonly long[] ExpectedPositions = [0, 1, 2];

    [Fact]
    public async Task OneStationsReadingsAllLandInOnePartitionInOrder()
    {
        // Order is a per-partition guarantee. If one station spreads across
        // partitions, no consumer can reconstruct the sequence it observed.
        var journal = new Journal();
        await journal.PublishAsync(
            Fixture.Reading("obs-0001", minutes: 0),
            Fixture.Reading("obs-0002", minutes: 1),
            Fixture.Reading("obs-0003", minutes: 2));

        var partition = journal.Feed.Partitions[journal.Feed.PartitionFor(Fixture.Station)];

        Assert.Equal(3, partition.Count);
        Assert.Equal(ExpectedOrder, partition.Select(item => item.Reading.ObservationId));
        Assert.Equal(ExpectedPositions, partition.Select(item => item.SequenceNumber));
    }

    [Fact]
    public async Task AProcessorClaimsEveryPartitionBeforeItReadsIt()
    {
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading());

        var report = await journal.ProcessAsync();

        Assert.Equal(journal.Feed.Partitions.Count, report.PartitionsOwned);
        Assert.Equal(0, report.PartitionsLost);
        Assert.All(
            journal.Feed.Partitions.Keys,
            partitionId => Assert.Equal(Journal.OwnerId, journal.Checkpoints.OwnerOf(partitionId)));
    }

    [Fact]
    public async Task APartitionAnotherLiveHostOwnsIsLeftAlone()
    {
        // Two processors reading one partition do the same work twice and
        // checkpoint over each other. The claim is what stops that.
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading());
        await journal.ProcessAsync();

        var intruder = journal.Restart(ownerId: "host-b");
        var report = await intruder.RunAsync((_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.PartitionsOwned);
        Assert.Equal(journal.Feed.Partitions.Count, report.PartitionsLost);
        Assert.Equal(0, report.EventsRead);
    }

    [Fact]
    public async Task AnExpiredLeaseIsTakenOverSoAStalledHostDoesNotStopTheStream()
    {
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading());
        await journal.ProcessAsync();

        journal.Clock.Advance(Journal.LeaseDuration + TimeSpan.FromSeconds(1));
        var successor = journal.Restart(ownerId: "host-b");
        var report = await successor.RunAsync((_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(journal.Feed.Partitions.Count, report.PartitionsOwned);
        Assert.Equal("host-b", journal.Checkpoints.OwnerOf(journal.Feed.PartitionFor(Fixture.Station)));
    }

    [Fact]
    public async Task ARestartResumesFromTheCheckpointRatherThanFromTheStart()
    {
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading("obs-0001"), Fixture.Reading("obs-0002"));
        await journal.ProcessAsync();

        var handledFirst = journal.Handled.Count;
        var second = await journal.ProcessAsync();

        Assert.Equal(2, handledFirst);
        Assert.Equal(0, second.EventsRead);
        Assert.Equal(0, second.EventsHandled);
    }

    [Fact]
    public async Task AnEventDeliveredTwiceIsRecognisedAndNotHandledAgain()
    {
        // Event Hubs is at-least-once, so a redelivery below the watermark is
        // normal. Handling it again duplicates every downstream effect.
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading("obs-0001"), Fixture.Reading("obs-0002"));
        await journal.ProcessAsync();

        journal.Feed.RedeliverEverything = true;
        var replay = await journal.ProcessAsync();

        Assert.Equal(2, replay.EventsRead);
        Assert.Equal(2, replay.ReplaysSkipped);
        Assert.Equal(0, replay.EventsHandled);
        Assert.Equal(2, journal.Handled.Count);
    }

    [Fact]
    public async Task NothingIsCheckpointedWhenTheHandlerThrows()
    {
        // Checkpointing before the work succeeds converts a transient failure
        // into permanent data loss: the position moves past an event nobody
        // handled, and no restart will ever see it again.
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading());

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.Processor.RunAsync(
            (_, _) => throw new InvalidOperationException("The handler failed."),
            TestContext.Current.CancellationToken));

        Assert.Empty(journal.Checkpoints.Written);
        Assert.Null(await journal.Checkpoints.TryReadCheckpointAsync(
            journal.Feed.PartitionFor(Fixture.Station),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedRunReplaysExactlyTheEventsItNeverFinished()
    {
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading("obs-0001"), Fixture.Reading("obs-0002"));

        var seen = new List<string>();
        var failing = journal.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunAsync(
            (streamEvent, _) =>
            {
                seen.Add(streamEvent.Reading.ObservationId);
                return streamEvent.Reading.ObservationId == "obs-0002"
                    ? throw new InvalidOperationException("The handler failed.")
                    : Task.CompletedTask;
            },
            TestContext.Current.CancellationToken));

        var recovered = await journal.ProcessAsync();

        Assert.Equal(ExpectedFailedRunOrder, seen);
        Assert.Equal(2, recovered.EventsHandled);
    }

    [Fact]
    public async Task TheClosingCheckpointCoversTheTailTheIntervalMissed()
    {
        // Without it, a clean shutdown replays up to checkpointEvery events for
        // no reason at all.
        var journal = new Journal(maxDeliveryCount: 2, checkpointEvery: 10);
        await journal.PublishAsync(Fixture.Reading("obs-0001"), Fixture.Reading("obs-0002"));

        var report = await journal.ProcessAsync();
        var checkpoint = await journal.Checkpoints.TryReadCheckpointAsync(
            journal.Feed.PartitionFor(Fixture.Station),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, report.CheckpointsWritten);
        Assert.Equal(1, checkpoint!.SequenceNumber);
    }

    [Fact]
    public async Task AProcessorThatLostItsLeaseStopsInsteadOfCheckpointing()
    {
        // A checkpoint written by a former owner rewinds or advances the new
        // owner's position, which is worse than the crash it was meant to survive.
        var journal = new Journal(maxDeliveryCount: 2, checkpointEvery: 1);
        await journal.PublishAsync(
            Fixture.Reading("obs-0001"),
            Fixture.Reading("obs-0002"),
            Fixture.Reading("obs-0003"));

        var stationPartition = journal.Feed.PartitionFor(Fixture.Station);
        var handled = 0;

        var report = await journal.Processor.RunAsync(
            (streamEvent, _) =>
            {
                handled++;
                if (streamEvent.PartitionId == stationPartition && handled == 1)
                {
                    journal.Checkpoints.StealOwnership(stationPartition, "host-b");
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, report.OwnershipLost);
        Assert.True(journal.Checkpoints.RejectedCheckpoints >= 1);
        Assert.Equal("host-b", journal.Checkpoints.OwnerOf(stationPartition));
    }

    [Fact]
    public async Task TheCheckpointIntervalDecidesHowMuchAReplayRepeats()
    {
        var journal = new Journal(maxDeliveryCount: 2, checkpointEvery: 2);
        await journal.PublishAsync(
            Fixture.Reading("obs-0001"),
            Fixture.Reading("obs-0002"),
            Fixture.Reading("obs-0003"),
            Fixture.Reading("obs-0004"));

        var report = await journal.ProcessAsync();

        // Four events at an interval of two: two interval checkpoints, and no
        // tail left over.
        Assert.Equal(2, report.CheckpointsWritten);
        Assert.Equal(4, report.EventsHandled);
    }

    [Fact]
    public async Task ProcessingDrivesIntakeAndDispatchExactlyOncePerObservation()
    {
        var journal = new Journal();
        await journal.PublishAsync(
            Fixture.Reading("obs-0001"),
            Fixture.Reading("obs-0002", Fixture.OtherStation),
            Fixture.Reading("obs-0001"));

        await journal.ProcessAsync();

        Assert.Equal(3, journal.Handled.Count);
        Assert.Equal(2, journal.Vault.Count);
        Assert.Equal(2, journal.Backlog.Sent.Count);
    }

    [Fact]
    public async Task CancellationStopsTheProcessorBetweenPartitions()
    {
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            journal.Processor.RunAsync((_, _) => Task.CompletedTask, cancelled.Token));
    }
}
