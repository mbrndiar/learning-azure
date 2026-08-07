namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>What one processed message did, for the run report.</summary>
/// <param name="Disposition">What the worker told the queue to do.</param>
/// <param name="EffectApplied">Whether the handler actually ran for this delivery.</param>
/// <param name="Order">The decoded order, when the body was decodable.</param>
public sealed record ProcessedMessage(WorkDisposition Disposition, bool EffectApplied, ArtifactWorkOrder? Order);

/// <summary>A summary of one drain pass.</summary>
/// <param name="Received">Messages received.</param>
/// <param name="Completed">Messages deleted because they were settled.</param>
/// <param name="Retried">Messages left for the visibility timeout to requeue.</param>
/// <param name="Quarantined">Messages moved to the poison queue.</param>
/// <param name="EffectsApplied">How many times the handler actually ran.</param>
public sealed record DrainReport(int Received, int Completed, int Retried, int Quarantined, int EffectsApplied);

/// <summary>Consumes work orders from an at-least-once queue and applies each effect once.</summary>
/// <remarks>
/// <para>
/// Milestone 2. The queue guarantees delivery, not uniqueness: a message is
/// redelivered when a worker crashes, when a handler outlives its visibility
/// timeout, and sometimes for no visible reason at all. The worker's job is to
/// make the <em>effect</em> happen once anyway, to give up on work that can never
/// succeed, and to leave a shutdown cleanly interruptible.
/// </para>
/// <para>
/// The disposition is a decision, not a status code:
/// </para>
/// <list type="table">
/// <item><term>an undecodable body</term><description>quarantine on delivery 1 — retrying a deterministic failure only stops the queue draining</description></item>
/// <item><term>a delivery over budget</term><description>quarantine before claiming — it will fail again, and every attempt is real money</description></item>
/// <item><term>an already-journaled claim</term><description>delete, do not re-run: the effect is done and the message is noise</description></item>
/// <item><term>a resumed claim</term><description>run the effect: Pending means "may or may not have run"</description></item>
/// <item><term>a handler exception</term><description>retry until the budget is spent; not deleting the message <em>is</em> the retry</description></item>
/// <item><term>cancellation</term><description>let it propagate: shutdown is not a message defect</description></item>
/// </list>
/// </remarks>
/// <param name="queue">The queue to consume.</param>
/// <param name="ledger">The ledger that decides whether the effect already happened.</param>
/// <param name="maxDeliveryCount">Deliveries allowed before a message is quarantined.</param>
public sealed class ArtifactWorker(IWorkBacklog queue, StationLedger ledger, int maxDeliveryCount)
{
    /// <summary>The queue this worker consumes.</summary>
    public IWorkBacklog Queue { get; } = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>The ledger that gates duplicate effects.</summary>
    public StationLedger Ledger { get; } = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>Deliveries allowed before a message is quarantined.</summary>
    public int MaxDeliveryCount { get; } = maxDeliveryCount >= 1
        ? maxDeliveryCount
        : throw new ArgumentOutOfRangeException(nameof(maxDeliveryCount));

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
        Func<ArtifactWorkOrder, CancellationToken, Task> effect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(effect);

        // GAP 11 — Decode locally first, then check the delivery budget, and do
        // both BEFORE claiming or running anything.
        //
        // Decoding costs nothing and lets a quarantine record name the
        // observation rather than only the message id. A message that has already
        // burned its budget will not succeed on this attempt either, and every
        // extra attempt is real compute, a real claim, and a real registry write
        // spent on something that will fail again.
        ArtifactWorkOrder? decoded;
        try
        {
            decoded = JournalCodec.DecodeWorkOrder(work.Body);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException)
        {
            return await QuarantineAsync(
                work,
                order: null,
                $"Undecodable work order: {error.Message}",
                cancellationToken).ConfigureAwait(false);
        }

        if (work.DeliveryCount > MaxDeliveryCount)
        {
            return await QuarantineAsync(
                work,
                decoded,
                $"Exceeded the {MaxDeliveryCount}-delivery budget.",
                cancellationToken).ConfigureAwait(false);
        }

        var order = decoded;
        var claim = await Ledger
            .TryClaimAsync(order.Key, order.ArtifactName, cancellationToken)
            .ConfigureAwait(false);

        switch (claim)
        {
            case ClaimOutcome.AlreadyJournaled:
                // The effect is done. Delete the message so it stops being
                // delivered, but do not run the handler again.
                await Queue.DeleteAsync(work, cancellationToken).ConfigureAwait(false);
                return new ProcessedMessage(WorkDisposition.Complete, EffectApplied: false, order);

            case ClaimOutcome.Quarantined:
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
            // the process stops. Leaving the message alone IS the retry, and the
            // visibility timeout does the rest.
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (work.DeliveryCount >= MaxDeliveryCount)
            {
                return await QuarantineAsync(work, order, error.Message, cancellationToken).ConfigureAwait(false);
            }

            return new ProcessedMessage(WorkDisposition.Retry, EffectApplied: false, order);
        }

        // Confirm first, delete second. A crash between them redelivers a message
        // whose row already says Journaled, which costs one wasted receive; a
        // crash the other way round loses the record of an effect that happened.
        await Ledger.ConfirmAsync(order.Key, StationPhase.Journaled, cancellationToken).ConfigureAwait(false);
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
        Func<ArtifactWorkOrder, CancellationToken, Task> effect,
        int maxBatches,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatches, 1);

        int received = 0, completed = 0, retried = 0, quarantined = 0, effects = 0;

        for (var batch = 1; batch <= maxBatches; batch++)
        {
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
        ArtifactWorkOrder? order,
        string reason,
        CancellationToken cancellationToken)
    {
        var record = new PoisonRecord(work.MessageId, work.DeliveryCount, reason);
        Quarantined.Add(record);

        if (order is not null)
        {
            await Ledger.ConfirmAsync(order.Key, StationPhase.Quarantined, cancellationToken).ConfigureAwait(false);
        }

        // Moving a message aside is two operations that must both happen: copy it
        // somewhere a human can read, then remove it from the work queue. Copy
        // first — a crash in between can duplicate a poison record, which a human
        // can read twice, whereas the other order loses the evidence entirely.
        await Queue.QuarantineAsync(work, record, cancellationToken).ConfigureAwait(false);
        return new ProcessedMessage(WorkDisposition.Quarantine, EffectApplied: false, order);
    }
}
