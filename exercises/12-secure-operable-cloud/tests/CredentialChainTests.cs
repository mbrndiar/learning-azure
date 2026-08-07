using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// Checks that the credential chain resolves the way the SDK documents it, that
/// the emulator boundary is respected in both directions, and that an ambiguous
/// resolution is reported rather than enjoyed.
/// </summary>
public sealed class CredentialChainTests
{
    private static readonly EnvironmentSnapshot Laptop = new(
        HasVisualStudioCodeAccount: true,
        HasAzureCliLogin: true,
        HasAzureDeveloperCliLogin: true);

    private static readonly EnvironmentSnapshot AppServiceWithManagedIdentity = new(HasImdsEndpoint: true);

    private static readonly EnvironmentSnapshot KubernetesWorkload = new(
        HasFederatedTokenFile: true,
        HasImdsEndpoint: true);

    private static readonly EnvironmentSnapshot BuildAgent = new(HasEnvironmentServicePrincipal: true);

    private static readonly EnvironmentSnapshot Nothing = new();

    [Fact]
    public void Order_StartsWithTheEnvironmentAndEndsWithDeveloperTools()
    {
        Assert.Equal(CredentialSource.Environment, CredentialChain.Order[0]);
        Assert.Equal(CredentialSource.AzureDeveloperCli, CredentialChain.Order[^1]);
    }

    [Fact]
    public void Order_PutsEveryDeploymentSourceAheadOfEveryDeveloperTool()
    {
        var lastDeployment = CredentialChain.Order.ToList().IndexOf(CredentialSource.ManagedIdentity);
        var firstDeveloperTool = CredentialChain.Order.ToList().IndexOf(CredentialSource.VisualStudio);

        Assert.True(lastDeployment < firstDeveloperTool);
    }

    [Fact]
    public void Order_DoesNotIncludeAnInteractiveCredential()
    {
        // InteractiveBrowserCredential is excluded from the chain by default. A
        // server that opens a browser is a server that hangs.
        Assert.Equal(8, CredentialChain.Order.Count);
    }

    [Fact]
    public void Resolve_PicksTheManagedIdentityOnAnAzureHost()
    {
        var resolution = CredentialChain.Resolve(AppServiceWithManagedIdentity);

        Assert.Equal(CredentialSource.ManagedIdentity, resolution.Selected);
    }

    [Fact]
    public void Resolve_PrefersWorkloadIdentityToTheHostIdentity()
    {
        // A pod with a federated token and a node with IMDS are two different
        // identities, and the chain picks the pod's.
        var resolution = CredentialChain.Resolve(KubernetesWorkload);

        Assert.Equal(CredentialSource.WorkloadIdentity, resolution.Selected);
        Assert.Contains(CredentialSource.ManagedIdentity, resolution.Shadowed);
    }

    [Fact]
    public void Resolve_PrefersTheEnvironmentServicePrincipalToEverything()
    {
        var resolution = CredentialChain.Resolve(BuildAgent with { HasImdsEndpoint = true, HasAzureCliLogin = true });

        Assert.Equal(CredentialSource.Environment, resolution.Selected);
        Assert.Empty(resolution.Skipped);
    }

    [Fact]
    public void Resolve_RecordsWhatItSteppedOverToGetThere()
    {
        var resolution = CredentialChain.Resolve(Laptop);

        Assert.Equal(CredentialSource.VisualStudioCode, resolution.Selected);
        Assert.Equal(
            [
                CredentialSource.Environment,
                CredentialSource.WorkloadIdentity,
                CredentialSource.ManagedIdentity,
                CredentialSource.VisualStudio,
            ],
            resolution.Skipped);
    }

    [Fact]
    public void Resolve_RecordsTheIdentitiesItCouldHaveBecomeInstead()
    {
        var resolution = CredentialChain.Resolve(Laptop);

        Assert.Equal(
            [CredentialSource.AzureCli, CredentialSource.AzureDeveloperCli],
            resolution.Shadowed);
    }

    [Fact]
    public void Resolve_KeepsSkippedAndShadowedApart()
    {
        // Sources ahead of the winner are why the chain kept going; sources
        // behind it are the risk. Merging them loses the distinction the whole
        // audit depends on.
        var resolution = CredentialChain.Resolve(Laptop);

        Assert.DoesNotContain(CredentialSource.AzureCli, resolution.Skipped);
        Assert.DoesNotContain(CredentialSource.ManagedIdentity, resolution.Shadowed);
    }

    [Fact]
    public void Resolve_ReportsNoneRatherThanGuessingWhenNothingIsConfigured()
    {
        var resolution = CredentialChain.Resolve(Nothing);

        Assert.Equal(CredentialSource.None, resolution.Selected);
        Assert.Equal(CredentialChain.Order.Count, resolution.Skipped.Count);
        Assert.Empty(resolution.Shadowed);
    }

    [Fact]
    public void AuthenticateAgainst_UsesTheWellKnownKeyForAnEmulator()
    {
        var decision = CredentialChain.AuthenticateAgainst(
            new ServiceEndpoint("127.0.0.1:10000", IsEmulator: true, AllowsSharedKey: true),
            CredentialChain.Resolve(Laptop));

        Assert.Equal(AuthenticationMethod.EmulatorWellKnownKey, decision.Method);
    }

    [Fact]
    public void AuthenticateAgainst_DoesNotSendATokenToAnEmulatorThatIssuesNone()
    {
        // A signed-in developer is not a reason to try Entra against Azurite:
        // there is no token endpoint on the other side.
        var decision = CredentialChain.AuthenticateAgainst(
            new ServiceEndpoint("127.0.0.1:8081", IsEmulator: true, AllowsSharedKey: false),
            CredentialChain.Resolve(BuildAgent));

        Assert.Equal(AuthenticationMethod.EmulatorWellKnownKey, decision.Method);
    }

    [Fact]
    public void AuthenticateAgainst_UsesATokenForALiveAccount()
    {
        var decision = CredentialChain.AuthenticateAgainst(
            new ServiceEndpoint("stexpedition001.blob.core.windows.net", IsEmulator: false, AllowsSharedKey: false),
            CredentialChain.Resolve(AppServiceWithManagedIdentity));

        Assert.Equal(AuthenticationMethod.EntraToken, decision.Method);
    }

    [Fact]
    public void AuthenticateAgainst_PrefersATokenEvenWhenTheAccountStillPermitsKeys()
    {
        var decision = CredentialChain.AuthenticateAgainst(
            new ServiceEndpoint("stexpedition001.blob.core.windows.net", IsEmulator: false, AllowsSharedKey: true),
            CredentialChain.Resolve(Laptop));

        Assert.Equal(AuthenticationMethod.EntraToken, decision.Method);
    }

    [Fact]
    public void AuthenticateAgainst_FallsBackToTheKeyOnlyWhenNothingCanIssueAToken()
    {
        var decision = CredentialChain.AuthenticateAgainst(
            new ServiceEndpoint("stexpedition001.blob.core.windows.net", IsEmulator: false, AllowsSharedKey: true),
            CredentialChain.Resolve(Nothing));

        Assert.Equal(AuthenticationMethod.SharedKey, decision.Method);
    }

    [Fact]
    public void AuthenticateAgainst_RefusesWhenTheAccountTakesNeither()
    {
        // allowSharedKeyAccess=false and an empty chain is not a fallback
        // situation; it is a configuration that cannot work.
        Assert.Throws<InvalidOperationException>(() => CredentialChain.AuthenticateAgainst(
            new ServiceEndpoint("stexpedition001.blob.core.windows.net", IsEmulator: false, AllowsSharedKey: false),
            CredentialChain.Resolve(Nothing)));
    }

    [Fact]
    public void Audit_SaysNothingAboutAnUnambiguousManagedIdentity()
    {
        Assert.Null(CredentialChain.Audit(
            CredentialChain.Resolve(AppServiceWithManagedIdentity),
            isProduction: true));
    }

    [Fact]
    public void Audit_FlagsAProductionHostRunningAsASignedInHuman()
    {
        var finding = CredentialChain.Audit(CredentialChain.Resolve(Laptop), isProduction: true);

        Assert.NotNull(finding);
        Assert.Contains("ManagedIdentityCredential", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_FlagsAProductionChainWithASecondUsableSource()
    {
        var finding = CredentialChain.Audit(
            CredentialChain.Resolve(new EnvironmentSnapshot(HasImdsEndpoint: true, HasAzureCliLogin: true)),
            isProduction: true);

        Assert.NotNull(finding);
    }

    [Fact]
    public void Audit_TreatsShadowingOnALaptopAsWorthMentioningAndNoMore()
    {
        var finding = CredentialChain.Audit(CredentialChain.Resolve(Laptop), isProduction: false);

        Assert.NotNull(finding);
        Assert.DoesNotContain("production", finding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_AlwaysFlagsAnEmptyChain()
    {
        Assert.NotNull(CredentialChain.Audit(CredentialChain.Resolve(Nothing), isProduction: false));
        Assert.NotNull(CredentialChain.Audit(CredentialChain.Resolve(Nothing), isProduction: true));
    }

    [Fact]
    public void IsAvailable_ReadsExactlyOneSignalPerSource()
    {
        Assert.True(CredentialChain.IsAvailable(CredentialSource.AzureCli, Laptop));
        Assert.False(CredentialChain.IsAvailable(CredentialSource.AzurePowerShell, Laptop));
    }
}
