namespace LearningAzure.Exercises.BlobLifecycle;

/// <summary>Applies a change to an artifact without ever overwriting somebody else's work.</summary>
/// <remarks>
/// The read-modify-write loop is the only correct shape for a shared artifact:
/// read the current revision, apply the change to <em>that</em> revision, write
/// it back conditionally, and start over if the condition failed. Every step is
/// necessary, and skipping the re-read is the classic bug — it retries a stale
/// value forever and eventually wins the race, destroying the other write.
/// </remarks>
public static class ArtifactUpdater
{
    /// <summary>The default attempt budget: enough for real contention, small enough to fail fast.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>Reads, applies <paramref name="change"/>, and writes conditionally until it lands.</summary>
    /// <param name="store">The conditional store.</param>
    /// <param name="name">Blob name.</param>
    /// <param name="change">Applied to the current bytes to produce the new bytes.</param>
    /// <param name="maxAttempts">Attempt budget; exceeding it throws.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The number of attempts the update actually cost.</returns>
    /// <exception cref="InvalidOperationException">The artifact does not exist.</exception>
    /// <exception cref="ConcurrencyExhaustedException">The budget ran out under contention.</exception>
    public static async Task<int> UpdateAsync(
        IArtifactStore store,
        string name,
        Func<byte[], byte[]> change,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GAP 4 — Re-read INSIDE the loop.
            //
            // Hoisting this above the loop turns the retry into "try the same
            // stale write again", which is not a retry: it is a slower way to
            // lose the same update.
            var current = await store.TryReadAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Artifact '{name}' does not exist. Create it with CreateIfAbsentAsync first.");

            var updated = change(current.Content);

            var outcome = await store
                .WriteIfUnchangedAsync(name, updated, current.ETag, cancellationToken)
                .ConfigureAwait(false);

            if (outcome == PreconditionOutcome.Written)
            {
                return attempt;
            }
        }

        // GAP 5 — Give up loudly.
        //
        // An unbounded loop under sustained contention is a livelock that looks
        // like a hang. A bounded one turns it into an incident with a number in
        // it, which is something an operator can act on.
        throw new ConcurrencyExhaustedException(name, maxAttempts);
    }
}

/// <summary>Raised when a conditional update lost its race on every attempt.</summary>
public sealed class ConcurrencyExhaustedException : Exception
{
    /// <summary>Creates the exception for <paramref name="name"/> after <paramref name="attempts"/> attempts.</summary>
    /// <param name="name">The contended artifact.</param>
    /// <param name="attempts">How many attempts were spent.</param>
    public ConcurrencyExhaustedException(string name, int attempts)
        : base($"Gave up updating '{name}' after {attempts} attempts: another writer won every race.")
    {
        ArtifactName = name;
        Attempts = attempts;
    }

    /// <summary>Creates the exception with a message.</summary>
    public ConcurrencyExhaustedException(string message)
        : base(message)
    {
        ArtifactName = string.Empty;
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public ConcurrencyExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ArtifactName = string.Empty;
    }

    /// <summary>Creates an empty exception.</summary>
    public ConcurrencyExhaustedException()
        : base("A conditional update lost its race on every attempt.")
    {
        ArtifactName = string.Empty;
    }

    /// <summary>The contended artifact.</summary>
    public string ArtifactName { get; }

    /// <summary>How many attempts were spent before giving up.</summary>
    public int Attempts { get; }
}
