namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>
/// Decides what a teardown is allowed to delete, and judges whether the
/// subscription is genuinely back where it started afterwards.
/// </summary>
/// <remarks>
/// A cleanup routine is the only code in this course that destroys things, so
/// it is the one place where being wrong is not recoverable by re-running it.
/// Both halves therefore fail closed: the plan refuses a scope it cannot prove
/// belongs to this run, and the verification refuses to call a cleanup complete
/// while anything recoverable or chargeable is still there.
/// </remarks>
public static class TeardownPlan
{
    /// <summary>Decides what may be deleted, given what the platform reports.</summary>
    /// <param name="group">The resource group as it exists right now.</param>
    /// <param name="expectedOwner">The owner tag this run wrote.</param>
    /// <returns>The action, its exact scope, and why it is not a broader one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedOwner"/> is empty.</exception>
    public static TeardownAction Plan(ResourceGroupState group, string expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwner);

        // GAP 13: prove ownership, then choose the narrowest delete that finishes the job.
        //
        // Four decisions, in order. A group scoped at anything other than a
        // resource group is refused outright -- a subscription-scoped delete is
        // not a teardown, it is an incident. A group with no tags, the wrong
        // managed-by, or a different owner is refused: this run did not make
        // it. A group this run owns that also contains resources it did not
        // create is deleted resource by resource, because deleting the group
        // would take somebody else's work with it. Only a group that is
        // entirely this run's may be deleted whole, which is the cheap, atomic,
        // and complete option and the reason every lab creates its own group.
        // See lessons/12-secure-operable-cloud/README.md#teardown-is-the-only-code-that-cannot-be-re-run
        throw new NotImplementedException(
            "GAP 13: implement TeardownPlan.Plan. "
            + "See lessons/12-secure-operable-cloud/README.md#teardown-is-the-only-code-that-cannot-be-re-run.");
    }

    /// <summary>Judges whether a completed delete actually removed everything.</summary>
    /// <param name="probe">What the platform still reports after the delete returned.</param>
    /// <returns>Whether the cleanup is complete, and what is left if it is not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    public static CleanupVerdict Verify(CleanupProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        // GAP 14: "deleted" is a state, not an absence.
        //
        // A delete that returned is not a delete that finished, and several
        // services keep a recoverable copy on purpose: a storage account for 14
        // days, a Log Analytics workspace for 14, a key vault for 7 to 90 --
        // and a vault with purge protection cannot be purged early at all,
        // which also means its name stays taken. Role assignments made inside
        // the deleted scope go with it; assignments whose *principal* was
        // deleted do not, and they accumulate as "Identity not found" entries
        // nobody audits. List every remnant with the number found, in this
        // order, so the learner has a work list rather than a boolean.
        // See lessons/12-secure-operable-cloud/README.md#deleted-is-not-gone
        throw new NotImplementedException(
            "GAP 14: implement TeardownPlan.Verify. "
            + "See lessons/12-secure-operable-cloud/README.md#deleted-is-not-gone.");
    }
}
