namespace LearningAzure.Exercises.QueueStorage.Tests;

public sealed class IdempotentDispatcherTests
{
    private static ReceivedMessage Message(WorkOrder order, long dequeueCount = 1, string? messageId = null) =>
        new(messageId ?? $"msg-{order.WorkOrderId}", "receipt-1", dequeueCount, WorkOrderCodec.Encode(order));

    private static WorkOrder Order(string id = "wo-1") => new(id, "station-01/reading.json", "ingest");

    [Fact]
    public async Task AFirstDeliveryRunsTheHandlerAndCompletes()
    {
        var ledger = new RecordingLedger();
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(ledger, 5);

        var disposition = await dispatcher.DispatchAsync(
            Message(Order()),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Complete, disposition);
        Assert.Equal(["wo-1"], effects.Applied);
    }

    [Fact]
    public async Task ARedeliveredMessageDoesNotApplyTheEffectASecondTime()
    {
        var ledger = new RecordingLedger();
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(ledger, 5);
        var order = Order();

        await dispatcher.DispatchAsync(Message(order), effects.ApplyAsync, TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(Message(order, dequeueCount: 2), effects.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(["wo-1"], effects.Applied);
    }

    [Fact]
    public async Task ARedeliveredMessageIsStillCompletedSoTheQueueCanDrain()
    {
        var ledger = new RecordingLedger();
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(ledger, 5);
        var order = Order();

        await dispatcher.DispatchAsync(Message(order), effects.ApplyAsync, TestContext.Current.CancellationToken);
        var second = await dispatcher.DispatchAsync(
            Message(order, dequeueCount: 2),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Complete, second);
    }

    [Fact]
    public async Task TheSameWorkReEnqueuedUnderANewMessageIdIsStillDeduplicated()
    {
        // This is the case a message-id cache misses: the producer retried, so
        // the queue entry is genuinely new but the work is not.
        var ledger = new RecordingLedger();
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(ledger, 5);
        var order = Order();

        await dispatcher.DispatchAsync(
            Message(order, messageId: "msg-a"),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(
            Message(order, messageId: "msg-b"),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(["wo-1"], effects.Applied);
    }

    [Fact]
    public async Task TheClaimIsMadeAgainstTheWorkOrderIdNotTheMessageId()
    {
        var ledger = new RecordingLedger();
        var dispatcher = new IdempotentDispatcher(ledger, 5);

        await dispatcher.DispatchAsync(
            Message(Order(), messageId: "msg-zzz"),
            new EffectRecorder().ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(["wo-1"], ledger.Attempts);
    }

    [Fact]
    public async Task DistinctWorkOrdersAreEachApplied()
    {
        var ledger = new RecordingLedger();
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(ledger, 5);

        await dispatcher.DispatchAsync(Message(Order("wo-1")), effects.ApplyAsync, TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(Message(Order("wo-2")), effects.ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Equal(["wo-1", "wo-2"], effects.Applied);
    }

    [Fact]
    public async Task AFailingHandlerAsksForARetryWhileBudgetRemains()
    {
        var effects = new EffectRecorder();
        effects.FailFor.Add("wo-1");
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 5);

        var disposition = await dispatcher.DispatchAsync(
            Message(Order()),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Retry, disposition);
    }

    [Fact]
    public async Task AFailingHandlerOnTheLastAllowedDeliveryQuarantines()
    {
        var effects = new EffectRecorder();
        effects.FailFor.Add("wo-1");
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 3);

        var disposition = await dispatcher.DispatchAsync(
            Message(Order(), dequeueCount: 3),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Quarantine, disposition);
    }

    [Fact]
    public async Task AMessageOverTheDeliveryBudgetIsQuarantinedWithoutRunningTheHandler()
    {
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 3);

        var disposition = await dispatcher.DispatchAsync(
            Message(Order(), dequeueCount: 4),
            effects.ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Quarantine, disposition);
        Assert.Empty(effects.Applied);
    }

    [Fact]
    public async Task AMessageOverTheDeliveryBudgetDoesNotEvenTouchTheLedger()
    {
        var ledger = new RecordingLedger();
        var dispatcher = new IdempotentDispatcher(ledger, 3);

        await dispatcher.DispatchAsync(
            Message(Order(), dequeueCount: 9),
            new EffectRecorder().ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Empty(ledger.Attempts);
    }

    [Fact]
    public async Task AQuarantineIsRecordedWithTheDequeueCountThatCausedIt()
    {
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 3);

        await dispatcher.DispatchAsync(
            Message(Order(), dequeueCount: 7),
            new EffectRecorder().ApplyAsync,
            TestContext.Current.CancellationToken);

        var report = Assert.Single(dispatcher.Quarantined);
        Assert.Equal("msg-wo-1", report.MessageId);
        Assert.Equal(7, report.DequeueCount);
    }

    [Fact]
    public async Task AnUndecodableMessageIsQuarantinedOnTheFirstDelivery()
    {
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 5);
        var message = new ReceivedMessage("msg-bad", "receipt-1", 1, "!!! not base64 !!!");

        var disposition = await dispatcher.DispatchAsync(
            message,
            new EffectRecorder().ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Quarantine, disposition);
    }

    [Fact]
    public async Task AnUndecodableMessageIsNeverRetried()
    {
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 5);
        var message = new ReceivedMessage(
            "msg-bad",
            "receipt-1",
            1,
            Convert.ToBase64String("still not json"u8.ToArray()));

        var disposition = await dispatcher.DispatchAsync(
            message,
            new EffectRecorder().ApplyAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageDisposition.Quarantine, disposition);
        Assert.Single(dispatcher.Quarantined);
    }

    [Fact]
    public async Task AnUndecodableMessageIsNeverClaimed()
    {
        var ledger = new RecordingLedger();
        var dispatcher = new IdempotentDispatcher(ledger, 5);
        var message = new ReceivedMessage("msg-bad", "receipt-1", 1, "!!! not base64 !!!");

        await dispatcher.DispatchAsync(message, new EffectRecorder().ApplyAsync, TestContext.Current.CancellationToken);

        Assert.Empty(ledger.Attempts);
    }

    [Fact]
    public async Task ACancelledDispatchDoesNotQuarantineTheMessage()
    {
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 5);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(
                Message(Order()),
                new EffectRecorder().ApplyAsync,
                cancellation.Token));

        Assert.Empty(dispatcher.Quarantined);
    }

    [Fact]
    public void ADispatcherRequiresALedger()
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotentDispatcher(null!, 5));
    }

    [Fact]
    public void ADeliveryBudgetBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdempotentDispatcher(new RecordingLedger(), 0));
    }

    [Fact]
    public async Task ANullMessageIsRejected()
    {
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 5);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.DispatchAsync(null!, new EffectRecorder().ApplyAsync, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANullHandlerIsRejected()
    {
        var dispatcher = new IdempotentDispatcher(new RecordingLedger(), 5);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.DispatchAsync(Message(Order()), null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheAtLeastOnceStormAppliesEachEffectExactlyOnce()
    {
        // Five work orders, each delivered three times in interleaved order:
        // the shape a real consumer sees after a rolling restart.
        var ledger = new RecordingLedger();
        var effects = new EffectRecorder();
        var dispatcher = new IdempotentDispatcher(ledger, 10);
        var orders = Enumerable.Range(1, 5).Select(n => Order($"wo-{n}")).ToArray();

        for (var delivery = 1; delivery <= 3; delivery++)
        {
            foreach (var order in orders)
            {
                await dispatcher.DispatchAsync(
                    Message(order, dequeueCount: delivery),
                    effects.ApplyAsync,
                    TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(5, effects.Applied.Count);
        Assert.Equal<IEnumerable<string>>(
            ["wo-1", "wo-2", "wo-3", "wo-4", "wo-5"],
            [.. effects.Applied.Order(StringComparer.Ordinal)]);
    }
}
