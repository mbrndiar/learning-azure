using System.Globalization;

namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>The services the expedition actually talks to.</summary>
public enum AzureService
{
    /// <summary>Blob Storage: reports, artifacts, and the checkpoint store.</summary>
    BlobStorage,

    /// <summary>Queue Storage: work orders handed to exactly one processor.</summary>
    QueueStorage,

    /// <summary>Table Storage: station state, addressed by partition and row key.</summary>
    TableStorage,

    /// <summary>Event Hubs: the telemetry stream.</summary>
    EventHubs,

    /// <summary>Azure Cosmos DB for NoSQL: the queryable journal.</summary>
    CosmosNoSql,
}

/// <summary>What a caller intends to do, stated before any role is chosen.</summary>
/// <remarks>
/// Intent is deliberately not "the role I want". A role is the answer; the
/// intent is the question, and writing the question down first is what stops a
/// deployment from acquiring Contributor because somebody was in a hurry.
/// </remarks>
public enum AccessIntent
{
    /// <summary>Read data that is already there.</summary>
    Read,

    /// <summary>Create, replace, or delete data.</summary>
    Write,

    /// <summary>Put messages or events on a queue or a stream.</summary>
    SendMessages,

    /// <summary>Take messages off a queue or read events off a stream.</summary>
    ProcessMessages,

    /// <summary>Change the entities themselves, not just the data inside them.</summary>
    Administer,
}

/// <summary>Which role system a role name belongs to.</summary>
/// <remarks>
/// This distinction is the whole reason the type exists. Storage and Event Hubs
/// data roles are ordinary Azure RBAC and are assigned with
/// <c>az role assignment create</c>. Cosmos DB for NoSQL data roles are a
/// separate, account-scoped system with its own definitions, its own
/// assignments, and its own commands
/// (<c>az cosmosdb sql role assignment create</c>). Assigning a Cosmos data
/// role with the Azure RBAC command does not fail loudly — it creates nothing
/// that grants data access.
/// </remarks>
public enum RoleSystem
{
    /// <summary>Azure RBAC: <c>Microsoft.Authorization/roleAssignments</c>.</summary>
    AzureRbac,

    /// <summary>The Cosmos DB account's own data-plane role assignments.</summary>
    CosmosDataPlane,
}

/// <summary>The role an intent needs, and the system it must be assigned in.</summary>
/// <param name="RoleName">The built-in role's display name.</param>
/// <param name="System">Which role system defines it.</param>
public sealed record RoleRequirement(string RoleName, RoleSystem System);

/// <summary>How deep a scope reaches.</summary>
/// <remarks>
/// Ordered from broadest to narrowest, so the values compare the way privilege
/// does: a larger value is a smaller blast radius.
/// </remarks>
public enum ScopeLevel
{
    /// <summary>Every resource group in the subscription.</summary>
    Subscription = 1,

    /// <summary>Every resource in one resource group.</summary>
    ResourceGroup = 2,

    /// <summary>One account or namespace.</summary>
    Resource = 3,

    /// <summary>One container, queue, table, event hub, or consumer group.</summary>
    SubResource = 4,
}

/// <summary>An Azure resource path, and the scope level it represents.</summary>
/// <remarks>
/// Built through <see cref="Parse(string)"/> so a scope cannot be constructed
/// at a level its path does not actually support. Event Hubs documents the
/// assignable scopes explicitly — consumer group, event hub, namespace,
/// resource group, subscription — and Storage works the same way down to a
/// single container.
/// </remarks>
public sealed record ResourceScope
{
    private ResourceScope(string path, ScopeLevel level, IReadOnlyList<string> segments)
    {
        Path = path;
        Level = level;
        Segments = segments;
    }

    /// <summary>Gets the full Azure resource path, without a trailing slash.</summary>
    public string Path { get; }

    /// <summary>Gets how deep the path reaches.</summary>
    public ScopeLevel Level { get; }

    /// <summary>Gets the path split on '/', with the leading empty segment removed.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Reads a scope from an Azure resource path.</summary>
    /// <param name="path">
    /// A path such as <c>/subscriptions/{id}/resourceGroups/{rg}</c> or
    /// <c>/subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{account}</c>.
    /// </param>
    /// <returns>The parsed scope.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, is not rooted at a subscription, or stops in the
    /// middle of a resource identifier. A path that cannot be read is refused
    /// rather than rounded to something plausible: guessing here would widen a
    /// role assignment silently.
    /// </exception>
    public static ResourceScope Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.TrimEnd('/');
        if (!trimmed.StartsWith("/subscriptions/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"'{path}' is not rooted at a subscription."),
                nameof(path));
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"'{path}' contains an empty segment."),
                nameof(path));
        }

        var level = LevelFor(segments)
            ?? throw new ArgumentException(
                FormattableString.Invariant($"'{path}' stops in the middle of a resource identifier."),
                nameof(path));

        return new ResourceScope(trimmed, level, segments);
    }

    /// <summary>
    /// The scope level a path represents, or <see langword="null"/> when the
    /// path is not somewhere a role can be assigned.
    /// </summary>
    /// <param name="segments">The '/'-separated segments, without the leading empty one.</param>
    /// <returns>The level, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Scopes come in pairs of segments, but not every pair is assignable. The
    /// data-service wrappers -- <c>blobServices/default</c>,
    /// <c>queueServices/default</c>, and their siblings -- exist in the
    /// resource path so that a container has somewhere to live; they are not
    /// scopes, and a role assignment aimed at one is rejected.
    /// </remarks>
    public static ScopeLevel? LevelFor(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count >= 2
            && segments[segments.Count - 2].EndsWith("Services", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return segments.Count switch
        {
            2 => ScopeLevel.Subscription,
            4 => ScopeLevel.ResourceGroup,
            8 => ScopeLevel.Resource,
            >= 10 when segments.Count % 2 == 0 => ScopeLevel.SubResource,
            _ => null,
        };
    }

    /// <summary>Whether this scope contains <paramref name="other"/>, or is it.</summary>
    /// <param name="other">The scope a request targets.</param>
    /// <returns><see langword="true"/> when an assignment here reaches there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool Covers(ResourceScope other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            || other.Path.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the path, so a scope reads as itself in a failure message.</summary>
    /// <returns>The resource path.</returns>
    public override string ToString() => Path;
}

/// <summary>One role assignment that exists somewhere in the subscription.</summary>
/// <param name="RoleName">The role's display name.</param>
/// <param name="Scope">Where it was assigned.</param>
/// <param name="System">Which role system it lives in.</param>
public sealed record RoleAssignment(string RoleName, ResourceScope Scope, RoleSystem System);

/// <summary>One thing the application is about to try.</summary>
/// <param name="Service">The service it will call.</param>
/// <param name="Intent">What it means to do.</param>
/// <param name="Target">The exact entity it will touch.</param>
public sealed record AccessRequest(AzureService Service, AccessIntent Intent, ResourceScope Target);

/// <summary>What a refusal looks like on the wire for one service.</summary>
/// <param name="HttpStatus">
/// The HTTP status, or <see langword="null"/> for a service whose refusal is
/// not an HTTP response at all.
/// </param>
/// <param name="ErrorCode">
/// The stable error code the service publishes, or <see langword="null"/> when
/// it publishes none.
/// </param>
/// <param name="Surface">Where the learner will actually see it.</param>
public sealed record DenialSignature(int? HttpStatus, string? ErrorCode, string Surface);

/// <summary>Whether a request is authorized, and what to do when it is not.</summary>
/// <param name="Allowed">Whether an existing assignment covers the request.</param>
/// <param name="MissingRole">
/// The least-privilege role that would fix the refusal, or
/// <see langword="null"/> when nothing is missing.
/// </param>
/// <param name="Denial">How the refusal presents, or <see langword="null"/> when allowed.</param>
/// <param name="Reason">Why, in one sentence a colleague can act on.</param>
public sealed record AuthorizationOutcome(
    bool Allowed,
    RoleRequirement? MissingRole,
    DenialSignature? Denial,
    string Reason);

/// <summary>Where a credential's material comes from.</summary>
public enum CredentialSource
{
    /// <summary>No source is configured; nothing can authenticate.</summary>
    None,

    /// <summary>A service principal in <c>AZURE_CLIENT_ID</c> and friends.</summary>
    Environment,

    /// <summary>A federated token file projected into the pod.</summary>
    WorkloadIdentity,

    /// <summary>The IMDS endpoint of an Azure host.</summary>
    ManagedIdentity,

    /// <summary>The signed-in Visual Studio account.</summary>
    VisualStudio,

    /// <summary>The signed-in Visual Studio Code account.</summary>
    VisualStudioCode,

    /// <summary>Whoever ran <c>az login</c>.</summary>
    AzureCli,

    /// <summary>Whoever ran <c>Connect-AzAccount</c>.</summary>
    AzurePowerShell,

    /// <summary>Whoever ran <c>azd auth login</c>.</summary>
    AzureDeveloperCli,
}

/// <summary>What a machine actually has available, as the chain would find it.</summary>
/// <param name="HasEnvironmentServicePrincipal">
/// <c>AZURE_TENANT_ID</c>, <c>AZURE_CLIENT_ID</c>, and a secret or certificate
/// are all present.
/// </param>
/// <param name="HasFederatedTokenFile">
/// <c>AZURE_FEDERATED_TOKEN_FILE</c> points at a readable projected token.
/// </param>
/// <param name="HasImdsEndpoint">The host answers on the managed-identity endpoint.</param>
/// <param name="HasVisualStudioAccount">Visual Studio holds a signed-in account.</param>
/// <param name="HasVisualStudioCodeAccount">Visual Studio Code holds a signed-in account.</param>
/// <param name="HasAzureCliLogin">An <c>az login</c> session exists.</param>
/// <param name="HasAzurePowerShellLogin">A <c>Connect-AzAccount</c> session exists.</param>
/// <param name="HasAzureDeveloperCliLogin">An <c>azd auth login</c> session exists.</param>
public sealed record EnvironmentSnapshot(
    bool HasEnvironmentServicePrincipal = false,
    bool HasFederatedTokenFile = false,
    bool HasImdsEndpoint = false,
    bool HasVisualStudioAccount = false,
    bool HasVisualStudioCodeAccount = false,
    bool HasAzureCliLogin = false,
    bool HasAzurePowerShellLogin = false,
    bool HasAzureDeveloperCliLogin = false);

/// <summary>Which source wins, and what it stepped over on the way.</summary>
/// <param name="Selected">The source that will produce the token.</param>
/// <param name="Skipped">The sources ahead of it that had nothing to offer, in order.</param>
/// <param name="Shadowed">
/// The sources behind it that could also have authenticated. Every one of them
/// is an identity the application might silently switch to on another machine.
/// </param>
public sealed record CredentialResolution(
    CredentialSource Selected,
    IReadOnlyList<CredentialSource> Skipped,
    IReadOnlyList<CredentialSource> Shadowed);

/// <summary>What an endpoint will accept as proof of identity.</summary>
public enum AuthenticationMethod
{
    /// <summary>A Microsoft Entra ID token.</summary>
    EntraToken,

    /// <summary>The account's shared key.</summary>
    SharedKey,

    /// <summary>The emulator's well-known, public development credential.</summary>
    EmulatorWellKnownKey,
}

/// <summary>An endpoint the application is configured to talk to.</summary>
/// <param name="Host">The host name or address in the endpoint.</param>
/// <param name="IsEmulator">Whether the endpoint is a local emulator.</param>
/// <param name="AllowsSharedKey">
/// Whether the live account still permits Shared Key authorization. Storage
/// exposes this as <c>allowSharedKeyAccess</c>; Cosmos exposes the inverse as
/// <c>disableLocalAuth</c>.
/// </param>
public sealed record ServiceEndpoint(string Host, bool IsEmulator, bool AllowsSharedKey);

/// <summary>What the application must present to an endpoint, and why.</summary>
/// <param name="Method">The method that will actually be accepted.</param>
/// <param name="Reason">Why the other methods are not available here.</param>
public sealed record AuthenticationDecision(AuthenticationMethod Method, string Reason);

/// <summary>Why a preflight refused to continue.</summary>
public enum PreflightRefusal
{
    /// <summary>Nothing is wrong; the session may proceed.</summary>
    None,

    /// <summary>There is no signed-in session at all.</summary>
    NotSignedIn,

    /// <summary>The session is in a different Entra tenant than the one required.</summary>
    TenantMismatch,

    /// <summary>The active subscription is not the one that was asked for.</summary>
    SubscriptionMismatch,

    /// <summary>More than one subscription matches, so "the first one" is a coin toss.</summary>
    AmbiguousSubscription,

    /// <summary>No subscription matches the name or id supplied.</summary>
    NoSuchSubscription,

    /// <summary>The signed-in identity lacks a role the lab needs.</summary>
    MissingRole,

    /// <summary>The requested region is outside the allowed list.</summary>
    RegionNotAllowed,
}

/// <summary>One subscription the signed-in identity can see.</summary>
/// <param name="Id">The subscription GUID.</param>
/// <param name="Name">Its display name, which is not unique.</param>
/// <param name="TenantId">The Entra tenant it belongs to.</param>
public sealed record SubscriptionRecord(string Id, string Name, string TenantId);

/// <summary>What the tooling reports about the current session.</summary>
/// <param name="SignedIn">Whether a session exists at all.</param>
/// <param name="TenantId">The tenant of the signed-in identity, when there is one.</param>
/// <param name="PrincipalId">The object id of the signed-in identity.</param>
/// <param name="Subscriptions">Every subscription the identity can see.</param>
/// <param name="AssignedRoles">The roles the identity already holds, at any scope.</param>
public sealed record SessionSnapshot(
    bool SignedIn,
    string? TenantId,
    string? PrincipalId,
    IReadOnlyList<SubscriptionRecord> Subscriptions,
    IReadOnlyList<RoleAssignment> AssignedRoles);

/// <summary>What the lab needs to be true before it creates anything.</summary>
/// <param name="SubscriptionSelector">
/// The subscription id, or a display name. An id is exact; a name is a guess
/// that has to be proved unique.
/// </param>
/// <param name="TenantId">The tenant the run must happen in, when it is pinned.</param>
/// <param name="Region">The region resources will be created in.</param>
/// <param name="AllowedRegions">The regions this course is willing to bill in.</param>
/// <param name="RequiredRoles">Roles the identity must already hold to run the lab.</param>
public sealed record PreflightRequirements(
    string SubscriptionSelector,
    string? TenantId,
    string Region,
    IReadOnlyList<string> AllowedRegions,
    IReadOnlyList<string> RequiredRoles);

/// <summary>The verdict a preflight reaches, and everything the caller needs to print.</summary>
/// <param name="Refusal">Why it stopped, or <see cref="PreflightRefusal.None"/>.</param>
/// <param name="Subscription">The resolved subscription, when exactly one was resolved.</param>
/// <param name="Detail">A sentence naming what to fix.</param>
public sealed record PreflightVerdict(
    PreflightRefusal Refusal,
    SubscriptionRecord? Subscription,
    string Detail)
{
    /// <summary>Gets a value indicating whether the caller may create resources.</summary>
    public bool MayProceed => Refusal == PreflightRefusal.None;
}

/// <summary>One attempt to use a permission that was just granted.</summary>
/// <param name="Elapsed">How long after the assignment the probe ran.</param>
/// <param name="Authorized">Whether the data-plane call was allowed.</param>
public sealed record AccessProbe(TimeSpan Elapsed, bool Authorized);

/// <summary>How a bounded wait for a role assignment ended.</summary>
public enum PropagationOutcome
{
    /// <summary>A probe succeeded inside the budget.</summary>
    Ready,

    /// <summary>The budget ran out while the answer was still 403.</summary>
    TimedOut,
}

/// <summary>The result of waiting for a role assignment to take effect.</summary>
/// <param name="Outcome">Whether the permission arrived in time.</param>
/// <param name="Probes">How many probes were made.</param>
/// <param name="Waited">How long the wait lasted.</param>
/// <param name="Advice">What to do next — which never includes widening the role.</param>
public sealed record PropagationResult(
    PropagationOutcome Outcome,
    int Probes,
    TimeSpan Waited,
    string Advice);

/// <summary>A resource name, and whether the service will accept it.</summary>
/// <param name="Name">The name that was composed.</param>
/// <param name="IsValid">Whether it satisfies the service's rules.</param>
/// <param name="Violation">The rule it breaks, or <see langword="null"/>.</param>
public sealed record ResourceName(string Name, bool IsValid, string? Violation);

/// <summary>The tags every resource this course creates must carry.</summary>
/// <param name="Owner">Who to ask before deleting it.</param>
/// <param name="ManagedBy">The automation that created it.</param>
/// <param name="Purpose">Why it exists.</param>
/// <param name="ExpiresOn">The date after which it is garbage.</param>
public sealed record ResourceTags(string Owner, string ManagedBy, string Purpose, DateOnly ExpiresOn)
{
    /// <summary>The value <see cref="ManagedBy"/> must hold for teardown to touch a group.</summary>
    public const string CourseManagedBy = "learning-azure";

    /// <summary>Renders the tags the way both management tracks accept them.</summary>
    /// <returns>Space-free <c>key=value</c> pairs, in a stable order.</returns>
    public IReadOnlyList<string> ToKeyValuePairs() =>
    [
        FormattableString.Invariant($"owner={Owner}"),
        FormattableString.Invariant($"managed-by={ManagedBy}"),
        FormattableString.Invariant($"purpose={Purpose}"),
        FormattableString.Invariant($"expires-on={ExpiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"),
    ];
}

/// <summary>How a resource is billed while it exists.</summary>
public enum BillingShape
{
    /// <summary>Billed for existing, whether or not anything uses it.</summary>
    Provisioned,

    /// <summary>Billed only for what it actually does.</summary>
    Consumption,

    /// <summary>Billed for the bytes it holds.</summary>
    Storage,
}

/// <summary>One resource in the live run, priced.</summary>
/// <param name="Name">What it is called in the lab.</param>
/// <param name="Shape">How it is billed.</param>
/// <param name="RatePerHourUsd">
/// The hourly rate for a provisioned resource, or the effective hourly rate of
/// the work a consumption resource is expected to do.
/// </param>
public sealed record BilledResource(string Name, BillingShape Shape, decimal RatePerHourUsd);

/// <summary>What a live run is expected to cost, and what it costs if forgotten.</summary>
/// <param name="RunCostUsd">The cost of the run itself.</param>
/// <param name="IdleCostPerDayUsd">
/// What the same resources cost per day with nobody using them. This is the
/// number that turns a forgotten teardown into a bill.
/// </param>
/// <param name="WithinBudget">Whether the run cost fits the declared ceiling.</param>
/// <param name="Dominant">The resource contributing the most idle cost.</param>
public sealed record CostEstimate(
    decimal RunCostUsd,
    decimal IdleCostPerDayUsd,
    bool WithinBudget,
    string Dominant);

/// <summary>What a teardown is allowed to do.</summary>
public enum TeardownDecision
{
    /// <summary>Delete the resource group, which removes everything inside it.</summary>
    DeleteResourceGroup,

    /// <summary>Delete only the resources the run created, one at a time.</summary>
    DeleteTaggedResources,

    /// <summary>Delete nothing, because the scope cannot be proved safe.</summary>
    Refuse,
}

/// <summary>A resource group as the platform reports it, at teardown time.</summary>
/// <param name="Name">The group name.</param>
/// <param name="Scope">Its scope path.</param>
/// <param name="Tags">The tags it carries, if any.</param>
/// <param name="ForeignResourceCount">
/// How many resources inside it were not created by this run — that is, do not
/// carry the run's own tags.
/// </param>
public sealed record ResourceGroupState(
    string Name,
    ResourceScope Scope,
    ResourceTags? Tags,
    int ForeignResourceCount);

/// <summary>A teardown that has been decided but not yet run.</summary>
/// <param name="Decision">What it will do.</param>
/// <param name="Scope">The exact scope it will act on.</param>
/// <param name="Reason">Why that decision and not a broader one.</param>
public sealed record TeardownAction(TeardownDecision Decision, ResourceScope? Scope, string Reason);

/// <summary>What the platform still reports after the delete returned.</summary>
/// <param name="ResourceGroupExists">Whether the group is still listed.</param>
/// <param name="SoftDeletedStorageAccounts">Accounts recoverable inside the retention window.</param>
/// <param name="SoftDeletedKeyVaults">Vaults in the soft-deleted state.</param>
/// <param name="SoftDeletedLogAnalyticsWorkspaces">Workspaces in the soft-deleted state.</param>
/// <param name="OrphanedRoleAssignments">
/// Assignments whose principal or scope no longer resolves. Deleting the scope
/// removes assignments made inside it; deleting the <em>principal</em> does
/// not, which is how a directory fills up with "Identity not found".
/// </param>
public sealed record CleanupProbe(
    bool ResourceGroupExists,
    int SoftDeletedStorageAccounts,
    int SoftDeletedKeyVaults,
    int SoftDeletedLogAnalyticsWorkspaces,
    int OrphanedRoleAssignments);

/// <summary>Whether the subscription is genuinely back where it started.</summary>
/// <param name="Complete">Whether nothing chargeable or recoverable remains.</param>
/// <param name="Remnants">
/// What is left, in the order a learner should deal with it. Empty when the
/// cleanup is complete.
/// </param>
public sealed record CleanupVerdict(bool Complete, IReadOnlyList<string> Remnants);
