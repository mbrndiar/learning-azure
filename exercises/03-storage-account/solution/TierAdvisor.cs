namespace LearningAzure.Exercises.StorageAccount;

/// <summary>Chooses a blob access tier from a stated access pattern.</summary>
public static class TierAdvisor
{
    /// <summary>Reads per month at or above which Hot is cheaper than Cool.</summary>
    public const int HotReadThreshold = 4;

    /// <summary>Minimum retention Azure bills for, per tier, in days.</summary>
    /// <param name="tier">The tier to describe.</param>
    /// <returns>The number of days billed even if the blob is deleted sooner.</returns>
    public static int MinimumRetentionDays(AccessTier tier) => tier switch
    {
        AccessTier.Hot => 0,
        AccessTier.Cool => 30,
        AccessTier.Cold => 90,
        AccessTier.Archive => 180,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown access tier."),
    };

    /// <summary>Recommends a tier for an artifact.</summary>
    /// <param name="pattern">How the artifact is read and how long it is kept.</param>
    /// <returns>The cheapest tier that still satisfies the read requirement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <c>null</c>.</exception>
    public static AccessTier Recommend(AccessPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // Access charges dominate long before storage charges do: a frequently
        // read blob is cheaper in the tier with the highest storage price.
        if (pattern.ReadsPerMonth >= HotReadThreshold)
        {
            return AccessTier.Hot;
        }

        // Archive is offline. No retention period makes it correct for an
        // artifact somebody has to read now, because rehydration is hours.
        if (!pattern.ReadMustBeImmediate
            && pattern.MinimumRetentionDays >= MinimumRetentionDays(AccessTier.Archive))
        {
            return AccessTier.Archive;
        }

        if (pattern.MinimumRetentionDays >= MinimumRetentionDays(AccessTier.Cold))
        {
            return AccessTier.Cold;
        }

        if (pattern.MinimumRetentionDays >= MinimumRetentionDays(AccessTier.Cool))
        {
            return AccessTier.Cool;
        }

        // Below 30 days of retention, every cheaper tier bills a minimum the
        // expedition never uses, so Hot is genuinely the cheapest option.
        return AccessTier.Hot;
    }
}
