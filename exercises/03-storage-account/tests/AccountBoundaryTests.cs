namespace LearningAzure.Exercises.StorageAccount.Tests;

/// <summary>Verifies the security baseline and the emulator parity table.</summary>
public sealed class AccountBoundaryTests
{
    private static AccountConfiguration Compliant() => new(
        AllowSharedKeyAccess: false,
        AllowBlobPublicAccess: false,
        RequireHttpsOnly: true,
        MinimumTlsVersion: "TLS1_2",
        DefaultNetworkActionIsDeny: true,
        InfrastructureEncryptionEnabled: true);

    [Fact]
    public void ACompliantAccountHasNoFindings()
    {
        Assert.Empty(AccountSecurityBaseline.Evaluate(Compliant()));
        Assert.True(AccountSecurityBaseline.IsCompliant(Compliant()));
    }

    [Fact]
    public void SharedKeyAccessIsAFinding()
    {
        var findings = AccountSecurityBaseline.Evaluate(Compliant() with { AllowSharedKeyAccess = true });

        Assert.Contains(findings, finding => finding.Setting == "allowSharedKeyAccess");
    }

    [Fact]
    public void AnonymousBlobAccessIsAFinding()
    {
        var findings = AccountSecurityBaseline.Evaluate(Compliant() with { AllowBlobPublicAccess = true });

        Assert.Contains(findings, finding => finding.Setting == "allowBlobPublicAccess");
    }

    [Fact]
    public void PlainHttpIsAFinding()
    {
        var findings = AccountSecurityBaseline.Evaluate(Compliant() with { RequireHttpsOnly = false });

        Assert.Contains(findings, finding => finding.Setting == "supportsHttpsTrafficOnly");
    }

    [Theory]
    [InlineData("TLS1_0")]
    [InlineData("TLS1_1")]
    public void ATlsVersionBelowTheBaselineIsAFinding(string version)
    {
        var findings = AccountSecurityBaseline.Evaluate(Compliant() with { MinimumTlsVersion = version });

        Assert.Contains(findings, finding => finding.Setting == "minimumTlsVersion");
    }

    [Fact]
    public void ANewerTlsVersionIsNotAFinding()
    {
        var findings = AccountSecurityBaseline.Evaluate(Compliant() with { MinimumTlsVersion = "TLS1_3" });

        Assert.DoesNotContain(findings, finding => finding.Setting == "minimumTlsVersion");
    }

    [Fact]
    public void AnOpenFirewallIsAFinding()
    {
        var findings = AccountSecurityBaseline.Evaluate(Compliant() with { DefaultNetworkActionIsDeny = false });

        Assert.Contains(findings, finding => finding.Setting == "networkAcls.defaultAction");
    }

    [Fact]
    public void MissingInfrastructureEncryptionIsAFinding()
    {
        var findings = AccountSecurityBaseline.Evaluate(
            Compliant() with { InfrastructureEncryptionEnabled = false });

        Assert.Contains(findings, finding => finding.Setting == "requireInfrastructureEncryption");
    }

    [Fact]
    public void EveryViolationIsReportedNotJustTheFirst()
    {
        var worst = new AccountConfiguration(
            AllowSharedKeyAccess: true,
            AllowBlobPublicAccess: true,
            RequireHttpsOnly: false,
            MinimumTlsVersion: "TLS1_0",
            DefaultNetworkActionIsDeny: false,
            InfrastructureEncryptionEnabled: false);

        // An account with six problems that reports one gets fixed once and
        // re-audited six times.
        Assert.Equal(6, AccountSecurityBaseline.Evaluate(worst).Count);
    }

    [Fact]
    public void EachFindingExplainsTheConsequenceNotJustTheSettingName()
    {
        var worst = new AccountConfiguration(true, true, false, "TLS1_0", false, false);

        foreach (var finding in AccountSecurityBaseline.Evaluate(worst))
        {
            Assert.True(
                finding.Consequence.Length > 40,
                $"'{finding.Setting}' has a consequence of {finding.Consequence.Length} characters, "
                + "which cannot describe what an attacker or an accident can do.");
            Assert.DoesNotContain(finding.Setting, finding.Consequence, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EvaluateRejectsANullConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => AccountSecurityBaseline.Evaluate(null!));
    }

    [Theory]
    [InlineData("blob-crud")]
    [InlineData("queue-crud")]
    [InlineData("table-crud")]
    [InlineData("shared-key-auth")]
    public void AzuriteCanProveTheDataPlaneBasics(string capability)
    {
        Assert.True(EmulatorParity.IsVerifiableLocally(capability), capability);
    }

    [Theory]
    [InlineData("entra-rbac")]
    [InlineData("redundancy")]
    [InlineData("access-tiers")]
    [InlineData("lifecycle-rules")]
    [InlineData("network-rules")]
    [InlineData("throttling")]
    [InlineData("file-shares")]
    public void AzuriteCannotProveTheControlPlane(string capability)
    {
        Assert.False(EmulatorParity.IsVerifiableLocally(capability), capability);
    }

    [Fact]
    public void AnUnknownCapabilityIsAnErrorNotAFalse()
    {
        // A silent false lets a typo downgrade a capability into "needs the live
        // checkpoint", and nobody ever notices.
        Assert.Throws<ArgumentException>(() => EmulatorParity.IsVerifiableLocally("redundancey"));
    }

    [Fact]
    public void TheLiveCheckpointListMatchesTheParityTable()
    {
        var required = EmulatorParity.RequiresLiveCheckpoint();

        Assert.All(required, capability => Assert.False(EmulatorParity.IsVerifiableLocally(capability)));
    }

    [Fact]
    public void TheLiveCheckpointListNamesEveryUnverifiableCapability()
    {
        string[] expected =
        [
            "entra-rbac",
            "redundancy",
            "access-tiers",
            "lifecycle-rules",
            "network-rules",
            "throttling",
            "file-shares",
        ];

        Assert.Equal(expected, EmulatorParity.RequiresLiveCheckpoint());
    }

    [Fact]
    public void TheLiveCheckpointListExcludesEverythingAzuriteCanProve()
    {
        Assert.DoesNotContain("blob-crud", EmulatorParity.RequiresLiveCheckpoint());
    }
}
