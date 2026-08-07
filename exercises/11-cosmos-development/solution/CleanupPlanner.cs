namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>
/// Chooses how to remove data, given that Cosmos has no <c>DELETE FROM</c> and
/// every document you delete one at a time is charged like a write.
/// </summary>
public sealed class CleanupPlanner
{
    /// <summary>What deleting one 1 KB document costs.</summary>
    public const double PerDocumentRequestUnits = 5.0;

    /// <summary>What a query has to charge before it finds the documents to delete.</summary>
    public const double QueryOverheadRequestUnits = 2.5;

    /// <summary>Prices a delete-everything-by-hand cleanup.</summary>
    /// <param name="documents">How many documents will be deleted.</param>
    /// <returns>The request units the cleanup will be charged.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="documents"/> is negative.</exception>
    public static double RequestUnitsFor(int documents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documents);

        return documents == 0
            ? 0
            : QueryOverheadRequestUnits + (documents * PerDocumentRequestUnits);
    }

    /// <summary>Chooses a cleanup mechanism.</summary>
    /// <param name="totalDocuments">Everything the container holds.</param>
    /// <param name="documentsToRemove">How many of them have to go.</param>
    /// <param name="containerIsDisposable">
    /// Whether the container holds nothing else anybody needs.
    /// </param>
    /// <param name="expiryIsPredictable">
    /// Whether the documents become worthless after a known age, so the service
    /// can be told once and left to it.
    /// </param>
    /// <returns>The mechanism, its cost, and why it beat the others.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, or more documents are removed than exist.</exception>
    public static CleanupPlan Plan(
        int totalDocuments,
        int documentsToRemove,
        bool containerIsDisposable,
        bool expiryIsPredictable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalDocuments);
        ArgumentOutOfRangeException.ThrowIfNegative(documentsToRemove);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(documentsToRemove, totalDocuments);

        // GAP 14: the cheapest mechanism that is still correct, in that order.
        //
        // Deleting the container is free and instantaneous, but only if
        // nothing else lives in it — which is why "one container per concern"
        // is an operational decision as much as a modelling one. TTL is the
        // next best: the service spends leftover throughput on it, so the
        // deletion costs the application nothing it would otherwise have used.
        // Per-document deletion is last because it is the only one you pay for,
        // and its bill scales with the mistake.
        // See lessons/11-cosmos-development/README.md#deleting-is-a-write
        if (containerIsDisposable && documentsToRemove == totalDocuments)
        {
            return new CleanupPlan(
                CleanupStrategy.DeleteContainer,
                0,
                "Everything in the container is going, and nothing else needs it.");
        }

        if (expiryIsPredictable)
        {
            return new CleanupPlan(
                CleanupStrategy.TimeToLive,
                0,
                "The documents age out on a known schedule, so the service can do it.");
        }

        return new CleanupPlan(
            CleanupStrategy.DeletePerDocument,
            RequestUnitsFor(documentsToRemove),
            "A subset with no predictable expiry has to be found and deleted one at a time.");
    }
}
