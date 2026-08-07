namespace LearningAzure.Exercises.StorageAccount;

/// <summary>Chooses a blob access tier from a stated access pattern.</summary>
public static class TierAdvisor
{
    /// <summary>Reads per month at or above which Hot is cheaper than Cool.</summary>
    public const int HotReadThreshold = 4;

    /// <summary>Minimum retention Azure bills for, per tier, in days.</summary>
    /// <param name="tier">The tier to describe.</param>
    /// <returns>The number of days billed even if the blob is deleted sooner.</returns>
    public static int MinimumRetentionDays(AccessTier tier) =>
        // GAP 5 — Hot 0, Cool 30, Cold 90, Archive 180.
        //
        // Early deletion does not save money: deleting a Cool blob on day 3 is
        // billed as 30 days. This is the number that makes "just put everything
        // in Cool" a more expensive decision than it looks.
        throw new NotImplementedException(
            "GAP 5: implement TierAdvisor.MinimumRetentionDays. See "
            + "lessons/03-storage-account/README.md#tiers-trade-storage-price-for-access-price.");

    /// <summary>Recommends a tier for an artifact.</summary>
    /// <param name="pattern">How the artifact is read and how long it is kept.</param>
    /// <returns>The cheapest tier that still satisfies the read requirement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <c>null</c>.</exception>
    public static AccessTier Recommend(AccessPattern pattern) =>
        // GAP 6 — Apply the rules in order; the first match wins.
        //
        //   1. ReadsPerMonth >= HotReadThreshold                   -> Hot
        //      (access charges dominate; a cheaper tier costs more overall)
        //   2. !ReadMustBeImmediate && MinimumRetentionDays >= 180 -> Archive
        //   3. MinimumRetentionDays >= 90                          -> Cold
        //   4. MinimumRetentionDays >= 30                          -> Cool
        //   5. otherwise                                           -> Hot
        //
        // Rule 2 comes before the retention ladder because Archive is offline:
        // no retention period justifies it if a read has to complete now. Rules
        // 3-5 refuse to recommend a tier whose minimum retention exceeds how long
        // the artifact is actually kept, because that bills for storage the
        // expedition never uses.
        throw new NotImplementedException(
            "GAP 6: implement TierAdvisor.Recommend. See "
            + "lessons/03-storage-account/README.md#tiers-trade-storage-price-for-access-price.");
}
