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
    public async Task<MessageDisposition> DispatchAsync(
        ReceivedMessage message,
        Func<WorkOrder, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(handler);

        // GAP 5 — Check the dequeue count BEFORE doing any work.
        //
        // A message that has already failed MaxDequeueCount times is not going to
        // succeed on this attempt either, and every extra attempt is real
        // compute spent on something that will fail. Checking afterwards means
        // one more full failure per message, forever.
        if (message.DequeueCount > MaxDequeueCount)
        {
            Quarantined.Add(new PoisonReport(
                message.MessageId,
                message.DequeueCount,
                $"Exceeded the {MaxDequeueCount}-delivery budget."));
            return MessageDisposition.Quarantine;
        }

        WorkOrder order;
        try
        {
            order = WorkOrderCodec.Decode(message.Body);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException)
        {
            // GAP 6 — An undecodable message is poison on the FIRST delivery.
            //
            // Retrying a malformed message is retrying a deterministic failure.
            // It will fail identically every time, so the only thing retrying
            // buys is a queue that never drains.
            Quarantined.Add(new PoisonReport(
                message.MessageId,
                message.DequeueCount,
                $"Message body is not a decodable work order: {error.Message}"));
            return MessageDisposition.Quarantine;
        }

        // GAP 7 — Claim the work by its PRODUCER-CHOSEN id, not the message id.
        //
        // The message id changes on every enqueue, so deduplicating on it catches
        // redelivery of the same queue entry and nothing else. A retry that
        // re-enqueues the same work gets a new message id and slips straight
        // through.
        if (!await Ledger.TryClaimAsync(order.WorkOrderId, cancellationToken).ConfigureAwait(false))
        {
            // Already done. The effect must not happen again, but the message
            // still has to be deleted or it will be delivered forever.
            return MessageDisposition.Complete;
        }

        try
        {
            await handler(order, cancellationToken).ConfigureAwait(false);
            return MessageDisposition.Complete;
        }
        catch (OperationCanceledException)
        {
            // Shutdown is not a message defect. Leave the message for whoever
            // picks the queue up next.
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // GAP 8 — A transient failure is a Retry, and the queue implements
            // the retry by simply not being told to delete the message.
            return message.DequeueCount >= MaxDequeueCount
                ? Quarantine(message, error.Message)
                : MessageDisposition.Retry;
        }
    }

    private MessageDisposition Quarantine(ReceivedMessage message, string reason)
    {
        Quarantined.Add(new PoisonReport(message.MessageId, message.DequeueCount, reason));
        return MessageDisposition.Quarantine;
    }
}
