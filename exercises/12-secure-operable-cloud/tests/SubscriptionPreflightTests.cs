using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// Checks that the preflight refuses every condition under which a live run
/// would create resources somewhere nobody intended, and that a fresh role
/// assignment is waited for rather than worked around.
/// </summary>
public sealed class SubscriptionPreflightTests
{
    private static readonly SubscriptionRecord Sandbox =
        new(Fixtures.SubscriptionId, "Expedition Sandbox", Fixtures.TenantId);

    private static readonly SubscriptionRecord Production =
        new(Fixtures.OtherSubscriptionId, "Visual Studio Enterprise", Fixtures.TenantId);

    private static readonly SubscriptionRecord SecondNamesake =
        new("33333333-3333-3333-3333-333333333333", "Visual Studio Enterprise", Fixtures.TenantId);

    [Fact]
    public void ResolveSubscription_MatchesAnIdExactly()
    {
        var (subscription, refusal) = SubscriptionPreflight.ResolveSubscription(
            Fixtures.SubscriptionId,
            [Production, Sandbox]);

        Assert.Equal(Sandbox, subscription);
        Assert.Equal(PreflightRefusal.None, refusal);
    }

    [Fact]
    public void ResolveSubscription_MatchesASingleDisplayName()
    {
        var (subscription, refusal) = SubscriptionPreflight.ResolveSubscription(
            "expedition sandbox",
            [Production, Sandbox]);

        Assert.Equal(Sandbox, subscription);
        Assert.Equal(PreflightRefusal.None, refusal);
    }

    [Fact]
    public void ResolveSubscription_RefusesADisplayNameThatMatchesTwice()
    {
        // Taking the first match here is how a lab creates a resource group in
        // the wrong subscription and nobody notices until the bill.
        var (subscription, refusal) = SubscriptionPreflight.ResolveSubscription(
            "Visual Studio Enterprise",
            [Production, SecondNamesake, Sandbox]);

        Assert.Null(subscription);
        Assert.Equal(PreflightRefusal.AmbiguousSubscription, refusal);
    }

    [Fact]
    public void ResolveSubscription_SaysNoSuchSubscriptionRatherThanAmbiguous()
    {
        var (subscription, refusal) = SubscriptionPreflight.ResolveSubscription("Expedition Prod", [Sandbox]);

        Assert.Null(subscription);
        Assert.Equal(PreflightRefusal.NoSuchSubscription, refusal);
    }

    [Fact]
    public void ResolveSubscription_RefusesAnEmptySelector()
    {
        Assert.Throws<ArgumentException>(() => SubscriptionPreflight.ResolveSubscription("  ", [Sandbox]));
    }

    [Fact]
    public void Check_PassesWhenEverythingIsWhereItShouldBe()
    {
        var verdict = SubscriptionPreflight.Check(
            Fixtures.SignedIn(new RoleAssignment("Contributor", Fixtures.Subscription, RoleSystem.AzureRbac)),
            Fixtures.Requirements(requiredRoles: ["Contributor"]));

        Assert.Equal(PreflightRefusal.None, verdict.Refusal);
        Assert.Equal(Fixtures.SubscriptionId, verdict.Subscription?.Id);
    }

    [Fact]
    public void Check_ReportsTheResolvedSubscriptionSoTheLearnerCanReadItBack()
    {
        var verdict = SubscriptionPreflight.Check(Fixtures.SignedIn(), Fixtures.Requirements());

        Assert.Contains(Fixtures.SubscriptionId, verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesWhenThereIsNoSession()
    {
        var verdict = SubscriptionPreflight.Check(
            new SessionSnapshot(false, null, null, [], []),
            Fixtures.Requirements());

        Assert.Equal(PreflightRefusal.NotSignedIn, verdict.Refusal);
    }

    [Fact]
    public void Check_ChecksSignInBeforeAnythingElse()
    {
        // Without a session there are no subscriptions to be wrong about, and
        // "no subscription matches" would send the learner to fix the wrong
        // thing.
        var verdict = SubscriptionPreflight.Check(
            new SessionSnapshot(false, null, null, [], []),
            Fixtures.Requirements(selector: "Nothing Like This"));

        Assert.Equal(PreflightRefusal.NotSignedIn, verdict.Refusal);
    }

    [Fact]
    public void Check_RefusesAnAmbiguousSubscriptionAndSaysToPassAnId()
    {
        var session = Fixtures.SignedIn() with { Subscriptions = [Production, SecondNamesake] };

        var verdict = SubscriptionPreflight.Check(
            session,
            Fixtures.Requirements(selector: "Visual Studio Enterprise"));

        Assert.Equal(PreflightRefusal.AmbiguousSubscription, verdict.Refusal);
        Assert.Null(verdict.Subscription);
        Assert.Contains("subscription id", verdict.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_RefusesASubscriptionInAnotherTenant()
    {
        var session = Fixtures.SignedIn() with
        {
            Subscriptions = [Sandbox with { TenantId = "ffffffff-ffff-ffff-ffff-ffffffffffff" }],
        };

        var verdict = SubscriptionPreflight.Check(session, Fixtures.Requirements());

        Assert.Equal(PreflightRefusal.TenantMismatch, verdict.Refusal);
    }

    [Fact]
    public void Check_RefusesARegionOutsideTheAllowList()
    {
        var verdict = SubscriptionPreflight.Check(Fixtures.SignedIn(), Fixtures.Requirements(region: "eastus"));

        Assert.Equal(PreflightRefusal.RegionNotAllowed, verdict.Refusal);
    }

    [Fact]
    public void Check_RefusesWhenARequiredRoleIsMissingAndNamesIt()
    {
        var verdict = SubscriptionPreflight.Check(
            Fixtures.SignedIn(new RoleAssignment("Reader", Fixtures.Subscription, RoleSystem.AzureRbac)),
            Fixtures.Requirements(requiredRoles: ["Contributor", "User Access Administrator"]));

        Assert.Equal(PreflightRefusal.MissingRole, verdict.Refusal);
        Assert.Contains("User Access Administrator", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("Contributor", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_StillReportsTheSubscriptionWhenTheRoleCheckFails()
    {
        // The learner needs to know which subscription to grant the role in.
        var verdict = SubscriptionPreflight.Check(
            Fixtures.SignedIn(),
            Fixtures.Requirements(requiredRoles: ["Contributor"]));

        Assert.Equal(PreflightRefusal.MissingRole, verdict.Refusal);
        Assert.NotNull(verdict.Subscription);
    }

    [Fact]
    public void Check_ChecksTheSubscriptionBeforeTheRegion()
    {
        var session = Fixtures.SignedIn() with { Subscriptions = [Production] };

        var verdict = SubscriptionPreflight.Check(
            session,
            Fixtures.Requirements(selector: Fixtures.SubscriptionId, region: "eastus"));

        Assert.Equal(PreflightRefusal.NoSuchSubscription, verdict.Refusal);
    }

    [Fact]
    public void RoleAssignmentPropagationBudget_MatchesTheDocumentedTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), SubscriptionPreflight.RoleAssignmentPropagationBudget);
    }

    [Fact]
    public void ConfirmRoleReady_StopsAtTheFirstAuthorizedProbe()
    {
        var result = SubscriptionPreflight.ConfirmRoleReady(
            [
                new AccessProbe(TimeSpan.FromSeconds(5), false),
                new AccessProbe(TimeSpan.FromSeconds(20), false),
                new AccessProbe(TimeSpan.FromSeconds(35), true),
                new AccessProbe(TimeSpan.FromSeconds(50), true),
            ],
            SubscriptionPreflight.RoleAssignmentPropagationBudget);

        Assert.Equal(PropagationOutcome.Ready, result.Outcome);
        Assert.Equal(3, result.Probes);
        Assert.Equal(TimeSpan.FromSeconds(35), result.Waited);
    }

    [Fact]
    public void ConfirmRoleReady_DoesNotTreatTheFirstRefusalAsFailure()
    {
        // A brand new assignment is documented as taking up to ten minutes, so
        // one 403 immediately after granting proves nothing at all.
        var result = SubscriptionPreflight.ConfirmRoleReady(
            [new AccessProbe(TimeSpan.FromSeconds(2), false), new AccessProbe(TimeSpan.FromMinutes(4), true)],
            SubscriptionPreflight.RoleAssignmentPropagationBudget);

        Assert.Equal(PropagationOutcome.Ready, result.Outcome);
    }

    [Fact]
    public void ConfirmRoleReady_SucceedsOnTheFirstProbeWithoutWaiting()
    {
        var result = SubscriptionPreflight.ConfirmRoleReady(
            [new AccessProbe(TimeSpan.Zero, true)],
            SubscriptionPreflight.RoleAssignmentPropagationBudget);

        Assert.Equal(PropagationOutcome.Ready, result.Outcome);
        Assert.Equal(1, result.Probes);
    }

    [Fact]
    public void ConfirmRoleReady_GivesUpWhenTheBudgetExpires()
    {
        var result = SubscriptionPreflight.ConfirmRoleReady(
            [
                new AccessProbe(TimeSpan.FromMinutes(2), false),
                new AccessProbe(TimeSpan.FromMinutes(6), false),
                new AccessProbe(TimeSpan.FromMinutes(10), false),
            ],
            SubscriptionPreflight.RoleAssignmentPropagationBudget);

        Assert.Equal(PropagationOutcome.TimedOut, result.Outcome);
        Assert.Equal(SubscriptionPreflight.RoleAssignmentPropagationBudget, result.Waited);
    }

    [Fact]
    public void ConfirmRoleReady_IgnoresProbesTakenAfterTheBudget()
    {
        // Polling past the budget is how a "quick" lab step runs for an hour.
        var result = SubscriptionPreflight.ConfirmRoleReady(
            [
                new AccessProbe(TimeSpan.FromMinutes(9), false),
                new AccessProbe(TimeSpan.FromMinutes(31), true),
            ],
            SubscriptionPreflight.RoleAssignmentPropagationBudget);

        Assert.Equal(PropagationOutcome.TimedOut, result.Outcome);
        Assert.Equal(1, result.Probes);
    }

    [Fact]
    public void ConfirmRoleReady_NeverSuggestsABroaderRole()
    {
        var result = SubscriptionPreflight.ConfirmRoleReady(
            [new AccessProbe(TimeSpan.FromMinutes(1), false)],
            TimeSpan.FromMinutes(1));

        Assert.DoesNotContain("Owner", result.Advice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contributor", result.Advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scope", result.Advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmRoleReady_TimesOutOnAnEmptyProbeSequence()
    {
        var result = SubscriptionPreflight.ConfirmRoleReady([], TimeSpan.FromMinutes(10));

        Assert.Equal(PropagationOutcome.TimedOut, result.Outcome);
        Assert.Equal(0, result.Probes);
    }

    [Fact]
    public void ConfirmRoleReady_RefusesANonPositiveBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SubscriptionPreflight.ConfirmRoleReady([new AccessProbe(TimeSpan.Zero, true)], TimeSpan.Zero));
    }
}
