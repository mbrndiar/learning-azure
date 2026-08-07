namespace LearningAzure.Exercises.EventHubsModel;

/// <summary>Chooses between a partitioned event stream and a work queue.</summary>
/// <remarks>
/// Module 6 built the queue. The two look interchangeable from a distance —
/// both move messages from a producer to a consumer — and they are not
/// interchangeable at all: a queue destroys a message when it is handled, and a
/// stream does not know that anyone handled anything.
/// </remarks>
public static class StreamOrQueueSelector
{
    /// <summary>Chooses the dispatch primitive a workload needs.</summary>
    /// <param name="requirement">The workload's characteristics.</param>
    /// <returns>The choice and the one characteristic that decided it.</returns>
    public static DispatchChoice Choose(WorkloadRequirement requirement) =>
        // GAP 9 — Evaluate the decisive characteristics IN ORDER, because a
        // workload usually has several and only one of them is structural.
        //
        //   1. RequiresReplay              → EventStream. A queue message is
        //      gone once it is deleted; "read it again next month" is not
        //      something a queue can be configured into.
        //   2. IndependentReaderCount > 1  → EventStream. Queue consumers
        //      compete for one copy, so two readers that each need everything
        //      need two queues and a fan-out the producer maintains.
        //   3. RequiresPerKeyOrdering      → EventStream. A queue makes no
        //      ordering promise, and its visibility timeout actively reorders
        //      redelivered work.
        //   4. RequiresPerItemAcknowledgement → WorkQueue. A stream has no
        //      per-event completion, only a per-partition cursor.
        //   5. ItemDurationSpread == Wide  → WorkQueue. A stream partition is
        //      processed by exactly one owner in order, so one slow event
        //      blocks everything behind it.
        //   otherwise                      → WorkQueue, the cheaper primitive.
        //
        // Each returned Reason must name the characteristic that decided it;
        // the evaluator reads it.
        throw new NotImplementedException(
            "GAP 9: implement StreamOrQueueSelector.Choose. See "
            + "lessons/08-event-hubs-model/README.md#a-stream-is-not-a-queue.");
}
