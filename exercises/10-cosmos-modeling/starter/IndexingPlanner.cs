namespace LearningAzure.Exercises.CosmosModeling;

/// <summary>
/// Prices an indexing policy. Cosmos indexes every path by default, which is
/// the right decision until write volume makes it the wrong one.
/// </summary>
public static class IndexingPlanner
{
    /// <summary>What writing a 1 KB document costs before any indexing.</summary>
    public const double BaseWriteRequestUnits = 5.0;

    /// <summary>What maintaining one indexed path adds to every write.</summary>
    public const double PerIndexedPathRequestUnits = 0.15;

    /// <summary>What maintaining one composite index adds to every write.</summary>
    public const double PerCompositeIndexRequestUnits = 0.4;

    /// <summary>Estimates what one write costs under a given policy.</summary>
    /// <param name="policy">The indexing policy.</param>
    /// <returns>The estimated write charge in request units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public static double WriteCost(IndexingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // GAP 12: the base cost, plus every indexed path, plus every composite
        // index.
        //
        // A read pays for an index once, at the moment it uses it. A write pays
        // for every index the document touches, whether or not anything ever
        // queries them. That asymmetry is the entire economics of an indexing
        // policy: excluding paths is the only lever that makes writes cheaper,
        // and it is the lever nobody pulls because the default works.
        // See lessons/10-cosmos-modeling/README.md#indexing-is-a-write-tax-you-choose
        throw new NotImplementedException(
            "GAP 12: implement IndexingPlanner.WriteCost. "
            + "See lessons/10-cosmos-modeling/README.md#indexing-is-a-write-tax-you-choose.");
    }

    /// <summary>
    /// How much of a container's write budget an indexing change gives back.
    /// </summary>
    /// <param name="before">The policy in force today.</param>
    /// <param name="after">The proposed policy.</param>
    /// <param name="writesPerSecond">The sustained write rate.</param>
    /// <returns>Request units per second saved; negative when the change costs more.</returns>
    /// <exception cref="ArgumentNullException">A policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="writesPerSecond"/> is negative.</exception>
    public static double SavingsPerSecond(
        IndexingPolicy before,
        IndexingPolicy after,
        double writesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(writesPerSecond);

        // GAP 13: the difference in write cost, multiplied by the write rate —
        // and it is allowed to be negative.
        //
        // Returning an absolute difference, or clamping at zero, turns the one
        // question worth asking — "is this change worth making?" — into a
        // number that always says yes. Adding a composite index to make one
        // query cheaper makes every write more expensive, and the SIGN of this
        // result is how that trade gets settled.
        // See lessons/10-cosmos-modeling/README.md#indexing-is-a-write-tax-you-choose
        throw new NotImplementedException(
            "GAP 13: implement IndexingPlanner.SavingsPerSecond. "
            + "See lessons/10-cosmos-modeling/README.md#indexing-is-a-write-tax-you-choose.");
    }

    /// <summary>
    /// Decides whether a query needs a composite index that the policy does not
    /// have.
    /// </summary>
    /// <param name="policy">The indexing policy.</param>
    /// <param name="orderByProperties">The properties the query orders by, in order.</param>
    /// <returns>True when the query would be refused for want of an index.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool RequiresMissingCompositeIndex(
        IndexingPolicy policy,
        IReadOnlyList<string> orderByProperties)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(orderByProperties);

        // GAP 14: one property is free; two or more need a composite index that
        // matches exactly, in order.
        //
        // A single-property ORDER BY is served by the range index every indexed
        // path already has, so fewer than two properties is never a problem. A
        // multi-property ORDER BY is not served at all unless some composite
        // index lists exactly those properties in exactly that sequence —
        // Cosmos returns an error rather than doing the sort, which makes this
        // a deployment-time failure and not a slow query. Compare ordinally:
        // JSON property names are case sensitive.
        // See lessons/10-cosmos-modeling/README.md#indexing-is-a-write-tax-you-choose
        throw new NotImplementedException(
            "GAP 14: implement IndexingPlanner.RequiresMissingCompositeIndex. "
            + "See lessons/10-cosmos-modeling/README.md#indexing-is-a-write-tax-you-choose.");
    }
}
