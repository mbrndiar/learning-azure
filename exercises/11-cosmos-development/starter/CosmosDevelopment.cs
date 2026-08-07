namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>One page of a query result, exactly as the service returns it.</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Items">The documents in this page.</param>
/// <param name="ContinuationToken">
/// Where to resume, or <see langword="null"/> when the result set is exhausted.
/// </param>
/// <param name="RequestCharge">What this page cost.</param>
public sealed record Page<T>(
    IReadOnlyList<T> Items,
    string? ContinuationToken,
    double RequestCharge);

/// <summary>A request for one page.</summary>
/// <param name="MaxItemCount">The most documents the page may contain.</param>
/// <param name="ContinuationToken">
/// The token from the previous page, or <see langword="null"/> for the first.
/// </param>
public sealed record PageRequest(int MaxItemCount, string? ContinuationToken);

/// <summary>The outcome of draining a query to the end.</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Items">Every document, in page order.</param>
/// <param name="Pages">How many round trips it took.</param>
/// <param name="RequestCharge">The sum of every page's charge.</param>
public sealed record DrainResult<T>(
    IReadOnlyList<T> Items,
    int Pages,
    double RequestCharge);

/// <summary>A stored document, reduced to the parts a writer has to reason about.</summary>
/// <param name="Id">The document id.</param>
/// <param name="PartitionKey">The value of its partition key path.</param>
/// <param name="ETag">The version the store currently holds.</param>
/// <param name="Corrections">A counter two writers might race to increment.</param>
public sealed record StoredDocument(
    string Id,
    string PartitionKey,
    string ETag,
    int Corrections);

/// <summary>How a conditional write ended.</summary>
public enum WriteOutcome
{
    /// <summary>The change was committed.</summary>
    Applied,

    /// <summary>Every attempt lost the race and the budget ran out.</summary>
    Exhausted,

    /// <summary>The store refused for a reason retrying cannot fix.</summary>
    Rejected,
}

/// <summary>What a bounded conditional-write loop achieved, and at what price.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Attempts">How many times the write was tried.</param>
/// <param name="ETag">The version finally written, when one was.</param>
/// <param name="StatusCode">The status that ended the loop.</param>
public sealed record ConcurrencyResult(
    WriteOutcome Outcome,
    int Attempts,
    string? ETag,
    int StatusCode);

/// <summary>One wait in a retry schedule.</summary>
/// <param name="Attempt">The one-based attempt this wait precedes.</param>
/// <param name="Delay">How long to wait.</param>
/// <param name="FromServer">
/// Whether the wait came from the service's own <c>x-ms-retry-after-ms</c>
/// header rather than from the client's backoff curve.
/// </param>
public sealed record RetryStep(int Attempt, TimeSpan Delay, bool FromServer);

/// <summary>A complete retry schedule, decided before any of it is executed.</summary>
/// <param name="Steps">The waits, in order.</param>
/// <param name="Exhausted">Whether the schedule ran out before succeeding.</param>
/// <param name="TotalDelay">The sum of every wait.</param>
public sealed record RetryPlan(
    IReadOnlyList<RetryStep> Steps,
    bool Exhausted,
    TimeSpan TotalDelay);

/// <summary>One response from a throttled service.</summary>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="RetryAfter">
/// What the service asked the client to wait, when it said anything at all.
/// </param>
public sealed record ServiceResponse(int StatusCode, TimeSpan? RetryAfter);

/// <summary>One operation queued for a transactional batch.</summary>
/// <param name="Id">The document id.</param>
/// <param name="PartitionKey">The logical partition it belongs to.</param>
/// <param name="SizeBytes">How large the serialised operation is.</param>
public sealed record BatchOperation(string Id, string PartitionKey, int SizeBytes);

/// <summary>A set of operations that may legally be sent as one batch.</summary>
/// <param name="PartitionKey">The single logical partition they share.</param>
/// <param name="Operations">The operations, in submission order.</param>
public sealed record BatchGroup(string PartitionKey, IReadOnlyList<BatchOperation> Operations);

/// <summary>What a failed batch actually says, once the noise is removed.</summary>
/// <param name="CulpritIndex">
/// The position of the operation that failed, or <c>-1</c> when the batch
/// succeeded.
/// </param>
/// <param name="StatusCode">That operation's status.</param>
/// <param name="Collateral">
/// How many operations reported 424 Failed Dependency: they would have worked.
/// </param>
public sealed record BatchDiagnosis(int CulpritIndex, int StatusCode, int Collateral);

/// <summary>Whether an interrupted operation may simply be sent again.</summary>
public enum RetrySafety
{
    /// <summary>Sending it again cannot produce a second effect.</summary>
    Safe,

    /// <summary>Sending it again might duplicate or clobber.</summary>
    Unsafe,
}

/// <summary>How to remove data.</summary>
public enum CleanupStrategy
{
    /// <summary>Delete the container: instant, free, total.</summary>
    DeleteContainer,

    /// <summary>Let time-to-live expire the documents in the background.</summary>
    TimeToLive,

    /// <summary>Query for the documents and delete each one.</summary>
    DeletePerDocument,
}

/// <summary>A chosen way to remove data, and what it will cost.</summary>
/// <param name="Strategy">The chosen mechanism.</param>
/// <param name="RequestUnits">
/// The request units the deletion itself will be charged, at the moment it
/// runs. A background expiry is not free, but it is not charged here.
/// </param>
/// <param name="Reason">Why this mechanism and not the others.</param>
public sealed record CleanupPlan(
    CleanupStrategy Strategy,
    double RequestUnits,
    string Reason);
