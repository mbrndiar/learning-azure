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
        if (group.Scope.Level != ScopeLevel.ResourceGroup)
        {
            return new TeardownAction(
                TeardownDecision.Refuse,
                null,
                FormattableString.Invariant(
                    $"{group.Scope} is a {group.Scope.Level} scope. A teardown deletes a resource group, never anything above one."));
        }

        if (group.Tags is null)
        {
            return new TeardownAction(
                TeardownDecision.Refuse,
                null,
                FormattableString.Invariant($"'{group.Name}' carries no tags, so nothing proves this run created it."));
        }

        if (!string.Equals(group.Tags.ManagedBy, ResourceTags.CourseManagedBy, StringComparison.Ordinal))
        {
            return new TeardownAction(
                TeardownDecision.Refuse,
                null,
                FormattableString.Invariant(
                    $"'{group.Name}' is managed by '{group.Tags.ManagedBy}', not '{ResourceTags.CourseManagedBy}'."));
        }

        if (!string.Equals(group.Tags.Owner, expectedOwner, StringComparison.OrdinalIgnoreCase))
        {
            return new TeardownAction(
                TeardownDecision.Refuse,
                null,
                FormattableString.Invariant(
                    $"'{group.Name}' belongs to {group.Tags.Owner}, not {expectedOwner}."));
        }

        if (group.ForeignResourceCount > 0)
        {
            return new TeardownAction(
                TeardownDecision.DeleteTaggedResources,
                group.Scope,
                FormattableString.Invariant(
                    $"'{group.Name}' also holds {group.ForeignResourceCount} resource(s) this run did not create, so only the tagged ones go."));
        }

        return new TeardownAction(
            TeardownDecision.DeleteResourceGroup,
            group.Scope,
            FormattableString.Invariant($"Everything in '{group.Name}' belongs to this run."));
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
        var remnants = new List<string>();

        if (probe.ResourceGroupExists)
        {
            remnants.Add("The resource group is still listed: the delete is asynchronous, so poll until it is gone.");
        }

        if (probe.SoftDeletedStorageAccounts > 0)
        {
            remnants.Add(FormattableString.Invariant(
                $"{probe.SoftDeletedStorageAccounts} storage account(s) are recoverable for 14 days; creating a new account with the same name forfeits that."));
        }

        if (probe.SoftDeletedKeyVaults > 0)
        {
            remnants.Add(FormattableString.Invariant(
                $"{probe.SoftDeletedKeyVaults} key vault(s) are soft-deleted; purge them, or the name is unavailable until retention expires."));
        }

        if (probe.SoftDeletedLogAnalyticsWorkspaces > 0)
        {
            remnants.Add(FormattableString.Invariant(
                $"{probe.SoftDeletedLogAnalyticsWorkspaces} Log Analytics workspace(s) are soft-deleted for 14 days and still hold ingested data."));
        }

        if (probe.OrphanedRoleAssignments > 0)
        {
            remnants.Add(FormattableString.Invariant(
                $"{probe.OrphanedRoleAssignments} role assignment(s) no longer resolve to a principal; remove them by id."));
        }

        return new CleanupVerdict(remnants.Count == 0, remnants);
    }
}
