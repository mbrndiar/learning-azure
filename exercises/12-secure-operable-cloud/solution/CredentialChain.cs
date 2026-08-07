namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>
/// Works out which identity <c>DefaultAzureCredential</c> will actually use,
/// what it stepped over to get there, and what an emulator endpoint will accept
/// instead.
/// </summary>
/// <remarks>
/// The chain is a fixed order, and the order is the whole point: it is what
/// makes the same binary authenticate as a service principal in CI, as a
/// managed identity in Azure, and as whoever last ran <c>az login</c> on a
/// laptop. That convenience is also the failure mode, which is why the
/// resolution reports what it shadowed rather than only what it chose.
/// </remarks>
public static class CredentialChain
{
    /// <summary>
    /// The credential sources <c>DefaultAzureCredential</c> tries, in order,
    /// with the interactive one omitted because it is excluded by default.
    /// </summary>
    /// <remarks>
    /// Order is deployment sources first, developer tools second, so a host
    /// with a managed identity never silently authenticates as a human.
    /// </remarks>
    public static readonly IReadOnlyList<CredentialSource> Order =
    [
        CredentialSource.Environment,
        CredentialSource.WorkloadIdentity,
        CredentialSource.ManagedIdentity,
        CredentialSource.VisualStudio,
        CredentialSource.VisualStudioCode,
        CredentialSource.AzureCli,
        CredentialSource.AzurePowerShell,
        CredentialSource.AzureDeveloperCli,
    ];

    /// <summary>Whether one source can produce a token in a given environment.</summary>
    /// <param name="source">The source to test.</param>
    /// <param name="snapshot">What the machine has available.</param>
    /// <returns><see langword="true"/> when the source is configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not part of the chain.</exception>
    public static bool IsAvailable(CredentialSource source, EnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return source switch
        {
            CredentialSource.Environment => snapshot.HasEnvironmentServicePrincipal,
            CredentialSource.WorkloadIdentity => snapshot.HasFederatedTokenFile,
            CredentialSource.ManagedIdentity => snapshot.HasImdsEndpoint,
            CredentialSource.VisualStudio => snapshot.HasVisualStudioAccount,
            CredentialSource.VisualStudioCode => snapshot.HasVisualStudioCodeAccount,
            CredentialSource.AzureCli => snapshot.HasAzureCliLogin,
            CredentialSource.AzurePowerShell => snapshot.HasAzurePowerShellLogin,
            CredentialSource.AzureDeveloperCli => snapshot.HasAzureDeveloperCliLogin,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a chained credential source."),
        };
    }

    /// <summary>Resolves the chain against one environment.</summary>
    /// <param name="snapshot">What the machine has available.</param>
    /// <returns>The winner, what it skipped, and what it shadowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static CredentialResolution Resolve(EnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // GAP 4: first available source wins, and everything behind it is
        // still an identity this process could have become.
        //
        // Walk Order once. Sources ahead of the winner that had nothing
        // configured are "skipped" -- they are why the chain kept going.
        // Sources behind it that *were* configured are "shadowed" -- they are
        // the identities the same binary will use on a machine where the winner
        // is absent, which is exactly how a deployment quietly runs as a
        // developer. With nothing configured at all, the selection is
        // CredentialSource.None and every source is skipped: report that rather
        // than pretending some default applies.
        // See lessons/12-secure-operable-cloud/README.md#the-chain-is-a-fixed-order-not-a-negotiation
        var skipped = new List<CredentialSource>();
        var shadowed = new List<CredentialSource>();
        var selected = CredentialSource.None;

        foreach (var source in Order)
        {
            var available = IsAvailable(source, snapshot);
            if (selected == CredentialSource.None)
            {
                if (available)
                {
                    selected = source;
                }
                else
                {
                    skipped.Add(source);
                }

                continue;
            }

            if (available)
            {
                shadowed.Add(source);
            }
        }

        return new CredentialResolution(selected, skipped, shadowed);
    }

    /// <summary>What an endpoint will accept, given how it is configured.</summary>
    /// <param name="endpoint">The endpoint the application is pointed at.</param>
    /// <param name="resolution">What the credential chain resolved to.</param>
    /// <returns>The method that will work, and why the others will not.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Nothing the endpoint accepts is available: a live account with Shared
    /// Key disabled and no credential in the chain has no way in, and saying so
    /// is more useful than returning a method that will fail at the first call.
    /// </exception>
    public static AuthenticationDecision AuthenticateAgainst(
        ServiceEndpoint endpoint,
        CredentialResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(resolution);

        // GAP 5: the emulator boundary, in one method.
        //
        // Emulators issue no tokens and validate none: Azurite and the Cosmos
        // emulator accept a well-known development key that is published in the
        // documentation, and a TokenCredential against them is not "less
        // secure", it simply does not work. Live accounts are the mirror image:
        // once allowSharedKeyAccess is false, the key is refused with 403 even
        // when it is the correct key, so the only way in is a token -- and if
        // the chain resolved to nothing there is no way in at all.
        // See lessons/12-secure-operable-cloud/README.md#where-the-emulator-boundary-actually-is
        if (endpoint.IsEmulator)
        {
            return new AuthenticationDecision(
                AuthenticationMethod.EmulatorWellKnownKey,
                FormattableString.Invariant(
                    $"{endpoint.Host} is an emulator: it issues no Entra tokens, so the published development credential is the only thing it accepts."));
        }

        if (resolution.Selected != CredentialSource.None)
        {
            return new AuthenticationDecision(
                AuthenticationMethod.EntraToken,
                FormattableString.Invariant(
                    $"{endpoint.Host} is a live endpoint and the chain resolved to {resolution.Selected}."));
        }

        if (endpoint.AllowsSharedKey)
        {
            return new AuthenticationDecision(
                AuthenticationMethod.SharedKey,
                FormattableString.Invariant(
                    $"Nothing in the chain is configured, so {endpoint.Host} can only be reached with its key — which is why the account should not permit one."));
        }

        throw new InvalidOperationException(
            FormattableString.Invariant(
                $"{endpoint.Host} refuses Shared Key and no credential in the chain is configured, so there is no way to authenticate."));
    }

    /// <summary>Decides whether a chain resolution is safe to deploy as it stands.</summary>
    /// <param name="resolution">What the chain resolved to.</param>
    /// <param name="isProduction">Whether this is a production or staging host.</param>
    /// <returns>
    /// A sentence naming the risk, or <see langword="null"/> when there is
    /// nothing to warn about.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> is <see langword="null"/>.</exception>
    public static string? Audit(CredentialResolution resolution, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        // GAP 6: say what is ambiguous, before it is ambiguous in production.
        //
        // Microsoft's own guidance is to replace DefaultAzureCredential with a
        // specific TokenCredential in production, and the reason is this
        // method's subject: a chain with more than one usable source is a chain
        // whose answer depends on the machine. In production, any shadowed
        // source is a finding, and resolving to a developer-tool credential at
        // all is a worse one -- somebody signed in on a server. Locally,
        // shadowing is normal and worth no more than a mention. Nothing
        // configured anywhere is always a finding.
        // See lessons/12-secure-operable-cloud/README.md#one-credential-in-production-not-a-chain
        if (resolution.Selected == CredentialSource.None)
        {
            return "No credential source is configured, so the first call will fail rather than authenticate.";
        }

        var isDeveloperTool = resolution.Selected
            is CredentialSource.VisualStudio
            or CredentialSource.VisualStudioCode
            or CredentialSource.AzureCli
            or CredentialSource.AzurePowerShell
            or CredentialSource.AzureDeveloperCli;

        if (isProduction && isDeveloperTool)
        {
            return FormattableString.Invariant(
                $"A production host resolved to {resolution.Selected}, so the workload is running as a signed-in human; pin ManagedIdentityCredential instead.");
        }

        if (isProduction && resolution.Shadowed.Count > 0)
        {
            return FormattableString.Invariant(
                $"{resolution.Selected} wins here, but {string.Join(", ", resolution.Shadowed)} would win on a host without it; pin one credential.");
        }

        if (resolution.Shadowed.Count > 0)
        {
            return FormattableString.Invariant(
                $"{resolution.Shadowed.Count} other source(s) are configured; on a machine without {resolution.Selected} this process changes identity silently.");
        }

        return null;
    }
}
