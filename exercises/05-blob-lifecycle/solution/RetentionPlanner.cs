namespace LearningAzure.Exercises.BlobLifecycle;

/// <summary>Checks a retention plan against what the service will actually do.</summary>
/// <remarks>
/// Every rule below exists because the setting is accepted, applied, and then
/// costs money or loses data. Azure does not reject a plan that moves a blob to
/// Archive after three days; it charges the early-deletion penalty instead.
/// </remarks>
public static class RetentionPlanner
{
    /// <summary>Minimum days Cool must be held before deletion avoids an early-deletion charge.</summary>
    public const int CoolMinimumDays = 30;

    /// <summary>Minimum days Archive must be held before deletion avoids an early-deletion charge.</summary>
    public const int ArchiveMinimumDays = 180;

    /// <summary>Maximum soft-delete retention the service accepts.</summary>
    public const int MaximumSoftDeleteDays = 365;

    /// <summary>Reports every way <paramref name="plan"/> fails to keep its promise.</summary>
    /// <param name="plan">The plan to check.</param>
    /// <returns>Every violation, not just the first.</returns>
    public static IReadOnlyList<RetentionViolation> Evaluate(RetentionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var violations = new List<RetentionViolation>();

        // GAP 8 — Report EVERY violation.
        //
        // Returning the first one turns fixing a plan into a guessing game with
        // one answer per deploy. An operator wants the whole list.
        if (plan.SoftDeleteRetentionDays is < 0 or > MaximumSoftDeleteDays)
        {
            violations.Add(new RetentionViolation(
                nameof(plan.SoftDeleteRetentionDays),
                $"must be between 0 and {MaximumSoftDeleteDays}; {plan.SoftDeleteRetentionDays} is rejected by the service."));
        }

        if (plan.SoftDeleteRetentionDays == 0)
        {
            violations.Add(new RetentionViolation(
                nameof(plan.SoftDeleteRetentionDays),
                "is 0, so a deleted artifact is unrecoverable the instant the delete returns."));
        }

        if (!plan.VersioningEnabled)
        {
            violations.Add(new RetentionViolation(
                nameof(plan.VersioningEnabled),
                "is off, so an overwrite silently destroys the previous bytes; soft delete does not cover overwrites."));
        }

        if (plan.VersioningEnabled && plan.VersionRetentionDays == 0)
        {
            violations.Add(new RetentionViolation(
                nameof(plan.VersionRetentionDays),
                "is 0, so every version is kept forever and the bill grows without bound."));
        }

        if (plan.VersionRetentionDays < 0)
        {
            violations.Add(new RetentionViolation(
                nameof(plan.VersionRetentionDays),
                $"is negative ({plan.VersionRetentionDays})."));
        }

        var previousDay = 0;
        AccessTier? previousTier = null;
        foreach (var transition in plan.Transitions)
        {
            if (transition.AfterDays <= previousDay)
            {
                violations.Add(new RetentionViolation(
                    $"{transition.Tier} transition",
                    $"fires on day {transition.AfterDays}, which is not after the previous transition on day {previousDay}."));
            }

            if (previousTier is not null && transition.Tier <= previousTier)
            {
                violations.Add(new RetentionViolation(
                    $"{transition.Tier} transition",
                    $"moves back towards {previousTier}; lifecycle transitions only go one way, towards colder."));
            }

            var minimum = MinimumDaysFor(transition.Tier);
            if (transition.Tier != AccessTier.Hot && transition.AfterDays < minimum)
            {
                violations.Add(new RetentionViolation(
                    $"{transition.Tier} transition",
                    $"fires on day {transition.AfterDays}, before the {minimum}-day minimum, so an early delete is billed as if the blob had been kept the full term."));
            }

            previousDay = transition.AfterDays;
            previousTier = transition.Tier;
        }

        return violations;
    }

    /// <summary>The minimum retention the tier bills for, whatever the blob's actual lifetime.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>Minimum billed days.</returns>
    public static int MinimumDaysFor(AccessTier tier) =>
        // GAP 9 — Hot has no minimum; Cool and Archive do, and they are the
        // reason "just move everything to Archive" costs more, not less.
        tier switch
        {
            AccessTier.Hot => 0,
            AccessTier.Cool => CoolMinimumDays,
            AccessTier.Archive => ArchiveMinimumDays,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown access tier."),
        };

    /// <summary>Decides how a lost artifact can be recovered, if at all.</summary>
    /// <param name="plan">The retention plan in force.</param>
    /// <param name="wasOverwritten"><c>true</c> for an overwrite, <c>false</c> for a delete.</param>
    /// <param name="daysAgo">Days since the loss.</param>
    /// <returns>A one-sentence answer an operator can act on.</returns>
    public static string RecoveryPath(RetentionPlan plan, bool wasOverwritten, int daysAgo)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(daysAgo);

        // GAP 10 — Soft delete and versioning cover DIFFERENT losses.
        //
        // Soft delete recovers a deleted blob. It does nothing for an overwrite,
        // because nothing was deleted. That single sentence is the most commonly
        // discovered-too-late fact about Blob Storage retention.
        if (wasOverwritten)
        {
            if (!plan.VersioningEnabled)
            {
                return "Unrecoverable: the blob was overwritten and versioning was off. Soft delete does not cover overwrites.";
            }

            return plan.VersionRetentionDays == 0 || daysAgo <= plan.VersionRetentionDays
                ? "Recoverable: promote the previous version, which versioning kept."
                : $"Unrecoverable: the previous version expired after {plan.VersionRetentionDays} days.";
        }

        if (plan.SoftDeleteRetentionDays == 0)
        {
            return "Unrecoverable: the blob was deleted and soft delete was off.";
        }

        return daysAgo <= plan.SoftDeleteRetentionDays
            ? "Recoverable: undelete the blob, which soft delete is still holding."
            : $"Unrecoverable: soft delete kept it for {plan.SoftDeleteRetentionDays} days and that window closed.";
    }
}
