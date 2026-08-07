namespace LearningAzure.Exercises.StorageAccount;

/// <summary>Judges an account's configuration against the course security baseline.</summary>
public static class AccountSecurityBaseline
{
    /// <summary>The lowest TLS version the baseline accepts.</summary>
    public const string RequiredMinimumTlsVersion = "TLS1_2";

    /// <summary>Lists every way a configuration falls short of the baseline.</summary>
    /// <param name="configuration">The account settings to judge.</param>
    /// <returns>One violation per failing setting; empty when the account is compliant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
    public static IReadOnlyList<BaselineViolation> Evaluate(AccountConfiguration configuration) =>
        // GAP 7 — Report EVERY violation, not the first one.
        //
        // Emit one BaselineViolation per failing setting, using exactly these
        // Setting values so the report is greppable:
        //
        //   "allowSharedKeyAccess"            when shared key access is enabled
        //   "allowBlobPublicAccess"           when anonymous container access is allowed
        //   "supportsHttpsTrafficOnly"        when plain HTTP is accepted
        //   "minimumTlsVersion"               when below RequiredMinimumTlsVersion
        //   "networkAcls.defaultAction"       when the firewall defaults to Allow
        //   "requireInfrastructureEncryption" when the second encryption layer is off
        //
        // Give each a Consequence that says what an attacker or an accident can
        // DO, not what the setting is named. "allowBlobPublicAccess is true" is
        // not a finding; "any container in this account can be switched to
        // anonymous read by anyone with container-configuration rights" is.
        //
        // Returning after the first violation is the mistake this gap exists to
        // prevent: an account with four problems that reports one gets fixed
        // once and re-audited four times.
        throw new NotImplementedException(
            "GAP 7: implement AccountSecurityBaseline.Evaluate. See "
            + "lessons/03-storage-account/README.md#the-account-is-the-auth-boundary.");

    /// <summary>Reports whether a configuration meets the baseline with no exceptions.</summary>
    /// <param name="configuration">The account settings to judge.</param>
    /// <returns><c>true</c> when <see cref="Evaluate"/> finds nothing.</returns>
    public static bool IsCompliant(AccountConfiguration configuration) =>
        Evaluate(configuration).Count == 0;
}
