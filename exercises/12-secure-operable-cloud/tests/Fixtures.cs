using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// The one expedition subscription every check in this evaluator argues about.
/// </summary>
/// <remarks>
/// The paths are real Azure resource identifiers with the ids replaced, because
/// scope containment is a string relationship over exactly this shape and a
/// simplified path would make the checks agree with a wrong implementation.
/// </remarks>
internal static class Fixtures
{
    internal const string SubscriptionId = "11111111-2222-3333-4444-555555555555";

    internal const string OtherSubscriptionId = "99999999-8888-7777-6666-555555555555";

    internal const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    internal const string Owner = "field-team";

    internal static ResourceScope Subscription { get; } =
        ResourceScope.Parse($"/subscriptions/{SubscriptionId}");

    internal static ResourceScope ResourceGroup { get; } =
        ResourceScope.Parse($"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint");

    internal static ResourceScope StorageAccount { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.Storage/storageAccounts/stexpedition001");

    internal static ResourceScope ReportsContainer { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.Storage/storageAccounts/stexpedition001"
            + "/blobServices/default/containers/reports");

    internal static ResourceScope CheckpointsContainer { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.Storage/storageAccounts/stexpedition001"
            + "/blobServices/default/containers/checkpoints");

    internal static ResourceScope WorkQueue { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.Storage/storageAccounts/stexpedition001"
            + "/queueServices/default/queues/artifact-work");

    internal static ResourceScope EventHubsNamespace { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.EventHub/namespaces/ehns-expedition");

    internal static ResourceScope TelemetryHub { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.EventHub/namespaces/ehns-expedition/eventhubs/telemetry");

    internal static ResourceScope CosmosAccount { get; } =
        ResourceScope.Parse(
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-expedition-checkpoint"
            + "/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-expedition");

    internal static ResourceScope OtherResourceGroup { get; } =
        ResourceScope.Parse($"/subscriptions/{SubscriptionId}/resourceGroups/rg-production-telemetry");

    internal static ResourceScope ForeignSubscriptionGroup { get; } =
        ResourceScope.Parse($"/subscriptions/{OtherSubscriptionId}/resourceGroups/rg-expedition-checkpoint");

    /// <summary>The tags a compliant run writes.</summary>
    /// <param name="expiresOn">When the resources stop being justified.</param>
    /// <returns>The tag set.</returns>
    internal static ResourceTags Tags(DateOnly? expiresOn = null) => new(
        Owner,
        ResourceTags.CourseManagedBy,
        "module-12-checkpoint",
        expiresOn ?? new DateOnly(2026, 12, 31));

    /// <summary>A signed-in session that can see exactly one subscription.</summary>
    /// <param name="roles">The roles the identity already holds.</param>
    /// <returns>The session snapshot.</returns>
    internal static SessionSnapshot SignedIn(params RoleAssignment[] roles) => new(
        SignedIn: true,
        TenantId: TenantId,
        PrincipalId: "principal-0001",
        Subscriptions: [new SubscriptionRecord(SubscriptionId, "Expedition Sandbox", TenantId)],
        AssignedRoles: roles);

    /// <summary>The requirements the module-12 lab declares.</summary>
    /// <param name="selector">How the subscription is named.</param>
    /// <param name="region">Where resources would be created.</param>
    /// <param name="requiredRoles">Roles the identity must already hold.</param>
    /// <returns>The requirements.</returns>
    internal static PreflightRequirements Requirements(
        string? selector = null,
        string region = "westeurope",
        IReadOnlyList<string>? requiredRoles = null) => new(
        selector ?? SubscriptionId,
        TenantId,
        region,
        ["westeurope", "northeurope"],
        requiredRoles ?? []);
}
