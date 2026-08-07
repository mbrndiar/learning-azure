namespace LearningAzure.Exercises.StorageAccount.Tests;

/// <summary>Verifies tier selection, including the minimum-retention trap.</summary>
public sealed class TierAdvisorTests
{
    [Theory]
    [InlineData(AccessTier.Hot, 0)]
    [InlineData(AccessTier.Cool, 30)]
    [InlineData(AccessTier.Cold, 90)]
    [InlineData(AccessTier.Archive, 180)]
    public void MinimumRetentionIsBilledEvenIfTheBlobIsDeletedSooner(AccessTier tier, int days)
    {
        Assert.Equal(days, TierAdvisor.MinimumRetentionDays(tier));
    }

    [Fact]
    public void MinimumRetentionIncreasesAsStoragePriceFalls()
    {
        Assert.True(TierAdvisor.MinimumRetentionDays(AccessTier.Hot)
            < TierAdvisor.MinimumRetentionDays(AccessTier.Cool));
        Assert.True(TierAdvisor.MinimumRetentionDays(AccessTier.Cool)
            < TierAdvisor.MinimumRetentionDays(AccessTier.Cold));
        Assert.True(TierAdvisor.MinimumRetentionDays(AccessTier.Cold)
            < TierAdvisor.MinimumRetentionDays(AccessTier.Archive));
    }

    [Fact]
    public void FrequentlyReadArtifactsBelongInHot()
    {
        var pattern = new AccessPattern(ReadsPerMonth: 40, MinimumRetentionDays: 3650, ReadMustBeImmediate: true);

        Assert.Equal(AccessTier.Hot, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void FrequentReadsBeatLongRetention()
    {
        // Access charges dominate. A ten-year retention does not make a blob read
        // fifty times a month cheaper in Archive; it makes it dramatically dearer.
        var pattern = new AccessPattern(ReadsPerMonth: 50, MinimumRetentionDays: 3650, ReadMustBeImmediate: false);

        Assert.Equal(AccessTier.Hot, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void ShortLivedArtifactsStayInHotEvenIfRarelyRead()
    {
        // Cool bills 30 days. An artifact kept for 7 costs more in Cool than Hot.
        var pattern = new AccessPattern(ReadsPerMonth: 0, MinimumRetentionDays: 7, ReadMustBeImmediate: true);

        Assert.Equal(AccessTier.Hot, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void ThirtyDayRetentionUnlocksCool()
    {
        var pattern = new AccessPattern(ReadsPerMonth: 1, MinimumRetentionDays: 30, ReadMustBeImmediate: true);

        Assert.Equal(AccessTier.Cool, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void NinetyDayRetentionUnlocksCold()
    {
        var pattern = new AccessPattern(ReadsPerMonth: 1, MinimumRetentionDays: 90, ReadMustBeImmediate: true);

        Assert.Equal(AccessTier.Cold, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void ArchiveIsRefusedWhenAReadMustBeImmediate()
    {
        // Rehydration is measured in hours. No retention period makes Archive
        // correct for an artifact somebody has to read now.
        var pattern = new AccessPattern(ReadsPerMonth: 0, MinimumRetentionDays: 3650, ReadMustBeImmediate: true);

        Assert.NotEqual(AccessTier.Archive, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void ArchiveIsChosenForColdLongLivedArtifacts()
    {
        var pattern = new AccessPattern(ReadsPerMonth: 0, MinimumRetentionDays: 3650, ReadMustBeImmediate: false);

        Assert.Equal(AccessTier.Archive, TierAdvisor.Recommend(pattern));
    }

    [Fact]
    public void ARecommendedTierNeverBillsForRetentionTheArtifactDoesNotUse()
    {
        var patterns = new[]
        {
            new AccessPattern(0, 1, true),
            new AccessPattern(0, 29, true),
            new AccessPattern(1, 30, true),
            new AccessPattern(1, 89, true),
            new AccessPattern(1, 90, true),
            new AccessPattern(0, 179, false),
            new AccessPattern(0, 180, false),
        };

        foreach (var pattern in patterns)
        {
            var tier = TierAdvisor.Recommend(pattern);
            Assert.True(
                TierAdvisor.MinimumRetentionDays(tier) <= pattern.MinimumRetentionDays,
                $"{tier} bills {TierAdvisor.MinimumRetentionDays(tier)} days for an artifact kept "
                + $"{pattern.MinimumRetentionDays} days.");
        }
    }

    [Fact]
    public void TheHotReadThresholdIsSmall()
    {
        // It takes very few reads per month before Hot wins, which is why "we
        // hardly ever read it" needs a number attached before it changes a tier.
        Assert.InRange(TierAdvisor.HotReadThreshold, 1, 10);
    }

    [Fact]
    public void RecommendRejectsANullPattern()
    {
        Assert.Throws<ArgumentNullException>(() => TierAdvisor.Recommend(null!));
    }
}
