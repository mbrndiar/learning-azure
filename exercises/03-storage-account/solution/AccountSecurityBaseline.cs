namespace LearningAzure.Exercises.StorageAccount;

/// <summary>Judges an account's configuration against the course security baseline.</summary>
public static class AccountSecurityBaseline
{
    /// <summary>The lowest TLS version the baseline accepts.</summary>
    public const string RequiredMinimumTlsVersion = "TLS1_2";

    private static readonly string[] AcceptedTlsVersions = ["TLS1_2", "TLS1_3"];

    /// <summary>Lists every way a configuration falls short of the baseline.</summary>
    /// <param name="configuration">The account settings to judge.</param>
    /// <returns>One violation per failing setting; empty when the account is compliant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
    public static IReadOnlyList<BaselineViolation> Evaluate(AccountConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var violations = new List<BaselineViolation>();

        if (configuration.AllowSharedKeyAccess)
        {
            violations.Add(new BaselineViolation(
                "allowSharedKeyAccess",
                "Anyone holding either account key has full data-plane access to every container, "
                + "queue, and table, with no role assignment, no expiry, and no per-identity audit "
                + "trail."));
        }

        if (configuration.AllowBlobPublicAccess)
        {
            violations.Add(new BaselineViolation(
                "allowBlobPublicAccess",
                "Any container in this account can be switched to anonymous read by anyone holding "
                + "container-configuration rights, publishing expedition artifacts to the internet "
                + "with no further approval."));
        }

        if (!configuration.RequireHttpsOnly)
        {
            violations.Add(new BaselineViolation(
                "supportsHttpsTrafficOnly",
                "Requests may be sent over plain HTTP, so credentials and artifact contents are "
                + "readable by anything on the network path."));
        }

        if (!AcceptedTlsVersions.Contains(configuration.MinimumTlsVersion, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add(new BaselineViolation(
                "minimumTlsVersion",
                $"Clients may negotiate a TLS version below {RequiredMinimumTlsVersion}, which has "
                + "known downgrade and cipher weaknesses."));
        }

        if (!configuration.DefaultNetworkActionIsDeny)
        {
            violations.Add(new BaselineViolation(
                "networkAcls.defaultAction",
                "The account answers requests from every network on the internet, so a leaked "
                + "credential is usable from anywhere rather than only from approved networks."));
        }

        if (!configuration.InfrastructureEncryptionEnabled)
        {
            violations.Add(new BaselineViolation(
                "requireInfrastructureEncryption",
                "Data is encrypted once rather than twice, so a defect in a single encryption "
                + "layer exposes the stored artifacts."));
        }

        // Every finding is reported. An account with four problems that reports
        // one gets fixed once and re-audited four times.
        return violations;
    }

    /// <summary>Reports whether a configuration meets the baseline with no exceptions.</summary>
    /// <param name="configuration">The account settings to judge.</param>
    /// <returns><c>true</c> when <see cref="Evaluate"/> finds nothing.</returns>
    public static bool IsCompliant(AccountConfiguration configuration) =>
        Evaluate(configuration).Count == 0;
}
