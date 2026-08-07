namespace LearningAzure.Exercises.QueueStorage;

/// <summary>One unit of processing work, as it travels through the queue.</summary>
/// <param name="WorkOrderId">Stable identity of the work, chosen by the producer.</param>
/// <param name="ArtifactName">The blob the work applies to.</param>
/// <param name="Operation">What to do with it.</param>
public sealed record WorkOrder(string WorkOrderId, string ArtifactName, string Operation);

/// <summary>A message as the queue service hands it back to a consumer.</summary>
/// <param name="MessageId">Service-assigned identity of this queue entry.</param>
/// <param name="PopReceipt">Proof of this particular receive; required to delete or extend.</param>
/// <param name="DequeueCount">How many times this message has been handed out, starting at 1.</param>
/// <param name="Body">The message payload, already decoded.</param>
public sealed record ReceivedMessage(string MessageId, string PopReceipt, long DequeueCount, string Body);

/// <summary>What the consumer decided to do with a received message.</summary>
public enum MessageDisposition
{
    /// <summary>The work is done. Delete the message so it is never delivered again.</summary>
    Complete,

    /// <summary>Something transient failed. Let the visibility timeout expire and try again.</summary>
    Retry,

    /// <summary>It has failed too often. Move it aside so the queue can drain.</summary>
    Quarantine,
}

/// <summary>Why a message was quarantined, in the operator's language.</summary>
/// <param name="MessageId">The message that was moved aside.</param>
/// <param name="DequeueCount">How many deliveries it took before giving up.</param>
/// <param name="Reason">What the last failure was.</param>
public sealed record PoisonReport(string MessageId, long DequeueCount, string Reason);

/// <summary>The two shapes of message-driven processing this module contrasts.</summary>
public enum DispatchModel
{
    /// <summary>Competing consumers pull independent work items; order is not preserved.</summary>
    WorkQueue,

    /// <summary>Partitioned, replayable log; per-partition order is preserved.</summary>
    EventStream,
}

/// <summary>The properties a workload needs, stated before a service is chosen.</summary>
/// <param name="RequiresPerKeyOrder">Whether events for one key must be processed in order.</param>
/// <param name="RequiresReplay">Whether the same data must be re-readable after processing.</param>
/// <param name="RequiresIndependentScaling">Whether items are independent and parallelizable.</param>
/// <param name="ConsumersPerItem">How many independent consumers must see each item.</param>
public sealed record WorkloadShape(
    bool RequiresPerKeyOrder,
    bool RequiresReplay,
    bool RequiresIndependentScaling,
    int ConsumersPerItem);

/// <summary>Records which work orders have already had their effect applied.</summary>
/// <remarks>
/// At-least-once delivery makes this the consumer's problem, not the queue's.
/// A real implementation is a table entity or a blob written with
/// <c>If-None-Match: *</c> — both of which this course has already built.
/// </remarks>
public interface IProcessedLedger
{
    /// <summary>Claims <paramref name="workOrderId"/> for processing, atomically.</summary>
    /// <param name="workOrderId">The producer-chosen work identity.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns><c>true</c> when this caller won the claim; <c>false</c> when it was already processed.</returns>
    Task<bool> TryClaimAsync(string workOrderId, CancellationToken cancellationToken);
}
