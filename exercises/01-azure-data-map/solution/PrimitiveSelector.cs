using System.Globalization;

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
    public static PrimitiveDecision Select(Workload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        // Rule 1. Independent consumers that re-read the same items need a
        // retained stream. A queue cannot serve this at all: a queue message is
        // handed to one consumer and then deleted, so a second consumer would
        // simply never see it.
        if (workload.ConsumersAreIndependentAndReplay)
        {
            return Decide(
                workload,
                Primitive.EventStream,
                Primitive.Queue,
                DecidingFactor.ReplayForIndependentConsumers,
                "Several consumers read the same items independently and may re-read them. "
                + "A Queue was the closest alternative, but a queue message is handed to exactly "
                + "one consumer and deleted, so a second reader would never observe it.");
        }

        // Rule 2. Work handed to exactly one worker wants competing consumers and
        // per-item completion, which is a queue, not a stream. A stream would make
        // every consumer read every item and force the application to invent its
        // own per-item completion tracking.
        if (workload.ItemIsHandedToExactlyOneWorker)
        {
            return Decide(
                workload,
                Primitive.Queue,
                Primitive.EventStream,
                DecidingFactor.CompetingConsumerHandoff,
                "Each item is work that one worker completes and then removes, which is exactly "
                + "the competing-consumer model a queue provides. An EventStream was the closest "
                + "alternative, but it has no per-item completion, so the application would have "
                + "to track which items are done itself.");
        }

        // Rule 3. Filtering on fields the key does not address is a query, and a
        // secondary index is what distinguishes a document store from a table.
        if (workload.QueriesFilterOnNonKeyFields)
        {
            return Decide(
                workload,
                Primitive.Document,
                Primitive.Table,
                DecidingFactor.ServerSideQueryOnNonKeyFields,
                "Reads filter on fields the key does not address, which needs a secondary index. "
                + "A Table was the closest alternative and is cheaper, but its only index is "
                + "PartitionKey plus RowKey, so those filters would become a scan.");
        }

        // Rule 4. When the caller already knows the key and the item fits, the
        // table's single index is not a limitation — it is the whole cost saving.
        if (workload.LookupsAreByKnownKey
            && workload.TypicalItemBytes <= PrimitiveCharacteristics.MaxTableEntityBytes)
        {
            return Decide(
                workload,
                Primitive.Table,
                Primitive.Document,
                DecidingFactor.PointLookupByKey,
                "Every lookup already knows its key, so the key is the only index this workload "
                + "needs. A Document store was the closest alternative, but its secondary index "
                + "costs request units on every write to serve queries this workload never issues.");
        }

        // Rule 5. What is left is opaque bytes read and written whole. Size is the
        // characteristic that rules the alternatives out: no entity, event, or
        // document ceiling accommodates a multi-megabyte artifact.
        return Decide(
            workload,
            Primitive.Blob,
            Primitive.Document,
            DecidingFactor.OpaquePayloadSize,
            "The item is opaque bytes read and written whole, and at "
            + workload.TypicalItemBytes.ToString("N0", CultureInfo.InvariantCulture)
            + " bytes it exceeds what an entity, an event, or a Document can hold. A Document "
            + "store was the closest alternative, but it would pay request units to index bytes "
            + "no query inspects.");
    }

    /// <summary>
    /// Builds the decision and derives the claim check from the chosen primitive's
    /// own ceiling, so the size rule never drifts from the characteristics table.
    /// </summary>
    private static PrimitiveDecision Decide(
        Workload workload,
        Primitive chosen,
        Primitive runnerUp,
        DecidingFactor factor,
        string justification)
    {
        var ceiling = PrimitiveCharacteristics.For(chosen).MaxItemBytes;
        var requiresClaimCheck = workload.TypicalItemBytes > ceiling;
        return new PrimitiveDecision(chosen, runnerUp, factor, requiresClaimCheck, justification);
    }
}
