using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>
/// Milestone 3 — dispatching work and consuming it once. Judges the producer's
/// suppression of duplicate orders, the consumer's behaviour under redelivery,
/// and the Queue adapter's pop-receipt discipline.
/// </summary>
[Trait("Milestone", "work-dispatch")]
public sealed class WorkDispatchTests
{
    [Fact]
    public async Task APreservedArtifactProducesExactlyOneWorkOrder()
    {
        var backlog = new InMemoryBacklog();
        var dispatcher = new WorkDispatcher(backlog);

        var order = await dispatcher.DispatchAsync(
            Fixture.Key, Fixture.Operation, TestContext.Current.CancellationToken);

        Assert.Equal(1, backlog.Depth);
        Assert.Equal(StationNaming.WorkOrderId(Fixture.Key, Fixture.Operation), order.WorkOrderId);
        Assert.Equal(StationNaming.ArtifactName(Fixture.Key), order.ArtifactName);
    }

    [Fact]
    public async Task ADuplicateIntakeDispatchesNothing()
    {
        // The consumer would survive the extra message, but it would pay a
        // receive, a claim, and a delete to discover it has nothing to do.
        var backlog = new InMemoryBacklog();
        var dispatcher = new WorkDispatcher(backlog);
        var name = StationNaming.ArtifactName(Fixture.Key);

        var dispatched = await dispatcher.DispatchStoredAsync(
            [
                new IntakeResult(IntakeOutcome.Stored, name, "\"0x1\""),
                new IntakeResult(IntakeOutcome.Duplicate, name, null),
                new IntakeResult(IntakeOutcome.Conflict, name, null),
            ],
            Fixture.Operation,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, backlog.Depth);
        Assert.Single(dispatched);
    }

    [Fact]
    public async Task AnAmendmentDispatchesFreshWorkForTheSameObservation()
    {
        // New bytes mean the previous checksum is wrong, so the work must run
        // again — and it must carry the same work-order id, because it is the
        // same observation.
        var backlog = new InMemoryBacklog();
        var dispatcher = new WorkDispatcher(backlog);

        var dispatched = await dispatcher.DispatchStoredAsync(
            [new IntakeResult(IntakeOutcome.Amended, StationNaming.ArtifactName(Fixture.Key), "\"0x2\"")],
            Fixture.Operation,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            StationNaming.WorkOrderId(Fixture.Key, Fixture.Operation),
            Assert.Single(dispatched).WorkOrderId);
    }

    [Fact]
    public async Task DispatchHonoursCancellation()
    {
        var backlog = new InMemoryBacklog();
        var dispatcher = new WorkDispatcher(backlog);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(Fixture.Key, Fixture.Operation, cancelled.Token));

        Assert.Equal(0, backlog.Depth);
    }

    [Fact]
    public async Task AWorkOrderIsAppliedOnceAndTheMessageIsDeleted()
    {
        var world = new Pipeline();
        await world.Dispatcher.DispatchAsync(Fixture.Key, Fixture.Operation, TestContext.Current.CancellationToken);

        var report = await world.DrainAsync();

        Assert.Equal(1, report.EffectsApplied);
        Assert.Equal(1, report.Completed);
        Assert.Equal(0, world.Backlog.Depth);
        Assert.Equal([StationNaming.WorkOrderId(Fixture.Key, Fixture.Operation)], world.Effect.Applied);
    }

    [Fact]
    public async Task ARedeliveredWorkOrderRunsTheEffectOnlyOnce()
    {
        // At-least-once is the contract the queue actually offers. The effect
        // being applied twice is the failure this whole project is built around.
        var world = new Pipeline();
        var order = Fixture.Order();

        await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 1), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);
        var second = await world.Worker.ProcessAsync(
            Fixture.Delivery(order, dequeueCount: 2), world.Effect.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(WorkDisposition.Complete, second.Disposition);
        Assert.False(second.EffectApplied);
        Assert.Single(world.Effect.Applied);
    }

    [Fact]
    public async Task TwoDistinctObservationsAreBothProcessed()
    {
        // Deduplication must key on the work order, not on "have I seen anything
        // from this station".
        var world = new Pipeline();
        await world.Dispatcher.DispatchAsync(Fixture.Key, Fixture.Operation, TestContext.Current.CancellationToken);
        await world.Dispatcher.DispatchAsync(
            new ArtifactKey(Fixture.Station, "obs-0002"), Fixture.Operation, TestContext.Current.CancellationToken);

        var report = await world.DrainAsync();

        Assert.Equal(2, report.EffectsApplied);
        Assert.Equal(2, world.Effect.Applied.Count);
    }

    [Fact]
    public async Task CancellationDuringTheEffectLeavesTheMessageOnTheQueue()
    {
        // Not deleting the message IS the retry. A worker that swallows
        // cancellation and deletes anyway drops work on every deployment.
        var world = new Pipeline();
        await world.Dispatcher.DispatchAsync(Fixture.Key, Fixture.Operation, TestContext.Current.CancellationToken);
        world.Effect.CancelFor.Add(StationNaming.WorkOrderId(Fixture.Key, Fixture.Operation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => world.DrainAsync());

        Assert.Equal(1, world.Backlog.Depth);
        Assert.Empty(world.Backlog.Poison);
    }

    [Fact]
    public async Task ADrainStopsWhenTheQueueIsEmptyRatherThanSpinning()
    {
        var world = new Pipeline();

        var report = await world.DrainAsync();

        Assert.Equal(0, report.Received);
    }

    [Fact]
    public async Task TheAdapterDeletesWithThePopReceiptFromThisReceive()
    {
        // The pop receipt is what stops a slow worker from deleting a message
        // another worker has since picked up.
        var handler = new ScriptedHandler(_ => ScriptedClients.NoContent());
        var backlog = new QueueStorageBacklog(
            ScriptedClients.Queue(handler),
            ScriptedClients.Queue(handler, ScriptedClients.PoisonQueueUri));

        await backlog.DeleteAsync(
            new ReceivedWork("mid-1", "receipt-abc", 1, "{}"), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Contains("/messages/mid-1", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("popreceipt=receipt-abc", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeletingAMessageSomeoneElseAlreadySettledIsNotAnError()
    {
        // MessageNotFound after a visibility timeout expired is a benign race:
        // the work is settled either way, and throwing turns it into an alert.
        var handler = new ScriptedHandler(_ => StorageResponses.NotFound("MessageNotFound"));
        var backlog = new QueueStorageBacklog(
            ScriptedClients.Queue(handler),
            ScriptedClients.Queue(handler, ScriptedClients.PoisonQueueUri));

        await backlog.DeleteAsync(
            new ReceivedWork("mid-1", "stale", 1, "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task AReceiveAsksForAtMostTheServiceMaximum()
    {
        // Asking for 64 is a 400 from the service, not a bigger batch.
        var handler = new ScriptedHandler(_ => StorageResponses.WithXml(
            System.Net.HttpStatusCode.OK, "<QueueMessagesList />"));
        var backlog = new QueueStorageBacklog(
            ScriptedClients.Queue(handler),
            ScriptedClients.Queue(handler, ScriptedClients.PoisonQueueUri));

        await backlog.ReceiveAsync(64, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains("numofmessages=32", Assert.Single(handler.Requests).Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuarantineCopiesAsideBeforeItDeletes()
    {
        // The other order loses the evidence whenever the process dies in
        // between; this order can at worst duplicate it, which a human can read.
        var handler = new ScriptedHandler(
            _ => ScriptedClients.MessageSent(),
            _ => ScriptedClients.NoContent());
        var backlog = new QueueStorageBacklog(
            ScriptedClients.Queue(handler),
            ScriptedClients.Queue(handler, ScriptedClients.PoisonQueueUri));

        await backlog.QuarantineAsync(
            new ReceivedWork("mid-1", "receipt-abc", 5, "{}"),
            new PoisonRecord("mid-1", 5, "Exceeded the budget."),
            TestContext.Current.CancellationToken);

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Contains("artifact-work-poison", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("DELETE", handler.Requests[1].Method);
    }
}
