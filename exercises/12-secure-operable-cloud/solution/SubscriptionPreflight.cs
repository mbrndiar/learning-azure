namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>
/// Decides whether a live run may create anything, before it creates anything.
/// </summary>
/// <remarks>
/// Every check here fails closed. A preflight that cannot prove where it is
/// must refuse: the alternative is a lab that creates a resource group in a
/// production subscription because a display name matched two of them and the
/// script took the first.
/// </remarks>
public static class SubscriptionPreflight
{
    /// <summary>Microsoft documents role assignment changes as taking up to this long to take effect.</summary>
    public static readonly TimeSpan RoleAssignmentPropagationBudget = TimeSpan.FromMinutes(10);

    /// <summary>Finds the one subscription a selector names.</summary>
    /// <param name="selector">A subscription id, or a display name.</param>
    /// <param name="candidates">Every subscription the identity can see.</param>
    /// <returns>
    /// The single match, or <see langword="null"/> together with the refusal
    /// that explains why there is not exactly one.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static (SubscriptionRecord? Subscription, PreflightRefusal Refusal) ResolveSubscription(
        string selector,
        IReadOnlyList<SubscriptionRecord> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(candidates);

        // GAP 7: exactly one, or none.
        //
        // An id is unique by construction, so an id match ends the question.
        // A display name is not unique -- "Visual Studio Enterprise" is the
        // name of a great many subscriptions, and two of them in one tenant is
        // ordinary -- so a name that matches twice is AmbiguousSubscription and
        // never "the first one". A name that matches nothing is
        // NoSuchSubscription, which is a different fix and deserves a different
        // word. Match names case-insensitively; match ids exactly.
        // See lessons/12-secure-operable-cloud/README.md#a-preflight-that-cannot-prove-where-it-is-must-refuse
        var byId = candidates.SingleOrDefault(
            candidate => string.Equals(candidate.Id, selector, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return (byId, PreflightRefusal.None);
        }

        var byName = candidates
            .Where(candidate => string.Equals(candidate.Name, selector, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count switch
        {
            1 => (byName[0], PreflightRefusal.None),
            0 => (null, PreflightRefusal.NoSuchSubscription),
            _ => (null, PreflightRefusal.AmbiguousSubscription),
        };
    }

    /// <summary>Runs every check a live lab needs before it creates a resource.</summary>
    /// <param name="session">What the tooling reports about the current session.</param>
    /// <param name="requirements">What the lab needs to be true.</param>
    /// <returns>The verdict, and a sentence naming what to fix.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static PreflightVerdict Check(SessionSnapshot session, PreflightRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requirements);

        // GAP 8: the checks, in the order that produces the most useful message.
        //
        // Sign-in first: everything below it is unanswerable without a session,
        // and "not signed in" is a different instruction from "wrong
        // subscription". Then the subscription, because the tenant of the
        // *resolved* subscription is what matters and a tenant check before
        // resolution would compare against the wrong thing. Then the tenant,
        // then the region allow-list, then the roles the lab needs -- roles
        // last because listing a missing role is only meaningful once the
        // target is known. Never call Connect-AzAccount or az login on the
        // learner's behalf: refusing is the whole point.
        // See lessons/12-secure-operable-cloud/README.md#a-preflight-that-cannot-prove-where-it-is-must-refuse
        if (!session.SignedIn)
        {
            return new PreflightVerdict(
                PreflightRefusal.NotSignedIn,
                null,
                "There is no signed-in session. Sign in yourself, so you can see which identity you are about to spend money with.");
        }

        var (subscription, refusal) = ResolveSubscription(requirements.SubscriptionSelector, session.Subscriptions);
        if (subscription is null)
        {
            var detail = refusal == PreflightRefusal.AmbiguousSubscription
                ? FormattableString.Invariant(
                    $"More than one subscription is called '{requirements.SubscriptionSelector}'. Pass the subscription id instead.")
                : FormattableString.Invariant(
                    $"No subscription matches '{requirements.SubscriptionSelector}'.");
            return new PreflightVerdict(refusal, null, detail);
        }

        if (requirements.TenantId is { } tenant
            && !string.Equals(subscription.TenantId, tenant, StringComparison.OrdinalIgnoreCase))
        {
            return new PreflightVerdict(
                PreflightRefusal.TenantMismatch,
                subscription,
                FormattableString.Invariant(
                    $"Subscription '{subscription.Name}' is in tenant {subscription.TenantId}, not the required {tenant}."));
        }

        if (!requirements.AllowedRegions.Contains(requirements.Region, StringComparer.OrdinalIgnoreCase))
        {
            return new PreflightVerdict(
                PreflightRefusal.RegionNotAllowed,
                subscription,
                FormattableString.Invariant(
                    $"Region '{requirements.Region}' is not in the allowed list ({string.Join(", ", requirements.AllowedRegions)})."));
        }

        var held = session.AssignedRoles.Select(assignment => assignment.RoleName).ToHashSet(StringComparer.Ordinal);
        var missing = requirements.RequiredRoles.Where(role => !held.Contains(role)).ToList();
        if (missing.Count > 0)
        {
            return new PreflightVerdict(
                PreflightRefusal.MissingRole,
                subscription,
                FormattableString.Invariant($"The signed-in identity is missing: {string.Join(", ", missing)}."));
        }

        return new PreflightVerdict(
            PreflightRefusal.None,
            subscription,
            FormattableString.Invariant(
                $"Subscription '{subscription.Name}' ({subscription.Id}) in tenant {subscription.TenantId}, region {requirements.Region}."));
    }

    /// <summary>Waits, within a budget, for a fresh role assignment to take effect.</summary>
    /// <param name="probes">
    /// The result of each data-plane probe, in the order they were made.
    /// </param>
    /// <param name="budget">
    /// The longest the caller will wait. Ten minutes matches what Microsoft
    /// documents for role assignment changes taking effect.
    /// </param>
    /// <returns>How the wait ended, and what to do next.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budget"/> is not positive.</exception>
    public static PropagationResult ConfirmRoleReady(IReadOnlyList<AccessProbe> probes, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        // GAP 9: bounded, and never self-escalating.
        //
        // A brand-new assignment is not instant, so the first 403 after
        // granting a role means nothing. Stop at the first authorized probe and
        // report how long it took. Ignore probes that ran after the budget
        // expired -- a script that keeps polling forever is a script nobody
        // interrupts. And when the budget runs out, the advice is to check the
        // assignment's scope and principal, never to assign a broader role:
        // "it worked when I gave it Contributor" is how least privilege dies.
        // See lessons/12-secure-operable-cloud/README.md#a-fresh-grant-is-not-a-fast-grant
        var used = 0;
        foreach (var probe in probes)
        {
            if (probe.Elapsed > budget)
            {
                break;
            }

            used++;
            if (probe.Authorized)
            {
                return new PropagationResult(
                    PropagationOutcome.Ready,
                    used,
                    probe.Elapsed,
                    "The assignment is in effect; continue.");
            }
        }

        return new PropagationResult(
            PropagationOutcome.TimedOut,
            used,
            budget,
            "Still refused after the budget. Check the assignment's scope and principal id — do not assign a broader role.");
    }
}
