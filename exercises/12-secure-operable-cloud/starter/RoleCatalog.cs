namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>
/// Turns an intent into the narrowest built-in role that satisfies it, and
/// judges whether the assignments that exist actually authorize a call.
/// </summary>
/// <remarks>
/// Every role name here is a built-in Azure role display name, which is what
/// both management tracks accept in place of a definition GUID. The catalog
/// deliberately contains no control-plane role: Owner and Contributor are ARM
/// roles with no storage or messaging data actions, and an application that
/// holds one of them still gets 403 on its first read.
/// </remarks>
public static class RoleCatalog
{
    /// <summary>What a refusal looks like, per service.</summary>
    /// <remarks>
    /// Only Storage publishes a stable, documented REST error code for this
    /// case. Cosmos answers 403 with a substatus in the message, and Event Hubs
    /// refuses the AMQP link rather than returning an HTTP response at all — so
    /// "look for the error code" is advice that only works on one of the three.
    /// </remarks>
    private static readonly DenialSignature StorageDenial = new(
        403,
        "AuthorizationPermissionMismatch",
        "the REST error code in the response body, and in the SDK's RequestFailedException.ErrorCode");

    private static readonly DenialSignature CosmosDenial = new(
        403,
        null,
        "a CosmosException with status 403 and a substatus in its message; there is no separate error-code field");

    private static readonly DenialSignature EventHubsDenial = new(
        null,
        null,
        "an authorization failure raised while the AMQP link is being attached; there is no HTTP status to read");

    /// <summary>Role names that are ordered by strength inside one service family.</summary>
    /// <remarks>
    /// A role implies every role below it in its own list, which is why a
    /// Contributor assignment satisfies a Reader requirement and never the
    /// other way round.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string[]> ImpliedRoles = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Storage Blob Data Owner"] = ["Storage Blob Data Contributor", "Storage Blob Data Reader"],
        ["Storage Blob Data Contributor"] = ["Storage Blob Data Reader"],
        ["Storage Queue Data Contributor"] =
        [
            "Storage Queue Data Reader",
            "Storage Queue Data Message Sender",
            "Storage Queue Data Message Processor",
        ],
        ["Storage Table Data Contributor"] = ["Storage Table Data Reader"],
        ["Azure Event Hubs Data Owner"] = ["Azure Event Hubs Data Sender", "Azure Event Hubs Data Receiver"],
        ["Cosmos DB Built-in Data Contributor"] = ["Cosmos DB Built-in Data Reader"],
    };

    /// <summary>Control-plane roles, listed so the evaluator can say why they did not help.</summary>
    /// <remarks>
    /// None of these carries a single storage or messaging data action. They
    /// are here to be recognised and reported, not to be granted.
    /// </remarks>
    public static readonly IReadOnlySet<string> ControlPlaneRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "Owner",
        "Contributor",
        "Reader",
        "Storage Account Contributor",
        "DocumentDB Account Contributor",
        "Cosmos DB Operator",
    };

    /// <summary>The least-privilege role for one intent against one service.</summary>
    /// <param name="service">The service being called.</param>
    /// <param name="intent">What the caller means to do.</param>
    /// <returns>The role, and the system it must be assigned in.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The intent has no meaning for that service — sending a message to a blob
    /// container, or administering a Cosmos account from the data plane.
    /// </exception>
    public static RoleRequirement RoleFor(AzureService service, AccessIntent intent)
    {
        // GAP 1: the narrowest role that does the job, and the system it lives in.
        //
        // Three things this mapping has to get right. Reading is not writing,
        // so a reader role exists for every service and is the default answer
        // for a consumer. A queue distinguishes *using* messages from *owning*
        // the queue, which is why Message Sender and Message Processor exist at
        // all and why a producer never needs Queue Data Contributor. And Cosmos
        // data roles are not Azure RBAC: they are account-scoped definitions
        // assigned with a different command, so they carry
        // RoleSystem.CosmosDataPlane. Refuse combinations that do not exist
        // rather than returning the nearest role.
        // See lessons/12-secure-operable-cloud/README.md#a-role-is-an-answer-to-a-question-you-have-to-ask-first
        throw new NotImplementedException(
            "GAP 1: implement RoleCatalog.RoleFor. "
            + "See lessons/12-secure-operable-cloud/README.md#a-role-is-an-answer-to-a-question-you-have-to-ask-first.");
    }

    /// <summary>How a refusal presents for one service.</summary>
    /// <param name="service">The service that refused.</param>
    /// <returns>The status, error code, and where to read it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The service is not one this course uses.</exception>
    public static DenialSignature DenialFor(AzureService service) => service switch
    {
        AzureService.BlobStorage or AzureService.QueueStorage or AzureService.TableStorage => StorageDenial,
        AzureService.EventHubs => EventHubsDenial,
        AzureService.CosmosNoSql => CosmosDenial,
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unknown service."),
    };

    /// <summary>The narrowest single scope that covers every target.</summary>
    /// <param name="targets">The entities one assignment has to reach.</param>
    /// <returns>The deepest scope that contains all of them.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="targets"/> is empty, or the targets live in different
    /// subscriptions — in which case there is no least-privilege answer below a
    /// management group, and inventing one would widen the assignment silently.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is <see langword="null"/>.</exception>
    public static ResourceScope NarrowestScope(IReadOnlyList<ResourceScope> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("There is no scope that covers nothing.", nameof(targets));
        }

        // GAP 2: the deepest common ancestor, truncated to a real scope boundary.
        //
        // Two containers in one account share the account, not the container.
        // Two accounts in one group share the group. A path is only a scope at
        // certain lengths -- subscription, resource group, resource, and then
        // pairs of child segments -- so a common prefix that stops halfway
        // through a provider identifier has to fall back to the last real
        // boundary, not be used as-is. ResourceScope.LevelFor knows where those
        // boundaries are.
        // See lessons/12-secure-operable-cloud/README.md#scope-is-the-other-half-of-the-grant
        throw new NotImplementedException(
            "GAP 2: implement RoleCatalog.NarrowestScope. "
            + "See lessons/12-secure-operable-cloud/README.md#scope-is-the-other-half-of-the-grant.");
    }

    /// <summary>Whether the assignments that exist authorize one specific call.</summary>
    /// <param name="request">The call about to be made.</param>
    /// <param name="assignments">Every assignment the calling identity holds.</param>
    /// <returns>The verdict, and the role that would fix a refusal.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static AuthorizationOutcome Evaluate(
        AccessRequest request,
        IReadOnlyList<RoleAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(assignments);

        // GAP 3: an assignment authorizes a call only if all three halves line up.
        //
        // The role has to be the required one or a role that implies it; the
        // scope has to *contain* the target, not merely mention it; and the
        // role system has to match, because a Cosmos data role recorded as an
        // Azure RBAC assignment grants nothing at all. Control-plane roles are
        // the trap worth naming explicitly: Owner satisfies none of this, and a
        // refusal that says "you have Owner, and Owner is not a data role" is
        // worth ten minutes of somebody's afternoon.
        // See lessons/12-secure-operable-cloud/README.md#owner-is-not-a-data-role
        throw new NotImplementedException(
            "GAP 3: implement RoleCatalog.Evaluate. "
            + "See lessons/12-secure-operable-cloud/README.md#owner-is-not-a-data-role.");
    }

    /// <summary>Whether a held role is, or implies, the required one.</summary>
    /// <param name="held">The role name on the assignment.</param>
    /// <param name="required">The role name the intent needs.</param>
    /// <returns><see langword="true"/> when the held role is enough.</returns>
    public static bool Satisfies(string held, string required) =>
        string.Equals(held, required, StringComparison.Ordinal)
        || (ImpliedRoles.TryGetValue(held, out var implied)
            && implied.Contains(required, StringComparer.Ordinal));
}
