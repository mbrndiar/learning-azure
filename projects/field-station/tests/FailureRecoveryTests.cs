namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>
/// Milestone 5 — what happens when things go wrong. Judges poison handling,
/// bounded retries, resumption after a crash, cancellation, and a teardown that
/// proves it finished.
/// </summary>
[Trait("Milestone", "failure-recovery")]
public sealed class FailureRecoveryTests
{
    [Fact]
    public async Task AMalformedMessageIsQuarantinedOnTheFirstDelivery()
    {
        // Retrying a deterministic failure buys nothing but a queue that never
        // drains, and the time to live quietly deletes the evidence a week later.
        var world = new Pipeline();
        world.Backlog.SendRaw("this is not a work order");

        var report = await world.DrainAsync();

        Assert.Equal(1, report.Quarantined);
        Assert.Equal(0, report.Retried);
        Assert.Equal(1, Assert.Single(world.Backlog.Poison).Record.DequeueCount);
        Assert.Equal(0, world.Backlog.Depth);
    }

    [Fact]
    public async Task AStructurallyValidButIncompleteMessageIsAlsoPoison()
    {
        // `{}` deserializes into a work order whose every field is null. Letting
        // it through moves the failure into the ledger.
        var world = new Pipeline();
        world.Backlog.SendRaw("{}");

        var report = await world.DrainAsync();

        Assert.Equal(1, report.Quarantined);
        Assert.Empty(world.Effect.Applied);
    }

    [Fact]
    public async Task AQuarantinedMessageIsMovedAsideRatherThanDropped()
    {
        var world = new Pipeline();
        world.Backlog.SendRaw("{ not json");

        await world.DrainAsync();

        var (record, body) = Assert.Single(world.Backlog.Poison);
        Assert.Equal("{ not json", body);
        Assert.Contains("Undecodable", record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingEffectIsRetriedWithinTheDeliveryBudget()
    {
        // The queue implements the retry by simply not being told to delete the
        // message; there is nothing for the worker to send.
        var world = new Pipeline(maxDequeueCount: 3);
        var order = Fixture.Order();
        world.Effect.FailFor.Add(order.WorkOrderId);

        var outcome = await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 1), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(WorkDisposition.Retry, outcome.Disposition);
        Assert.Empty(world.Backlog.Poison);
    }

    [Fact]
    public async Task AFailureOnTheLastAllowedDeliveryQuarantinesInsteadOfRetryingForever()
    {
        var world = new Pipeline(maxDequeueCount: 3);
        var order = Fixture.Order();
        world.Effect.FailFor.Add(order.WorkOrderId);

        var outcome = await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 3), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(WorkDisposition.Quarantine, outcome.Disposition);
        Assert.Equal(ProcessingState.Quarantined, (await world.RowAsync())!.State);
        Assert.Contains("Checksum failed", Assert.Single(world.Backlog.Poison).Record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageOverTheBudgetIsQuarantinedWithoutRunningTheEffectAgain()
    {
        // Checking the budget before the claim is what stops a doomed message
        // from paying for compute and a status write on every redelivery.
        var world = new Pipeline(maxDequeueCount: 3);
        var order = Fixture.Order();

        var outcome = await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 4), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(WorkDisposition.Quarantine, outcome.Disposition);
        Assert.Empty(world.Effect.Applied);
        Assert.Contains("3-delivery budget", Assert.Single(world.Backlog.Poison).Record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransientFailureFollowedByASuccessProcessesTheObservationOnce()
    {
        var world = new Pipeline(maxDequeueCount: 3);
        var order = Fixture.Order();
        world.Effect.FailFor.Add(order.WorkOrderId);

        await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 1), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);
        world.Effect.FailFor.Clear();
        var second = await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 2), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(WorkDisposition.Complete, second.Disposition);
        Assert.True(second.EffectApplied);
        Assert.Single(world.Effect.Applied);
        Assert.Equal(1, (await world.SummaryAsync())!.ProcessedCount);
    }

    [Fact]
    public async Task ARestartedWorkerResumesAnObservationTheCrashedRunNeverConfirmed()
    {
        // The row says Pending, which means the effect may or may not have
        // happened. Re-running it is the only safe reading.
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);

        var outcome = await world.Restart().ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 2), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.True(outcome.EffectApplied);
        Assert.Equal(ProcessingState.Processed, (await world.RowAsync())!.State);
    }

    [Fact]
    public async Task ARestartedWorkerDoesNotRerunAnObservationTheCrashedRunConfirmed()
    {
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);
        await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);
        world.Backlog.SendRaw(WorkOrderCodec.Encode(order));

        var report = await world.DrainAsync();

        Assert.Equal(0, report.EffectsApplied);
        Assert.Equal(1, report.Completed);
        Assert.Equal(0, world.Backlog.Depth);
        Assert.Equal(1, (await world.SummaryAsync())!.ProcessedCount);
    }

    [Fact]
    public async Task AnAlreadyQuarantinedObservationIsNotRetriedByALaterDelivery()
    {
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.MarkQuarantinedAsync(order, 8, TestContext.Current.CancellationToken);

        var outcome = await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 1), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(WorkDisposition.Quarantine, outcome.Disposition);
        Assert.Empty(world.Effect.Applied);
    }

    [Fact]
    public async Task CancellationIsNotTreatedAsAMessageDefect()
    {
        // Swallowing cancellation here quarantines healthy work on every
        // deployment, and the evidence looks like a data problem.
        var world = new Pipeline();
        var order = Fixture.Order();
        world.Effect.CancelFor.Add(order.WorkOrderId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => world.Worker.ProcessAsync(
                Fixture.Delivery(order), world.Effect.ApplyAsync, TestContext.Current.CancellationToken));

        Assert.Empty(world.Backlog.Poison);
        Assert.Equal(ProcessingState.Pending, (await world.RowAsync())!.State);
    }

    [Fact]
    public async Task ADrainStopsBetweenBatchesWhenShutdownIsRequested()
    {
        var world = new Pipeline();
        await world.Dispatcher.DispatchAsync(Fixture.Key, Fixture.Operation, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => world.Worker.DrainAsync(
                world.Effect.ApplyAsync, maxBatches: 4, TimeSpan.FromSeconds(30), cancelled.Token));

        Assert.Equal(1, world.Backlog.Depth);
    }

    [Fact]
    public async Task ARedeliveringQueueCannotSpinTheDrainForever()
    {
        // Every delivery fails, so nothing is ever deleted. Without a batch bound
        // this is an infinite loop that looks like a hung worker.
        var world = new Pipeline(maxDequeueCount: 99);
        await world.Dispatcher.DispatchAsync(Fixture.Key, Fixture.Operation, TestContext.Current.CancellationToken);
        world.Effect.FailFor.Add(StationNaming.WorkOrderId(Fixture.Key, Fixture.Operation));

        var report = await world.DrainAsync(maxBatches: 3);

        Assert.Equal(3, report.Received);
        Assert.Equal(3, report.Retried);
    }

    [Fact]
    public async Task CleanupRemovesEveryArtifactAndRowTheRunCreated()
    {
        var world = await FullRunAsync();

        var report = await world.Cleanup.RemoveStationAsync(Fixture.Station, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ArtifactsDeleted);
        Assert.Equal(3, report.StatusRowsDeleted); // two observations plus the summary
        Assert.True(report.IsComplete);
        Assert.Equal(0, world.Store.Count);
        Assert.Empty(world.Index.Rows);
    }

    [Fact]
    public async Task CleanupRemovesArtifactsThisProcessNeverCreated()
    {
        // Listing by prefix is the only view that includes what a previous,
        // crashed run left behind — which is the state cleanup exists to resolve.
        var world = new Pipeline();
        world.Store.Seed("stations/ridge-camp/orphan-0001.json", "{}");

        var report = await world.Cleanup.RemoveStationAsync(Fixture.Station, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ArtifactsDeleted);
        Assert.Equal(0, world.Store.Count);
    }

    [Fact]
    public async Task CleanupLeavesOtherStationsAlone()
    {
        var world = new Pipeline();
        world.Store.Seed("stations/ridge-camp/obs-0001.json", "{}");
        world.Store.Seed("stations/valley-camp/obs-0001.json", "{}");

        await world.Cleanup.RemoveStationAsync(Fixture.Station, TestContext.Current.CancellationToken);

        Assert.Equal(1, world.Store.Count);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(world.Store["stations/valley-camp/obs-0001.json"]));
    }

    [Fact]
    public async Task CleanupReportsAnIncompleteTeardownRatherThanClaimingSuccess()
    {
        // A teardown nobody verifies is indistinguishable from no teardown, and
        // in a real subscription the difference arrives on the invoice.
        var world = new Pipeline();
        world.Backlog.SendRaw("{}");

        var report = await world.Cleanup.RemoveStationAsync(Fixture.Station, TestContext.Current.CancellationToken);

        Assert.False(report.IsComplete);
        Assert.Equal(1, report.MessagesRemaining);
    }

    private static async Task<Pipeline> FullRunAsync()
    {
        var world = new Pipeline();

        foreach (var observation in new[] { "obs-0001", "obs-0002" })
        {
            var key = new ArtifactKey(Fixture.Station, observation);
            using var content = Fixture.Content($$"""{"observation":"{{observation}}"}""");
            var intake = await world.Intake.PreserveAsync(
                key, content, "application/json", TestContext.Current.CancellationToken);
            await world.Dispatcher.DispatchStoredAsync(
                [intake], Fixture.Operation, TestContext.Current.CancellationToken);
        }

        await world.DrainAsync();
        return world;
    }
}
