using System.Globalization;
using System.Text.RegularExpressions;

namespace LearningAzure.Exercises.DataMap;

/// <summary>
/// Names and tags every resource an expedition deployment creates, so that one
/// command can remove all of it.
/// </summary>
/// <remarks>
/// Naming is not cosmetic here. Azure resource names have different, unforgiving
/// rule sets — a resource group tolerates hyphens and mixed case, a storage
/// account name must be 3 to 24 lowercase letters and digits and is globally
/// unique across all of Azure. Deriving both from the same expedition identity
/// keeps a deployment reproducible; putting everything in one resource group is
/// what makes teardown a single, complete operation instead of a hunt.
/// </remarks>
public static partial class ExpeditionNaming
{
    /// <summary>The shortest accepted expedition or environment segment.</summary>
    public const int MinSegmentLength = 2;

    /// <summary>The longest accepted expedition or environment segment.</summary>
    public const int MaxSegmentLength = 24;

    /// <summary>The Azure limit on a storage account name.</summary>
    public const int MaxStorageAccountNameLength = 24;

    /// <summary>Tag applied to everything the course creates, so stray resources are attributable.</summary>
    public const string ManagedByTagValue = "learning-azure";

    private const int MinDiscriminatorLength = 4;
    private const int MaxDiscriminatorLength = 8;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SegmentPattern { get; }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex AlphanumericPattern { get; }

    /// <summary>Builds the single resource group that owns every resource of one deployment.</summary>
    /// <param name="expedition">Expedition identity, lowercase kebab-case.</param>
    /// <param name="environment">Environment identity, lowercase kebab-case.</param>
    /// <returns><c>rg-expedition-{expedition}-{environment}</c>.</returns>
    /// <exception cref="ArgumentException">A segment is not lowercase kebab-case within the length limits.</exception>
    public static string ResourceGroup(string expedition, string environment)
    {
        ValidateSegment(expedition, nameof(expedition));
        ValidateSegment(environment, nameof(environment));
        return $"rg-expedition-{expedition}-{environment}";
    }

    /// <summary>Builds a storage account name that satisfies Azure's global naming rules.</summary>
    /// <param name="expedition">Expedition identity, lowercase kebab-case.</param>
    /// <param name="environment">Environment identity, lowercase kebab-case.</param>
    /// <param name="discriminator">4 to 8 lowercase letters or digits that make the name globally unique.</param>
    /// <returns>A name of 3 to 24 lowercase letters and digits that still contains the discriminator.</returns>
    /// <exception cref="ArgumentException">A segment or the discriminator breaks its rule.</exception>
    public static string StorageAccount(string expedition, string environment, string discriminator)
    {
        ValidateSegment(expedition, nameof(expedition));
        ValidateSegment(environment, nameof(environment));
        ValidateDiscriminator(discriminator);

        // The discriminator is placed immediately after the "st" prefix so that
        // truncation can only ever remove identity context, never the part that
        // makes the name globally unique.
        var candidate = string.Concat(
            "st",
            discriminator,
            expedition.Replace("-", string.Empty, StringComparison.Ordinal),
            environment.Replace("-", string.Empty, StringComparison.Ordinal));

        return candidate.Length <= MaxStorageAccountNameLength
            ? candidate
            : candidate[..MaxStorageAccountNameLength];
    }

    /// <summary>Builds the tags every expedition resource must carry.</summary>
    /// <param name="expedition">Expedition identity, lowercase kebab-case.</param>
    /// <param name="environment">Environment identity, lowercase kebab-case.</param>
    /// <param name="today">The day the deployment is created, injected so the result is testable.</param>
    /// <param name="lifetime">How long the deployment may live before it must be deleted.</param>
    /// <returns>Tags <c>expedition</c>, <c>environment</c>, <c>expires-on</c>, and <c>managed-by</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    /// <exception cref="ArgumentException">A segment is not lowercase kebab-case within the length limits.</exception>
    public static IReadOnlyDictionary<string, string> RequiredTags(
        string expedition,
        string environment,
        DateOnly today,
        TimeSpan lifetime)
    {
        ValidateSegment(expedition, nameof(expedition));
        ValidateSegment(environment, nameof(environment));
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                lifetime,
                "A deployment that has already expired when it is created cannot be reasoned about; "
                + "lifetime must be positive.");
        }

        var expiresOn = today.AddDays((int)Math.Ceiling(lifetime.TotalDays));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["expedition"] = expedition,
            ["environment"] = environment,
            ["expires-on"] = expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["managed-by"] = ManagedByTagValue,
        };
    }

    /// <summary>Returns the single command that removes an entire deployment.</summary>
    /// <param name="resourceGroup">The resource group produced by <see cref="ResourceGroup"/>.</param>
    /// <returns>An Azure CLI command that deletes the group without prompting.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceGroup"/> is null, empty, or whitespace.</exception>
    public static string TeardownCommand(string resourceGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        return $"az group delete --name {resourceGroup} --yes --no-wait";
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length is < MinSegmentLength or > MaxSegmentLength)
        {
            throw new ArgumentException(
                $"'{value}' is {value.Length} characters; {parameterName} must be between "
                + $"{MinSegmentLength} and {MaxSegmentLength}.",
                parameterName);
        }

        if (!SegmentPattern.IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not lowercase kebab-case; {parameterName} must match "
                + "^[a-z0-9]+(-[a-z0-9]+)*$ so it can be reused in resource names that forbid "
                + "uppercase and underscores.",
                parameterName);
        }
    }

    private static void ValidateDiscriminator(string discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        if (discriminator.Length is < MinDiscriminatorLength or > MaxDiscriminatorLength)
        {
            throw new ArgumentException(
                $"'{discriminator}' is {discriminator.Length} characters; the discriminator must be "
                + $"between {MinDiscriminatorLength} and {MaxDiscriminatorLength} so it survives "
                + "truncation while leaving room for identity.",
                nameof(discriminator));
        }

        if (!AlphanumericPattern.IsMatch(discriminator))
        {
            throw new ArgumentException(
                $"'{discriminator}' must match ^[a-z0-9]+$; a storage account name accepts only "
                + "lowercase letters and digits.",
                nameof(discriminator));
        }
    }
}
