namespace LearningAzure.Projects.FieldStation;

/// <summary>What one processed message did, for the run report.</summary>
/// <param name="Disposition">What the worker told the queue to do.</param>
/// <param name="EffectApplied">Whether the handler actually ran for this delivery.</param>
/// <param name="Order">The decoded order, when the body was decodable.</param>
public sealed record ProcessedMessage(WorkDisposition Disposition, bool EffectApplied, WorkOrder? Order);

/// <summary>A summary of one drain pass.</summary>
/// <param name="Received">Messages received.</param>
/// <param name="Completed">Messages deleted because they were settled.</param>
/// <param name="Retried">Messages left for the visibility timeout to requeue.</param>
/// <param name="Quarantined">Messages moved to the poison queue.</param>
/// <param name="EffectsApplied">How many times the handler actually ran.</param>
public sealed record DrainReport(int Received, int Completed, int Retried, int Quarantined, int EffectsApplied);

/// <summary>
/// Consumes work orders from an at-least-once queue and applies each effect once.
/// </summary>
/// <remarks>
/// <para>
/// Milestones 3 and 5. The queue guarantees delivery, not uniqueness: a message
/// is redelivered when a worker crashes, when a handler outlives its visibility
/// timeout, and sometimes for no visible reason. The worker's whole job is to
/// make the <em>effect</em> happen once anyway, to give up on work that can never
/// succeed, and to leave a shutdown cleanly interruptible.
/// </para>
/// <para>
/// Nothing in here mentions Azure. It is driven identically by in-memory fakes
/// and by the Azurite-backed adapters, which is what makes the failure behaviour
/// testable at all.
/// </para>
/// </remarks>
/// <param name="queue">The queue to consume.</param>
/// <param name="projector">The ledger that decides whether the effect already happened.</param>
/// <param name="maxDequeueCount">Deliveries allowed before a message is quarantined.</param>
public sealed class StationWorker(IWorkBacklog queue, StationStatusProjector projector, int maxDequeueCount)
{
    private const int ConcurrencyAttempts = 8;

    /// <summary>The queue this worker consumes.</summary>
    public IWorkBacklog Queue { get; } = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>The status projector that gates duplicate effects.</summary>
    public StationStatusProjector Projector { get; } =
        projector ?? throw new ArgumentNullException(nameof(projector));

    /// <summary>Deliveries allowed before a message is quarantined.</summary>
    public int MaxDequeueCount { get; } = maxDequeueCount >= 1
        ? maxDequeueCount
        : throw new ArgumentOutOfRangeException(nameof(maxDequeueCount));

    /// <summary>Every message this worker gave up on, in order.</summary>
    public List<PoisonRecord> Quarantined { get; } = [];

    /// <summary>Handles one received message and settles it with the queue.</summary>
    /// <param name="work">The message the queue handed back.</param>
    /// <param name="effect">The effect to apply, at most once per work order.</param>
    /// <param name="cancellationToken">Cancels the processing.</param>
    /// <returns>What the worker did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> or <paramref name="effect"/> is <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException">Shutdown was requested; the message is left on the queue.</exception>
    public async Task<ProcessedMessage> ProcessAsync(
        ReceivedWork work,
        Func<WorkOrder, CancellationToken, Task> effect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(effect);

        // GAP 9 — Check the delivery budget BEFORE claiming or running anything.
        //
        // A message that has already burned its budget will not succeed on this
        // attempt either, and every extra attempt is real compute, a real claim,
        // and a real status write spent on something that will fail again.
        // Decoding first costs nothing — it is local — and it is what lets the
        // quarantine name the observation instead of only the message id.
        WorkOrder? decoded;
        try
        {
            decoded = WorkOrderCodec.Decode(work.Body);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException)
        {
            // GAP 10 — An undecodable message is poison on the FIRST delivery.
            //
            // Retrying a deterministic failure buys nothing but a queue that
            // never drains, and the message will still be malformed in seven
            // days when its time to live quietly deletes the evidence.
            return await QuarantineAsync(work, order: null, $"Undecodable work order: {error.Message}", cancellationToken)
                .ConfigureAwait(false);
        }

        if (work.DequeueCount > MaxDequeueCount)
        {
            return await QuarantineAsync(
                work,
                decoded,
                $"Exceeded the {MaxDequeueCount}-delivery budget.",
                cancellationToken).ConfigureAwait(false);
        }

        var order = decoded;

        // GAP 11 — Claim by the producer-chosen identity, then honour the claim.
        //
        // AlreadyProcessed means the effect is done: delete the message so it
        // stops being delivered, but do NOT run the handler again. Resumed means
        // a previous attempt claimed the row and never confirmed, so the effect
        // may or may not have happened. Running it again requires the effect
        // itself to be idempotent or independently deduplicated.
        var claim = await Projector.TryClaimAsync(order, cancellationToken).ConfigureAwait(false);
        switch (claim)
        {
            case ClaimOutcome.AlreadyProcessed:
                await Queue.DeleteAsync(work, cancellationToken).ConfigureAwait(false);
                return new ProcessedMessage(WorkDisposition.Complete, EffectApplied: false, order);

            case ClaimOutcome.AlreadyQuarantined:
                return await QuarantineAsync(
                    work,
                    order,
                    "The observation is already quarantined and needs a human.",
                    cancellationToken).ConfigureAwait(false);

            default:
                break;
        }

        try
        {
            await effect(order, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // GAP 12 — Shutdown is not a message defect.
            //
            // Swallowing cancellation here quarantines healthy work every time
            // the process stops. Leave the message alone: not deleting it IS the
            // retry, and the visibility timeout does the rest.
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (work.DequeueCount >= MaxDequeueCount)
            {
                return await QuarantineAsync(work, order, error.Message, cancellationToken).ConfigureAwait(false);
            }

            // The queue implements the retry by simply not being told to delete
            // the message. There is nothing to send.
            return new ProcessedMessage(WorkDisposition.Retry, EffectApplied: false, order);
        }

        // Confirm first, delete second. A crash between them redelivers a
        // message whose row already says Processed, which costs one wasted
        // receive; a crash the other way round loses the record of an effect
        // that really happened.
        await Projector.ConfirmProcessedAsync(order, ConcurrencyAttempts, cancellationToken).ConfigureAwait(false);
        await Queue.DeleteAsync(work, cancellationToken).ConfigureAwait(false);
        return new ProcessedMessage(WorkDisposition.Complete, EffectApplied: true, order);
    }

    /// <summary>Receives and settles messages until the queue stops handing any back.</summary>
    /// <param name="effect">The effect to apply, at most once per work order.</param>
    /// <param name="maxBatches">Upper bound on receive rounds, so a redelivering queue cannot spin forever.</param>
    /// <param name="visibilityTimeout">How long each received batch stays invisible.</param>
    /// <param name="cancellationToken">Cancels the drain between and during messages.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <c>null</c>.</exception>
    public async Task<DrainReport> DrainAsync(
        Func<WorkOrder, CancellationToken, Task> effect,
        int maxBatches,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatches, 1);

        int received = 0, completed = 0, retried = 0, quarantined = 0, effects = 0;

        for (var batch = 1; batch <= maxBatches; batch++)
        {
            // Checking the token between batches is what makes a long drain
            // stoppable without waiting for the current batch to finish.
            cancellationToken.ThrowIfCancellationRequested();

            var messages = await Queue
                .ReceiveAsync(32, visibilityTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (messages.Count == 0)
            {
                break;
            }

            foreach (var message in messages)
            {
                received++;
                var outcome = await ProcessAsync(message, effect, cancellationToken).ConfigureAwait(false);
                if (outcome.EffectApplied)
                {
                    effects++;
                }

                switch (outcome.Disposition)
                {
                    case WorkDisposition.Complete:
                        completed++;
                        break;
                    case WorkDisposition.Retry:
                        retried++;
                        break;
                    default:
                        quarantined++;
                        break;
                }
            }
        }

        return new DrainReport(received, completed, retried, quarantined, effects);
    }

    private async Task<ProcessedMessage> QuarantineAsync(
        ReceivedWork work,
        WorkOrder? order,
        string reason,
        CancellationToken cancellationToken)
    {
        var record = new PoisonRecord(work.MessageId, work.DequeueCount, reason);
        Quarantined.Add(record);

        if (order is not null)
        {
            await Projector.MarkQuarantinedAsync(order, ConcurrencyAttempts, cancellationToken).ConfigureAwait(false);
        }

        // Moving the message aside is two operations that must both happen:
        // copy it somewhere a human can read it, then remove it from the work
        // queue so the backlog drains.
        await Queue.QuarantineAsync(work, record, cancellationToken).ConfigureAwait(false);
        return new ProcessedMessage(WorkDisposition.Quarantine, EffectApplied: false, order);
    }
}
