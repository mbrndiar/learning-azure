namespace LearningAzure.Exercises.QueueStorage;

/// <summary>Chooses between a work queue and an event stream from stated requirements.</summary>
/// <remarks>
/// The two look interchangeable in a diagram and are not. A queue destroys a
/// message once it is handled; a stream keeps it and lets every consumer read at
/// its own offset. Neither can be made into the other by configuration.
/// </remarks>
public static class DispatchSelector
{
    /// <summary>Chooses the dispatch model <paramref name="shape"/> actually needs.</summary>
    /// <param name="shape">The workload's stated requirements.</param>
    /// <returns>The model that satisfies them.</returns>
    public static DispatchModel Choose(WorkloadShape shape) =>
        // GAP 9 — Apply the rules in order; the first match wins.
        //
        // 1. Replay: a queue message is gone once deleted. If the same data must
        //    be re-read later, only a stream can do it.
        // 2. Per-key order: a queue hands items to competing consumers in no
        //    guaranteed order. Only a partitioned stream preserves order.
        // 3. Fan-out: a queue message is consumed by exactly one reader. Two
        //    independent consumers need two copies, or a stream.
        // 4. Otherwise: independent work items, scaled by adding consumers.
        //    That is precisely what a work queue is for.
        throw new NotImplementedException(
            "GAP 9: implement DispatchSelector.Choose. See "
            + "lessons/06-queue-storage/README.md#a-queue-is-not-a-stream.");

    /// <summary>Explains the choice in one sentence an architecture review can check.</summary>
    /// <param name="shape">The workload's stated requirements.</param>
    /// <returns>The reason the chosen model was chosen.</returns>
    public static string Justify(WorkloadShape shape) =>
        // GAP 10 — Name the requirement that decided it, not the service.
        //
        // "We chose Event Hubs because it scales" is not a justification; it is
        // a preference. "We chose a stream because the audit replay requires
        // re-reading processed data" is one, and it can be checked.
        //
        // The evaluator asserts that the sentence starts with "Event stream:" or
        // "Work queue:" and names the deciding requirement.
        throw new NotImplementedException(
            "GAP 10: implement DispatchSelector.Justify. See "
            + "lessons/06-queue-storage/README.md#a-queue-is-not-a-stream.");
}
