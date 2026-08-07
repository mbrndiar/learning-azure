namespace LearningAzure.Projects.FieldStation;

/// <summary>Turns preserved artifacts into work orders on the dispatch queue.</summary>
/// <remarks>
/// Milestone 3, producer half. Dispatch happens <em>after</em> the artifact is
/// durable, never before: a message that points at a blob nobody wrote is a
/// guaranteed consumer failure, and the consumer cannot tell it apart from a
/// transient read error.
/// </remarks>
/// <param name="queue">The queue work orders are sent to.</param>
public sealed class WorkDispatcher(IWorkBacklog queue)
{
    /// <summary>The queue this dispatcher sends to.</summary>
    public IWorkBacklog Queue { get; } = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>Dispatches one operation for one preserved artifact.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="operation">The operation the worker should perform.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    /// <returns>The order that was sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    public async Task<WorkOrder> DispatchAsync(
        ArtifactKey key,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var order = new WorkOrder(
            StationNaming.WorkOrderId(key, operation),
            key.StationId,
            key.ObservationId,
            StationNaming.ArtifactName(key),
            operation);

        await Queue.SendAsync(order, cancellationToken).ConfigureAwait(false);
        return order;
    }

    /// <summary>Dispatches one operation for every artifact an intake result reports as stored.</summary>
    /// <param name="intake">The intake results of a batch.</param>
    /// <param name="operation">The operation the worker should perform.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    /// <returns>The orders that were sent, in the order they were dispatched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="intake"/> is <c>null</c>.</exception>
    public Task<IReadOnlyList<WorkOrder>> DispatchStoredAsync(
        IEnumerable<IntakeResult> intake,
        string operation,
        CancellationToken cancellationToken) =>
            // GAP 6 — A duplicate upload must not produce a second work order.
            //
            // The consumer is idempotent, so a duplicate message would not corrupt
            // anything — it would just pay for a receive, a claim, and a delete to
            // discover it has nothing to do. Suppressing the dispatch here is free;
            // suppressing it downstream is not.
            //
            // Dispatch for the results that actually changed the store — Stored and
            // Amended — and for those only. Recover the key with
            // StationNaming.TryParseArtifactName rather than splitting the name by
            // hand, and fail loudly on a name that does not follow the convention:
            // it means something wrote outside the naming rules.
            throw new NotImplementedException(
                "GAP 6: dispatch work only for artifacts that were actually written. See "
                + "projects/field-station/README.md#milestone-3-dispatching-work-and-consuming-it-once.");
}
