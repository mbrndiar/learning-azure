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
public static class ExpeditionNaming
{
    /// <summary>The shortest accepted expedition or environment segment.</summary>
    public const int MinSegmentLength = 2;

    /// <summary>The longest accepted expedition or environment segment.</summary>
    public const int MaxSegmentLength = 24;

    /// <summary>The Azure limit on a storage account name.</summary>
    public const int MaxStorageAccountNameLength = 24;

    /// <summary>Tag applied to everything the course creates, so stray resources are attributable.</summary>
    public const string ManagedByTagValue = "learning-azure";

    /// <summary>Builds the single resource group that owns every resource of one deployment.</summary>
    /// <param name="expedition">Expedition identity, lowercase kebab-case.</param>
    /// <param name="environment">Environment identity, lowercase kebab-case.</param>
    /// <returns><c>rg-expedition-{expedition}-{environment}</c>.</returns>
    /// <exception cref="ArgumentException">A segment is not lowercase kebab-case within the length limits.</exception>
    public static string ResourceGroup(string expedition, string environment) =>
        // GAP 3 — Validate both segments, then build rg-expedition-{expedition}-{environment}.
        //
        // A segment is valid when it matches ^[a-z0-9]+(-[a-z0-9]+)*$ and its
        // length is between MinSegmentLength and MaxSegmentLength. Reject anything
        // else with an ArgumentException whose message names the offending segment
        // and the rule it broke — a validation message that does not say what to
        // fix is a second defect.
        throw new NotImplementedException(
            "GAP 3: implement ExpeditionNaming.ResourceGroup. See "
            + "lessons/01-azure-data-map/README.md#name-it-so-you-can-delete-it.");

    /// <summary>Builds a storage account name that satisfies Azure's global naming rules.</summary>
    /// <param name="expedition">Expedition identity, lowercase kebab-case.</param>
    /// <param name="environment">Environment identity, lowercase kebab-case.</param>
    /// <param name="discriminator">4 to 8 lowercase letters or digits that make the name globally unique.</param>
    /// <returns>A name of 3 to 24 lowercase letters and digits that still contains the discriminator.</returns>
    /// <exception cref="ArgumentException">A segment or the discriminator breaks its rule.</exception>
    public static string StorageAccount(string expedition, string environment, string discriminator) =>
        // GAP 4 — Build st{discriminator}{expedition}{environment}, stripped of
        // hyphens and truncated to MaxStorageAccountNameLength.
        //
        // The discriminator comes first, immediately after "st", so truncation can
        // never remove the part that makes the name unique. Validate the
        // discriminator as 4 to 8 characters matching ^[a-z0-9]+$, and validate the
        // two identity segments with the same rule ResourceGroup uses.
        throw new NotImplementedException(
            "GAP 4: implement ExpeditionNaming.StorageAccount. See "
            + "lessons/01-azure-data-map/README.md#name-it-so-you-can-delete-it.");

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
        TimeSpan lifetime) =>
        // GAP 5 — Return the four required tags.
        //
        // expires-on is today + lifetime, formatted as yyyy-MM-dd with the
        // invariant culture, and managed-by is always ManagedByTagValue. A
        // non-positive lifetime is an ArgumentOutOfRangeException: a resource that
        // has already expired when it is created cannot be reasoned about.
        throw new NotImplementedException(
            "GAP 5: implement ExpeditionNaming.RequiredTags. See "
            + "lessons/01-azure-data-map/README.md#name-it-so-you-can-delete-it.");

    /// <summary>Returns the single command that removes an entire deployment.</summary>
    /// <param name="resourceGroup">The resource group produced by <see cref="ResourceGroup"/>.</param>
    /// <returns>An Azure CLI command that deletes the group without prompting.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceGroup"/> is null, empty, or whitespace.</exception>
    public static string TeardownCommand(string resourceGroup) =>
        // GAP 6 — Return: az group delete --name {resourceGroup} --yes --no-wait
        //
        // This is the payoff for the naming discipline above. Because every
        // resource of a deployment lives in one group, teardown is one command
        // with no per-service cleanup and nothing left billing.
        throw new NotImplementedException(
            "GAP 6: implement ExpeditionNaming.TeardownCommand. See "
            + "lessons/01-azure-data-map/README.md#name-it-so-you-can-delete-it.");
}
