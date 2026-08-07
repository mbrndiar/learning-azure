namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>A store that accepts a write only against the version you read.</summary>
public interface IConditionalStore
{
    /// <summary>Reads the current version of a document.</summary>
    /// <param name="id">The document id.</param>
    /// <returns>The document as it is now.</returns>
    StoredDocument Read(string id);

    /// <summary>Writes a document, but only if it has not changed since.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="ifMatchEtag">The version the caller believes is current.</param>
    /// <returns>
    /// <c>200</c> when the write was applied, <c>412</c> when the version was
    /// stale, and any other status when the store refused for its own reasons.
    /// </returns>
    int TryReplace(StoredDocument document, string ifMatchEtag);
}

/// <summary>
/// Applies a change to a document without losing a concurrent one, and without
/// spinning forever trying.
/// </summary>
public sealed class ConcurrencyGuard
{
    /// <summary>The status a stale conditional write comes back as.</summary>
    public const int PreconditionFailed = 412;

    /// <summary>The status a throttled request comes back as.</summary>
    public const int TooManyRequests = 429;

    /// <summary>The status a successful write comes back as.</summary>
    public const int Ok = 200;

    /// <summary>Decides whether a status is worth trying again.</summary>
    /// <param name="statusCode">The status the store returned.</param>
    /// <returns><see langword="true"/> when a further attempt could succeed.</returns>
    public static bool ShouldRetry(int statusCode)
    {
        // GAP 4: retry what a later attempt could plausibly change.
        //
        // 412 means someone else got there first, and the next attempt reads
        // their version — that resolves. 429, 503 and 408 are the service
        // asking for time. 404, 409 and 400 will return exactly the same answer
        // no matter how many times they are asked, so retrying them converts a
        // fast, clear failure into a slow, identical one.
        // See lessons/11-cosmos-development/README.md#retry-is-a-budget-not-a-loop
        throw new NotImplementedException(
            "GAP 4: implement ConcurrencyGuard.ShouldRetry. "
            + "See lessons/11-cosmos-development/README.md#retry-is-a-budget-not-a-loop.");
    }

    /// <summary>Applies a change under optimistic concurrency, with a bound.</summary>
    /// <param name="store">The store to write to.</param>
    /// <param name="id">The document to change.</param>
    /// <param name="change">
    /// Turns the current version of the document into the version to write.
    /// This is the caller's INTENT, and it is re-applied against a freshly read
    /// document on every attempt.
    /// </param>
    /// <param name="maximumAttempts">How many times the write may be tried.</param>
    /// <returns>How the loop ended.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumAttempts"/> is not positive.</exception>
    public static ConcurrencyResult Apply(
        IConditionalStore store,
        string id,
        Func<StoredDocument, StoredDocument> change,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);

        // GAP 5: the re-read belongs INSIDE the loop.
        //
        // Reading once and retrying the same proposed document is the defect
        // this whole mechanism exists to prevent: every attempt carries the
        // same stale ETag, so every attempt fails with 412 and the loop burns
        // its budget achieving nothing. Worse, an implementation that reacts to
        // 412 by dropping the ETag and writing unconditionally has silently
        // reintroduced the lost update it was asked to prevent.
        //
        // Read, apply `change` to what was read, write conditionally on the
        // ETag that was read, and stop on success, on a status ShouldRetry
        // refuses, or when the attempt budget is spent.
        // See lessons/11-cosmos-development/README.md#an-etag-is-a-version-you-can-argue-with
        throw new NotImplementedException(
            "GAP 5: implement ConcurrencyGuard.Apply. "
            + "See lessons/11-cosmos-development/README.md#an-etag-is-a-version-you-can-argue-with.");
    }
}
