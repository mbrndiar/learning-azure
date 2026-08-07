using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// Checks the only code in this course that destroys things: what it refuses to
/// delete, and what it refuses to call finished.
/// </summary>
public sealed class TeardownPlanTests
{
    private static ResourceGroupState Group(
        ResourceScope? scope = null,
        ResourceTags? tags = null,
        int foreignResources = 0) => new(
        (scope ?? Fixtures.ResourceGroup).Path.Split('/')[^1],
        scope ?? Fixtures.ResourceGroup,
        tags ?? Fixtures.Tags(),
        foreignResources);

    [Fact]
    public void Plan_DeletesAGroupThisRunEntirelyOwns()
    {
        var action = TeardownPlan.Plan(Group(), Fixtures.Owner);

        Assert.Equal(TeardownDecision.DeleteResourceGroup, action.Decision);
        Assert.Equal(Fixtures.ResourceGroup, action.Scope);
    }

    [Fact]
    public void Plan_MatchesTheOwnerTagWithoutCaringAboutCase()
    {
        var action = TeardownPlan.Plan(Group(), Fixtures.Owner.ToUpperInvariant());

        Assert.Equal(TeardownDecision.DeleteResourceGroup, action.Decision);
    }

    [Fact]
    public void Plan_RefusesASubscriptionScope()
    {
        // `az group delete` at subscription scope is not a teardown.
        var action = TeardownPlan.Plan(Group(Fixtures.Subscription), Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
        Assert.Null(action.Scope);
    }

    [Fact]
    public void Plan_RefusesAResourceScopeToo()
    {
        var action = TeardownPlan.Plan(Group(Fixtures.StorageAccount), Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
    }

    [Fact]
    public void Plan_RefusesAnUntaggedGroup()
    {
        var action = TeardownPlan.Plan(
            new ResourceGroupState("rg-mystery", Fixtures.ResourceGroup, null, 0),
            Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
        Assert.Contains("tags", action.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_RefusesAGroupAnotherToolManages()
    {
        var action = TeardownPlan.Plan(
            Group(tags: Fixtures.Tags() with { ManagedBy = "terraform" }),
            Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
        Assert.Contains("terraform", action.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RefusesAGroupThatBelongsToSomebodyElse()
    {
        var action = TeardownPlan.Plan(
            Group(tags: Fixtures.Tags() with { Owner = "platform-team" }),
            Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
        Assert.Null(action.Scope);
    }

    [Fact]
    public void Plan_NarrowsToTaggedResourcesWhenSomebodyElsePutSomethingInTheGroup()
    {
        // Deleting the group would take their work with it, and "the script did
        // it" is not a defence.
        var action = TeardownPlan.Plan(Group(foreignResources: 2), Fixtures.Owner);

        Assert.Equal(TeardownDecision.DeleteTaggedResources, action.Decision);
        Assert.Equal(Fixtures.ResourceGroup, action.Scope);
        Assert.Contains("2", action.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ChecksOwnershipBeforeItChecksForeignResources()
    {
        var action = TeardownPlan.Plan(
            Group(tags: Fixtures.Tags() with { ManagedBy = "terraform" }, foreignResources: 3),
            Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
    }

    [Fact]
    public void Plan_ChecksScopeBeforeItChecksTags()
    {
        var action = TeardownPlan.Plan(
            new ResourceGroupState("subscription", Fixtures.Subscription, null, 0),
            Fixtures.Owner);

        Assert.Equal(TeardownDecision.Refuse, action.Decision);
        Assert.Contains("resource group", action.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_RefusesAnEmptyExpectedOwner()
    {
        Assert.Throws<ArgumentException>(() => TeardownPlan.Plan(Group(), " "));
    }

    [Fact]
    public void Verify_AcceptsASubscriptionWithNothingLeft()
    {
        var verdict = TeardownPlan.Verify(new CleanupProbe(false, 0, 0, 0, 0));

        Assert.True(verdict.Complete);
        Assert.Empty(verdict.Remnants);
    }

    [Fact]
    public void Verify_ReportsAGroupThatIsStillListed()
    {
        // `az group delete` returns before the delete finishes unless you wait.
        var verdict = TeardownPlan.Verify(new CleanupProbe(true, 0, 0, 0, 0));

        Assert.False(verdict.Complete);
        Assert.Single(verdict.Remnants);
    }

    [Fact]
    public void Verify_ReportsASoftDeletedStorageAccount()
    {
        var verdict = TeardownPlan.Verify(new CleanupProbe(false, 1, 0, 0, 0));

        Assert.False(verdict.Complete);
        Assert.Contains(verdict.Remnants, remnant => remnant.Contains("14 days", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsASoftDeletedKeyVault()
    {
        var verdict = TeardownPlan.Verify(new CleanupProbe(false, 0, 2, 0, 0));

        Assert.False(verdict.Complete);
        Assert.Contains(verdict.Remnants, remnant => remnant.Contains("key vault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verify_ReportsASoftDeletedWorkspaceThatIsStillHoldingData()
    {
        var verdict = TeardownPlan.Verify(new CleanupProbe(false, 0, 0, 1, 0));

        Assert.False(verdict.Complete);
        Assert.Contains(
            verdict.Remnants,
            remnant => remnant.Contains("Log Analytics", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsOrphanedRoleAssignments()
    {
        // The scope survived the principal, so the assignment survived both.
        var verdict = TeardownPlan.Verify(new CleanupProbe(false, 0, 0, 0, 3));

        Assert.False(verdict.Complete);
        Assert.Contains(verdict.Remnants, remnant => remnant.Contains('3'));
    }

    [Fact]
    public void Verify_ListsEveryRemnantRatherThanTheFirst()
    {
        var verdict = TeardownPlan.Verify(new CleanupProbe(true, 1, 1, 1, 1));

        Assert.Equal(5, verdict.Remnants.Count);
    }

    [Fact]
    public void Verify_KeepsTheRemnantOrderStable()
    {
        var first = TeardownPlan.Verify(new CleanupProbe(true, 1, 1, 1, 1));
        var second = TeardownPlan.Verify(new CleanupProbe(true, 1, 1, 1, 1));

        Assert.Equal(first.Remnants, second.Remnants);
    }

    [Fact]
    public void Verify_DoesNotCallACleanupCompleteBecauseTheGroupIsGone()
    {
        // The group is gone, a recoverable copy of the storage account is not,
        // and two role assignments still point at principals that no longer
        // exist. That is not "clean".
        var verdict = TeardownPlan.Verify(new CleanupProbe(false, 1, 0, 0, 2));

        Assert.False(verdict.Complete);
        Assert.Equal(2, verdict.Remnants.Count);
    }
}
