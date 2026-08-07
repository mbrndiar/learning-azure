namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>
/// Milestone 2 — Blob, Queue, and Table together. Judges whether a duplicate
/// costs nothing, whether the ledger arbitrates instead of guessing, and whether
/// a message that cannot succeed is moved aside instead of retried forever.
/// </summary>
[Trait("Milestone", "storage-workflow")]
public sealed class StorageWorkflowTests
{
    [Fact]
    public async Task ARepeatedReadingIsPreservedOnceAndDispatchedOnce()
    {
        var journal = new Journal();
        var reading = Fixture.Reading();

        var first = await journal.Intake.PreserveAsync(reading, TestContext.Current.CancellationToken);
        var second = await journal.Intake.PreserveAsync(reading, TestContext.Current.CancellationToken);

        await journal.Dispatcher.DispatchAsync(first, WorkOperations.Summarize, TestContext.Current.CancellationToken);
        await journal.Dispatcher.DispatchAsync(second, WorkOperations.Summarize, TestContext.Current.CancellationToken);

        Assert.Equal(IntakeOutcome.Stored, first.Outcome);
        Assert.Equal(IntakeOutcome.Duplicate, second.Outcome);
        Assert.Equal(1, journal.Vault.Count);
        Assert.Single(journal.Backlog.Sent);
    }

    [Fact]
    public async Task ADuplicateNeverOverwritesTheStoredReport()
    {
        // The second reading disagrees with the first. If intake replaced instead
        // of refusing, a retry carrying different bytes would silently rewrite
        // history under the same name.
        var journal = new Journal();
        await journal.Intake.PreserveAsync(Fixture.Reading(celsius: -14.5), TestContext.Current.CancellationToken);
        await journal.Intake.PreserveAsync(Fixture.Reading(celsius: 99.0), TestContext.Current.CancellationToken);

        var stored = System.Text.Encoding.UTF8.GetString(journal.Vault[ExpeditionNaming.ArtifactName(Fixture.Key)]);

        Assert.Contains("-14.5", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("99", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntakeWritesOnceRatherThanReadingThenWriting()
    {
        // A read-then-write intake passes this suite too, and loses the race in
        // production. One conditional write per report is the observable
        // difference.
        var journal = new Journal();
        await journal.Intake.PreserveAsync(Fixture.Reading(), TestContext.Current.CancellationToken);

        Assert.Single(journal.Vault.Writes);
    }

    [Fact]
    public async Task TheFirstDeliveryClaimsTheObservationAndTheSecondFindsItJournaled()
    {
        var journal = new Journal();
        var order = Fixture.Order();

        var first = await journal.Ledger.TryClaimAsync(
            order.Key,
            order.ArtifactName,
            TestContext.Current.CancellationToken);
        await journal.Ledger.ConfirmAsync(order.Key, StationPhase.Journaled, TestContext.Current.CancellationToken);
        var second = await journal.Ledger.TryClaimAsync(
            order.Key,
            order.ArtifactName,
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Claimed, first);
        Assert.Equal(ClaimOutcome.AlreadyJournaled, second);
        Assert.Equal(1, journal.Registry.LostInserts);
    }

    [Fact]
    public async Task AClaimLeftPendingByACrashedWorkerIsResumedNotSkipped()
    {
        // A pending row means "this may or may not have run". Reading it as done
        // loses work permanently; reading it as resumable costs one repeat of an
        // idempotent effect.
        var journal = new Journal();
        var order = Fixture.Order();

        await journal.Ledger.TryClaimAsync(order.Key, order.ArtifactName, TestContext.Current.CancellationToken);
        var resumed = await journal.Ledger.TryClaimAsync(
            order.Key,
            order.ArtifactName,
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Resumed, resumed);
    }

    [Fact]
    public async Task AQuarantinedObservationIsNotHandedBackToAWorker()
    {
        var journal = new Journal();
        var order = Fixture.Order();

        await journal.Ledger.TryClaimAsync(order.Key, order.ArtifactName, TestContext.Current.CancellationToken);
        await journal.Ledger.ConfirmAsync(order.Key, StationPhase.Quarantined, TestContext.Current.CancellationToken);

        Assert.Equal(
            ClaimOutcome.Quarantined,
            await journal.Ledger.TryClaimAsync(
                order.Key,
                order.ArtifactName,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AContendedWatermarkKeepsBothWritersCounts()
    {
        // The competing writer lands between this caller's read and its replace.
        // An implementation that re-sends the value it computed from the stale
        // read loses that writer's increment and reports success.
        var journal = new Journal();
        await journal.Ledger.AdvanceWatermarkAsync(Fixture.Station, 0, 1, TestContext.Current.CancellationToken);

        var stolen = false;
        journal.Registry.BeforeReplace = (station, row) =>
        {
            if (stolen || row != ExpeditionNaming.WatermarkRowKey)
            {
                return;
            }

            stolen = true;
            journal.Registry.StealAdvance(station, 5, 1);
        };

        var result = await journal.Ledger.AdvanceWatermarkAsync(
            Fixture.Station,
            6,
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.JournaledCount);
        Assert.Equal(6, result.LastSequenceNumber);
        Assert.Equal(1, journal.Registry.StaleReplaces);
    }

    [Fact]
    public async Task TheWatermarkNeverMovesBackwards()
    {
        // A replayed event carries a position the row has already passed.
        // Accepting it would rewind the consumer and redeliver everything after.
        var journal = new Journal();
        await journal.Ledger.AdvanceWatermarkAsync(Fixture.Station, 9, 1, TestContext.Current.CancellationToken);
        var back = await journal.Ledger.AdvanceWatermarkAsync(
            Fixture.Station,
            4,
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal(9, back.LastSequenceNumber);
    }

    [Fact]
    public async Task APermanentlyContendedRowFailsInsteadOfLoopingForever()
    {
        // An unbounded retry against a hot row is an outage that presents as a
        // hang, which is the hardest kind to diagnose.
        var journal = new Journal();
        await journal.Ledger.AdvanceWatermarkAsync(Fixture.Station, 0, 1, TestContext.Current.CancellationToken);
        journal.Registry.BeforeReplace = (station, row) => journal.Registry.StealRace(station, row);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.Ledger.AdvanceWatermarkAsync(Fixture.Station, 1, 1, TestContext.Current.CancellationToken));

        Assert.Contains("contended", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMalformedMessageIsQuarantinedOnItsFirstDelivery()
    {
        // Retrying a message that can never decode spends the whole delivery
        // budget discovering what the first attempt already knew.
        var journal = new Journal();
        journal.Backlog.SendRaw("""{"workOrderId":"","operation":"summarize"}""");

        var report = await journal.DrainAsync();

        Assert.Equal(1, report.Quarantined);
        Assert.Equal(0, report.Retried);
        Assert.Single(journal.Backlog.Poison);
        Assert.Equal(0, journal.Backlog.Depth);
    }

    [Fact]
    public async Task AFailingEffectIsRetriedUntilItsBudgetIsSpentAndThenQuarantined()
    {
        var journal = new Journal(maxDeliveryCount: 2);
        var order = Fixture.Order();
        journal.Effect.FailFor.Add(order.WorkOrderId);
        await journal.Backlog.SendAsync(order, TestContext.Current.CancellationToken);

        var first = await journal.DrainAsync(maxBatches: 1);
        var second = await journal.DrainAsync(maxBatches: 1);

        Assert.Equal(1, first.Retried);
        Assert.Equal(1, second.Quarantined);
        Assert.Equal(0, journal.Backlog.Depth);
        Assert.Empty(journal.Effect.Applied);
        Assert.Equal(StationPhase.Quarantined, (await journal.RowAsync())!.Phase);
    }

    [Fact]
    public async Task ARedeliveredMessageDoesNotRepeatASucceededEffect()
    {
        var journal = new Journal(maxDeliveryCount: 3);
        var order = Fixture.Order();

        await journal.Worker.ProcessAsync(
            Fixture.Delivery(order),
            journal.Effect.ApplyAsync,
            TestContext.Current.CancellationToken);
        var repeat = await journal.Worker.ProcessAsync(
            Fixture.Delivery(order, deliveryCount: 2, messageId: "m2"),
            journal.Effect.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Single(journal.Effect.Applied);
        Assert.Equal(WorkDisposition.Complete, repeat.Disposition);
        Assert.False(repeat.EffectApplied);
    }

    [Fact]
    public async Task ShutdownDuringAnEffectLeavesTheMessageOnTheQueue()
    {
        // Cancellation is not a message defect. Quarantining on shutdown throws
        // away work that would have succeeded on the next start.
        var journal = new Journal();
        var order = Fixture.Order();
        journal.Effect.CancelFor.Add(order.WorkOrderId);
        await journal.Backlog.SendAsync(order, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.DrainAsync());

        Assert.Empty(journal.Backlog.Poison);
        Assert.Equal(1, journal.Backlog.Depth);
    }

    [Fact]
    public async Task ADeleteWithAStalePopReceiptIsRefused()
    {
        // The pop receipt proves this receive. Without it, a worker whose
        // visibility timeout lapsed deletes work another worker is running.
        var journal = new Journal();
        await journal.Backlog.SendAsync(Fixture.Order(), TestContext.Current.CancellationToken);

        var first = await journal.Backlog.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await journal.Backlog.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await journal.Backlog.DeleteAsync(first[0], TestContext.Current.CancellationToken);

        Assert.Equal(1, journal.Backlog.RejectedDeletes);
        Assert.Equal(1, journal.Backlog.Depth);
    }

    [Fact]
    public async Task AQuarantinedMessageIsCopiedAsideBeforeItIsDeleted()
    {
        var journal = new Journal();
        journal.Backlog.SendRaw("{ not a work order");

        await journal.DrainAsync();

        var (record, body) = Assert.Single(journal.Backlog.Poison);
        Assert.Contains("{ not a work order", body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(record.Reason));
    }

    [Fact]
    public async Task TeardownRemovesWorkAPreviousRunLeftBehind()
    {
        // Deleting only what this process remembers leaves behind everything a
        // crashed run created, which is exactly the state teardown exists for.
        var journal = new Journal();
        journal.Vault.Seed($"{ExpeditionNaming.StationPrefix(Fixture.Station)}orphan.json", "{}");
        await journal.Intake.PreserveAsync(Fixture.Reading(), TestContext.Current.CancellationToken);
        await journal.Ledger.AdvanceWatermarkAsync(Fixture.Station, 0, 1, TestContext.Current.CancellationToken);

        var report = await journal.Cleanup.RemoveAsync(
            [Fixture.Station],
            pageSize: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ReportsDeleted);
        Assert.Equal(1, report.StationRowsDeleted);
        Assert.True(report.IsComplete);
        Assert.Equal(0, journal.Vault.Count);
    }

    [Fact]
    public async Task TeardownReportsAnIncompleteQueueRatherThanClaimingSuccess()
    {
        var journal = new Journal();
        await journal.Backlog.SendAsync(Fixture.Order(), TestContext.Current.CancellationToken);

        var report = await journal.Cleanup.RemoveAsync(
            [Fixture.Station],
            pageSize: 10,
            TestContext.Current.CancellationToken);

        Assert.False(report.IsComplete);
        Assert.Equal(1, report.MessagesRemaining);
    }
}
