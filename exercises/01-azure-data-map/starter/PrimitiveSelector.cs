namespace LearningAzure.Exercises.DataMap;

/// <summary>Routes an expedition workload to the Azure data primitive that fits it.</summary>
/// <remarks>
/// The rules are ordered, and the order matters: a workload can satisfy more than
/// one condition, and the earlier rule describes the stronger constraint. The
/// module narrative derives each rule and the boundary it protects.
/// </remarks>
public static class PrimitiveSelector
{
    /// <summary>Chooses a primitive for <paramref name="workload"/> and names the option it beat.</summary>
    /// <param name="workload">The workload, described without naming a primitive.</param>
    /// <returns>The decision, its runner-up, the deciding factor, and whether a claim check is needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workload"/> is <c>null</c>.</exception>
    public static PrimitiveDecision Select(Workload workload) =>
        // GAP 2 — Implement the ordered routing rules.
        //
        //   1. Independent consumers that re-read the same items   -> EventStream, over Queue
        //   2. Each item handed to exactly one worker              -> Queue, over EventStream
        //   3. Queries filter on non-key fields                    -> Document, over Table
        //   4. Lookups by known key, item fits a table entity      -> Table, over Document
        //   5. Otherwise                                           -> Blob, over Document
        //
        // Set RequiresClaimCheck when workload.TypicalItemBytes exceeds the chosen
        // primitive's MaxItemBytes: the payload then has to live in a blob and the
        // item carries only its name.
        //
        // Justification must name the runner-up and say why it lost — a choice you
        // cannot defend against the adjacent service is a guess.
        throw new NotImplementedException(
            "GAP 2: implement PrimitiveSelector.Select. See "
            + "lessons/01-azure-data-map/README.md#route-a-workload.");
}
