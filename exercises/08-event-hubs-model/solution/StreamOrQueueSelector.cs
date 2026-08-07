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
    public static DispatchChoice Choose(WorkloadRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // GAP 9 — The decisive characteristics are evaluated in order, because
        // a workload usually has several and only one of them is structural.
        //
        // Replay first: a queue message is gone once it is deleted, so "read it
        // again next month" is not something a queue can be configured into.
        if (requirement.RequiresReplay)
        {
            return new DispatchChoice(
                DispatchPrimitive.EventStream,
                "the same data must be readable again, and a queue deletes what it delivers");
        }

        // Then independent readers: a queue's consumers compete for one copy,
        // so two readers that each need everything need two queues and a
        // fan-out the producer maintains.
        if (requirement.IndependentReaderCount > 1)
        {
            return new DispatchChoice(
                DispatchPrimitive.EventStream,
                $"{requirement.IndependentReaderCount} readers each need every event, and queue "
                + "consumers compete for one copy");
        }

        // Then ordering: a queue makes no ordering promise at all, and its
        // visibility timeout actively reorders redelivered work.
        if (requirement.RequiresPerKeyOrdering)
        {
            return new DispatchChoice(
                DispatchPrimitive.EventStream,
                "per-key order is required, and a queue reorders on every redelivery");
        }

        // Per-item acknowledgement is the queue's structural advantage: a
        // stream has no per-event completion, only a per-partition cursor.
        if (requirement.RequiresPerItemAcknowledgement)
        {
            return new DispatchChoice(
                DispatchPrimitive.WorkQueue,
                "each item is completed or retried on its own, and a stream only has a "
                + "per-partition cursor");
        }

        // Wide duration spread is the other one: a queue lets a fast worker
        // take the next item, while a stream pins a partition to one owner and
        // one slow event blocks everything behind it.
        if (requirement.ItemDurationSpread == WorkDurationSpread.Wide)
        {
            return new DispatchChoice(
                DispatchPrimitive.WorkQueue,
                "item cost varies widely, and a stream partition is processed by exactly one "
                + "owner in order");
        }

        return new DispatchChoice(
            DispatchPrimitive.WorkQueue,
            "nothing requires replay, fan-out, or ordering, so the cheaper primitive with "
            + "per-item retry wins");
    }
}
