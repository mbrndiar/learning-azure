using System.Globalization;
using System.Text;

namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>
/// Composes resource names that the service will accept and that a teardown can
/// find again, and checks the tags that make a resource safe to delete.
/// </summary>
/// <remarks>
/// Naming looks like bureaucracy until the first live run. A storage account
/// name is a DNS label in a global namespace, so a name that reads well and is
/// already taken fails at creation; a name that is unique but says nothing
/// about who made it or when it expires survives forever because nobody dares
/// delete it.
/// </remarks>
public static class ResourceNaming
{
    /// <summary>The naming rule each service publishes.</summary>
    /// <param name="MinimumLength">The shortest name the service accepts.</param>
    /// <param name="MaximumLength">The longest name the service accepts.</param>
    /// <param name="AllowsHyphens">Whether a hyphen is a legal character.</param>
    /// <param name="AllowsUpperCase">Whether upper-case letters are legal.</param>
    /// <param name="MustStartWithLetter">Whether the first character must be a letter.</param>
    /// <param name="GloballyUnique">Whether the name has to be unique across all of Azure.</param>
    public sealed record NamingRule(
        int MinimumLength,
        int MaximumLength,
        bool AllowsHyphens,
        bool AllowsUpperCase,
        bool MustStartWithLetter,
        bool GloballyUnique);

    /// <summary>The published rule for one service.</summary>
    /// <param name="service">The service the name is for.</param>
    /// <returns>Its naming rule.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The service is not one this course uses.</exception>
    public static NamingRule RuleFor(AzureService service) => service switch
    {
        // One storage account backs blobs, queues, and tables, so all three
        // share the strictest rule in the course: 3-24 characters, lower-case
        // letters and digits only, globally unique because it becomes
        // <name>.blob.core.windows.net.
        AzureService.BlobStorage or AzureService.QueueStorage or AzureService.TableStorage =>
            new NamingRule(3, 24, AllowsHyphens: false, AllowsUpperCase: false, MustStartWithLetter: false, GloballyUnique: true),

        AzureService.EventHubs =>
            new NamingRule(6, 50, AllowsHyphens: true, AllowsUpperCase: true, MustStartWithLetter: true, GloballyUnique: true),

        AzureService.CosmosNoSql =>
            new NamingRule(3, 44, AllowsHyphens: true, AllowsUpperCase: false, MustStartWithLetter: false, GloballyUnique: true),

        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unknown service."),
    };

    /// <summary>Builds a name for one service out of a prefix and a run identifier.</summary>
    /// <param name="service">The service the name is for.</param>
    /// <param name="prefix">
    /// What a human will recognise, such as <c>expedition</c>. It may contain
    /// anything; this method is responsible for what survives.
    /// </param>
    /// <param name="runId">
    /// The value that keeps two learners in one subscription apart. It is the
    /// part that must never be truncated away.
    /// </param>
    /// <returns>The composed name and whether the service will accept it.</returns>
    /// <exception cref="ArgumentException">The prefix or run id is empty.</exception>
    public static ResourceName Compose(AzureService service, string prefix, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        // GAP 10: sanitise, then join, then truncate from the END OF THE PREFIX.
        //
        // Three rules and one trap. Drop every character the service does not
        // allow, and lower-case the rest when it insists. Keep a separator only
        // where hyphens are legal, so a storage account becomes one run of
        // letters and digits. Then, if the result is too long, cut the
        // *prefix*, never the run id: truncating the tail is what turns two
        // learners' unique names into the same name, and the failure arrives as
        // a 409 on somebody else's resource group. Pad a too-short name rather
        // than emitting one the service will reject.
        // See lessons/12-secure-operable-cloud/README.md#a-name-is-a-teardown-handle
        throw new NotImplementedException(
            "GAP 10: implement ResourceNaming.Compose. "
            + "See lessons/12-secure-operable-cloud/README.md#a-name-is-a-teardown-handle.");
    }

    /// <summary>Checks a name against a rule.</summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="rule">The rule it must satisfy.</param>
    /// <returns>The violated rule, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    public static string? Validate(string name, NamingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrEmpty(name))
        {
            return "A name is required.";
        }

        if (name.Length < rule.MinimumLength || name.Length > rule.MaximumLength)
        {
            return FormattableString.Invariant(
                $"'{name}' is {name.Length} characters; the rule is {rule.MinimumLength}-{rule.MaximumLength}.");
        }

        if (!rule.AllowsUpperCase && name.Any(char.IsUpper))
        {
            return FormattableString.Invariant($"'{name}' contains upper-case letters, which this service rejects.");
        }

        if (!rule.AllowsHyphens && name.Contains('-', StringComparison.Ordinal))
        {
            return FormattableString.Invariant($"'{name}' contains a hyphen, which this service rejects.");
        }

        if (rule.MustStartWithLetter && !char.IsLetter(name[0]))
        {
            return FormattableString.Invariant($"'{name}' must start with a letter.");
        }

        if (name.EndsWith('-') || name.StartsWith('-'))
        {
            return FormattableString.Invariant($"'{name}' must start and end with a letter or a digit.");
        }

        return null;
    }

    /// <summary>Checks that a resource carries the tags teardown depends on.</summary>
    /// <param name="tags">The tags the resource carries, if any.</param>
    /// <param name="today">The date to judge expiry against.</param>
    /// <returns>
    /// Every problem found, in a stable order. Empty means the resource is safe
    /// to create and, later, safe to find and delete.
    /// </returns>
    public static IReadOnlyList<string> ValidateTags(ResourceTags? tags, DateOnly today)
    {
        // GAP 11: the tags are a contract with your future self.
        //
        // Absent tags are the worst case, not an edge case: an untagged
        // resource group cannot be attributed and will not be deleted by
        // anybody who is not certain. owner and purpose answer "may I delete
        // this?"; managed-by proves the automation made it, which is what the
        // teardown checks before it acts; expires-on turns "probably still
        // needed" into a date. An expires-on already in the past is a finding
        // too -- it means the resource outlived its own declaration.
        // See lessons/12-secure-operable-cloud/README.md#a-name-is-a-teardown-handle
        throw new NotImplementedException(
            "GAP 11: implement ResourceNaming.ValidateTags. "
            + "See lessons/12-secure-operable-cloud/README.md#a-name-is-a-teardown-handle.");
    }

    /// <summary>Removes every character a rule forbids.</summary>
    /// <param name="value">The raw text.</param>
    /// <param name="rule">The rule to satisfy.</param>
    /// <returns>What is left, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    public static string Sanitize(string value, NamingRule rule)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(rule);

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(rule.AllowsUpperCase ? character : char.ToLowerInvariant(character));
            }
            else if (character == '-' && rule.AllowsHyphens && builder.Length > 0)
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }
}
