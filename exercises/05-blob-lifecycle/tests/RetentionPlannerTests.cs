namespace LearningAzure.Exercises.BlobLifecycle.Tests;

/// <summary>Asserts that a retention plan keeps the promise it claims to.</summary>
public sealed class RetentionPlannerTests
{
    private static RetentionPlan Sound() => new(
        SoftDeleteRetentionDays: 30,
        VersioningEnabled: true,
        VersionRetentionDays: 90,
        Transitions:
        [
            new TierTransition(AccessTier.Cool, 30),
            new TierTransition(AccessTier.Archive, 180),
        ]);

    [Fact]
    public void ASoundPlanHasNoViolations()
    {
        Assert.Empty(RetentionPlanner.Evaluate(Sound()));
    }

    [Fact]
    public void SoftDeleteOffIsAViolation()
    {
        var violations = RetentionPlanner.Evaluate(Sound() with { SoftDeleteRetentionDays = 0 });

        Assert.Contains(violations, v => v.Setting == nameof(RetentionPlan.SoftDeleteRetentionDays));
    }

    [Fact]
    public void SoftDeleteBeyondTheServiceMaximumIsAViolation()
    {
        var violations = RetentionPlanner.Evaluate(
            Sound() with { SoftDeleteRetentionDays = RetentionPlanner.MaximumSoftDeleteDays + 1 });

        Assert.Contains(violations, v => v.Setting == nameof(RetentionPlan.SoftDeleteRetentionDays));
    }

    [Fact]
    public void VersioningOffIsNotALossWhenSoftDeleteCoversTheOverwriteWindow()
    {
        var violations = RetentionPlanner.Evaluate(
            Sound() with { VersioningEnabled = false, VersionRetentionDays = 0 });

        Assert.DoesNotContain(violations, v => v.Setting == nameof(RetentionPlan.VersioningEnabled));
    }

    [Fact]
    public void VersionsKeptForeverAreAViolation()
    {
        var violations = RetentionPlanner.Evaluate(Sound() with { VersionRetentionDays = 0 });

        Assert.Contains(violations, v => v.Setting == nameof(RetentionPlan.VersionRetentionDays));
    }

    [Fact]
    public void ATransitionBeforeTheTierMinimumIsAViolation()
    {
        var violations = RetentionPlanner.Evaluate(Sound() with
        {
            Transitions = [new TierTransition(AccessTier.Archive, 10)],
        });

        Assert.Contains(violations, v => v.Setting.Contains("Archive", StringComparison.Ordinal));
    }

    [Fact]
    public void ATransitionThatMovesBackTowardsHotIsAViolation()
    {
        var violations = RetentionPlanner.Evaluate(Sound() with
        {
            Transitions =
            [
                new TierTransition(AccessTier.Archive, 180),
                new TierTransition(AccessTier.Cool, 200),
            ],
        });

        Assert.Contains(violations, v => v.Setting.Contains("Cool", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoTransitionsOnTheSameDayAreAViolation()
    {
        var violations = RetentionPlanner.Evaluate(Sound() with
        {
            Transitions =
            [
                new TierTransition(AccessTier.Cool, 30),
                new TierTransition(AccessTier.Archive, 30),
            ],
        });

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void EveryViolationIsReportedNotJustTheFirst()
    {
        var broken = new RetentionPlan(
            SoftDeleteRetentionDays: 0,
            VersioningEnabled: false,
            VersionRetentionDays: -1,
            Transitions:
            [
                new TierTransition(AccessTier.Archive, 5),
                new TierTransition(AccessTier.Cool, 3),
            ]);

        Assert.True(RetentionPlanner.Evaluate(broken).Count >= 4, "Every fault should be reported at once.");
    }

    [Fact]
    public void ANullPlanIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => RetentionPlanner.Evaluate(null!));
    }

    [Fact]
    public void HotHasNoMinimumRetention()
    {
        Assert.Equal(0, RetentionPlanner.MinimumDaysFor(AccessTier.Hot));
    }

    [Fact]
    public void CoolAndArchiveHaveMinimumRetention()
    {
        Assert.Equal(30, RetentionPlanner.MinimumDaysFor(AccessTier.Cool));
        Assert.Equal(180, RetentionPlanner.MinimumDaysFor(AccessTier.Archive));
    }

    [Fact]
    public void ColderTiersHaveLongerMinimums()
    {
        Assert.True(
            RetentionPlanner.MinimumDaysFor(AccessTier.Archive) > RetentionPlanner.MinimumDaysFor(AccessTier.Cool));
    }

    [Fact]
    public void AnUnknownTierIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPlanner.MinimumDaysFor((AccessTier)99));
    }

    [Fact]
    public void AnOverwriteWithVersioningOffUsesTheSoftDeletedSnapshot()
    {
        // The most commonly discovered-too-late fact in this module.
        var plan = Sound() with { VersioningEnabled = false };

        var answer = RetentionPlanner.RecoveryPath(plan, wasOverwritten: true, daysAgo: 0);

        Assert.StartsWith("Recoverable", answer, StringComparison.Ordinal);
        Assert.Contains("snapshot", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOverwriteWithVersioningOnIsRecoverable()
    {
        var answer = RetentionPlanner.RecoveryPath(Sound(), wasOverwritten: true, daysAgo: 3);

        Assert.StartsWith("Recoverable", answer, StringComparison.Ordinal);
        Assert.Contains("version", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOverwriteOlderThanVersionRetentionIsUnrecoverable()
    {
        var answer = RetentionPlanner.RecoveryPath(Sound(), wasOverwritten: true, daysAgo: 91);

        Assert.StartsWith("Unrecoverable", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeleteInsideTheSoftDeleteWindowIsRecoverable()
    {
        var answer = RetentionPlanner.RecoveryPath(Sound(), wasOverwritten: false, daysAgo: 29);

        Assert.StartsWith("Recoverable", answer, StringComparison.Ordinal);
        Assert.Contains("soft delete", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADeleteOutsideTheSoftDeleteWindowIsUnrecoverable()
    {
        var answer = RetentionPlanner.RecoveryPath(Sound(), wasOverwritten: false, daysAgo: 31);

        Assert.StartsWith("Unrecoverable", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeleteWithSoftDeleteOffIsUnrecoverable()
    {
        var plan = Sound() with { SoftDeleteRetentionDays = 0 };

        var answer = RetentionPlanner.RecoveryPath(plan, wasOverwritten: false, daysAgo: 0);

        Assert.StartsWith("Unrecoverable", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftDeleteRescuesARecentOverwriteInAFlatNamespaceAccount()
    {
        var plan = Sound() with { VersioningEnabled = false, VersionRetentionDays = 0, SoftDeleteRetentionDays = 30 };

        var overwrite = RetentionPlanner.RecoveryPath(plan, wasOverwritten: true, daysAgo: 1);
        var delete = RetentionPlanner.RecoveryPath(plan, wasOverwritten: false, daysAgo: 1);

        Assert.StartsWith("Recoverable", overwrite, StringComparison.Ordinal);
        Assert.StartsWith("Recoverable", delete, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeAgeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetentionPlanner.RecoveryPath(Sound(), wasOverwritten: false, daysAgo: -1));
    }

    [Fact]
    public void ANullPlanIsRejectedByRecoveryPath()
    {
        Assert.Throws<ArgumentNullException>(
            () => RetentionPlanner.RecoveryPath(null!, wasOverwritten: false, daysAgo: 1));
    }
}
