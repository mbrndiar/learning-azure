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
    public static IReadOnlyList<RetentionViolation> Evaluate(RetentionPlan plan) =>
        // GAP 8 — Report EVERY violation, not just the first.
        //
        // Returning the first one turns fixing a plan into a guessing game with
        // one answer per deploy. An operator wants the whole list.
        //
        // The rules, each with a RetentionViolation naming the setting:
        //   - SoftDeleteRetentionDays outside 0..MaximumSoftDeleteDays
        //   - SoftDeleteRetentionDays == 0 (a delete is instantly permanent)
        //   - VersioningEnabled with VersionRetentionDays == 0 (kept forever)
        //   - VersionRetentionDays negative
        //   - a transition that does not fire strictly after the previous one
        //   - a transition that moves back towards a warmer tier
        //   - a non-Hot transition firing before MinimumDaysFor(tier)
        throw new NotImplementedException(
            "GAP 8: implement RetentionPlanner.Evaluate. See "
            + "lessons/05-blob-lifecycle/README.md#retention-is-three-independent-promises.");

    /// <summary>The minimum retention the tier bills for, whatever the blob's actual lifetime.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>Minimum billed days.</returns>
    public static int MinimumDaysFor(AccessTier tier) =>
        // GAP 9 — Hot has no minimum; Cool and Archive do, and they are the
        // reason "just move everything to Archive" costs more, not less.
        throw new NotImplementedException(
            "GAP 9: implement RetentionPlanner.MinimumDaysFor. See "
            + "lessons/05-blob-lifecycle/README.md#retention-is-three-independent-promises.");

    /// <summary>Decides how a lost artifact can be recovered, if at all.</summary>
    /// <param name="plan">The retention plan in force.</param>
    /// <param name="wasOverwritten"><c>true</c> for an overwrite, <c>false</c> for a delete.</param>
    /// <param name="daysAgo">Days since the loss.</param>
    /// <returns>A one-sentence answer an operator can act on.</returns>
    public static string RecoveryPath(RetentionPlan plan, bool wasOverwritten, int daysAgo) =>
        // GAP 10 — Flat-namespace block blobs have two recovery paths.
        //
        // Versioning preserves a first-class previous version. Without
        // versioning, blob soft delete preserves the pre-overwrite state as a
        // soft-deleted snapshot for its retention window. HNS accounts do not
        // get overwrite protection from soft delete; this course uses flat
        // namespace storage accounts.
        //
        // Return a sentence starting with "Recoverable:" or "Unrecoverable:".
        // The evaluator asserts on that prefix and on the reason, so say which
        // mechanism applies and why the window is open or closed.
        throw new NotImplementedException(
            "GAP 10: implement RetentionPlanner.RecoveryPath. See "
            + "lessons/05-blob-lifecycle/README.md#retention-is-three-independent-promises.");
}
