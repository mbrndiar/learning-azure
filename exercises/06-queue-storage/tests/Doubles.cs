namespace LearningAzure.Exercises.QueueStorage.Tests;

/// <summary>An in-memory ledger that claims each work order id exactly once.</summary>
internal sealed class RecordingLedger : IProcessedLedger
{
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    /// <summary>Every id a claim was attempted for, in order.</summary>
    public List<string> Attempts { get; } = [];

    /// <summary>Ids that were successfully claimed.</summary>
    public IReadOnlyCollection<string> Claimed => _claimed;

    public Task<bool> TryClaimAsync(string workOrderId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Attempts.Add(workOrderId);
        return Task.FromResult(_claimed.Add(workOrderId));
    }
}

/// <summary>Counts the observable effects a handler produced.</summary>
internal sealed class EffectRecorder
{
    /// <summary>Every work order the handler was actually run for.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>Ids the handler should fail for.</summary>
    public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

    public Task ApplyAsync(WorkOrder order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailFor.Contains(order.WorkOrderId))
        {
            throw new InvalidOperationException($"Handler failed for {order.WorkOrderId}.");
        }

        Applied.Add(order.WorkOrderId);
        return Task.CompletedTask;
    }
}
