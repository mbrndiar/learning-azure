namespace LearningAzure.Exercises.QueueStorage;

/// <summary>Processes work orders exactly once, on a queue that delivers at least once.</summary>
/// <remarks>
/// The queue guarantees delivery, not uniqueness. A message is redelivered when
/// a consumer crashes, when a handler outlives its visibility timeout, and
/// occasionally for no visible reason at all. Making the <em>effect</em> happen
/// once is therefore the consumer's job, and there is no setting for it.
/// </remarks>
public sealed class IdempotentDispatcher(IProcessedLedger ledger, int maxDequeueCount)
{
    /// <summary>The ledger that decides whether this work has already been done.</summary>
    public IProcessedLedger Ledger { get; } = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>Deliveries allowed before a message is quarantined.</summary>
    public int MaxDequeueCount { get; } = maxDequeueCount >= 1
        ? maxDequeueCount
        : throw new ArgumentOutOfRangeException(nameof(maxDequeueCount));

    /// <summary>Every message this dispatcher gave up on.</summary>
    public List<PoisonReport> Quarantined { get; } = [];

    /// <summary>Handles one received message and reports what should happen to it.</summary>
    /// <param name="message">The message the queue handed back.</param>
    /// <param name="handler">The effect to apply, at most once per work order.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    /// <returns>What the caller should do with the queue message.</returns>
    public Task<MessageDisposition> DispatchAsync(
        ReceivedMessage message,
        Func<WorkOrder, CancellationToken, Task> handler,
        CancellationToken cancellationToken) =>
        // GAP 5 — Check the dequeue count BEFORE doing any work.
        //
        // A message that has already failed MaxDequeueCount times is not going to
        // succeed on this attempt either, and every extra attempt is real compute
        // spent on something that will fail. Add a PoisonReport to Quarantined
        // and return Quarantine.
        //
        // GAP 6 — An undecodable message is poison on the FIRST delivery.
        //
        // Retrying a malformed message is retrying a deterministic failure. It
        // will fail identically every time, so the only thing retrying buys is a
        // queue that never drains. Catch FormatException and JsonException.
        //
        // GAP 7 — Claim the work by its PRODUCER-CHOSEN id, not the message id.
        //
        // The message id changes on every enqueue, so deduplicating on it catches
        // redelivery of the same queue entry and nothing else. A retry that
        // re-enqueues the same work gets a new message id and slips straight
        // through. When the claim is lost the work is already done: return
        // Complete so the message is deleted, but do NOT run the handler.
        //
        // GAP 8 — A handler failure is a Retry until the delivery budget is
        // spent, then a Quarantine. The queue implements the retry by simply not
        // being told to delete the message. An OperationCanceledException is a
        // shutdown, not a message defect: let it propagate.
        throw new NotImplementedException(
            "GAP 5: implement IdempotentDispatcher.DispatchAsync. See "
            + "lessons/06-queue-storage/README.md#at-least-once-is-your-problem.");
}
