namespace LearningAzure.Exercises.BlobLifecycle;

/// <summary>One artifact revision: its bytes and the version the service gave them.</summary>
/// <param name="Content">The stored bytes.</param>
/// <param name="ETag">The service's opaque version token for exactly these bytes.</param>
public sealed record ArtifactRevision(byte[] Content, string ETag);

/// <summary>What a conditional write actually meant.</summary>
public enum PreconditionOutcome
{
    /// <summary>The write landed: the precondition held.</summary>
    Written,

    /// <summary>412: somebody else wrote first. The caller's copy is stale.</summary>
    Stale,

    /// <summary>409: the blob already exists and the caller demanded it did not.</summary>
    AlreadyExists,

    /// <summary>404: the blob is gone. Not an error unless the caller expected it.</summary>
    Absent,
}

/// <summary>What a caller should do about a failed request, decided once and centrally.</summary>
public enum RecoveryAction
{
    /// <summary>Re-read the current revision and re-apply the change to it.</summary>
    RereadAndRetry,

    /// <summary>Wait and retry the identical request: the service asked for it.</summary>
    BackOffAndRetry,

    /// <summary>Stop. Retrying cannot change the answer.</summary>
    Abort,

    /// <summary>Treat as "no such artifact" and take the caller's absent path.</summary>
    TreatAsAbsent,
}

/// <summary>The access tier a lifecycle rule can move a blob to.</summary>
public enum AccessTier
{
    /// <summary>Frequent access; highest storage price, lowest access price.</summary>
    Hot,

    /// <summary>Infrequent access; 30-day minimum retention.</summary>
    Cool,

    /// <summary>Rare access; 180-day minimum retention and a rehydration delay.</summary>
    Archive,
}

/// <summary>One tier transition inside a lifecycle rule.</summary>
/// <param name="Tier">The tier to move to.</param>
/// <param name="AfterDays">Days since last modification before the move happens.</param>
public sealed record TierTransition(AccessTier Tier, int AfterDays);

/// <summary>
/// The retention promise an expedition makes, expressed as service settings.
/// </summary>
/// <param name="SoftDeleteRetentionDays">Days a deleted blob stays recoverable, or 0 for off.</param>
/// <param name="VersioningEnabled">Whether every overwrite keeps the previous bytes as a version.</param>
/// <param name="VersionRetentionDays">Days a non-current version is kept, or 0 for forever.</param>
/// <param name="Transitions">Tier transitions, in the order they are declared.</param>
public sealed record RetentionPlan(
    int SoftDeleteRetentionDays,
    bool VersioningEnabled,
    int VersionRetentionDays,
    IReadOnlyList<TierTransition> Transitions);

/// <summary>A single reason a retention plan does not do what it promises.</summary>
/// <param name="Setting">The setting at fault.</param>
/// <param name="Problem">What is wrong with it, in the operator's language.</param>
public sealed record RetentionViolation(string Setting, string Problem);

/// <summary>
/// The seam an artifact updater writes through: read the current revision, then
/// write only if it has not changed since.
/// </summary>
/// <remarks>
/// This is a port, not an Azure type, so the update loop can be evaluated
/// without a service. <c>ConditionalArtifactStore</c> is the adapter that
/// implements it with a real <c>BlobClient</c>.
/// </remarks>
public interface IArtifactStore
{
    /// <summary>Reads the current revision, or <c>null</c> when the artifact does not exist.</summary>
    /// <param name="name">Blob name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current revision, or <c>null</c>.</returns>
    Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="content"/> only if the stored ETag still matches.</summary>
    /// <param name="name">Blob name.</param>
    /// <param name="content">The bytes to store.</param>
    /// <param name="ifMatch">The ETag the caller believes is current.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="PreconditionOutcome.Written"/> or <see cref="PreconditionOutcome.Stale"/>.</returns>
    Task<PreconditionOutcome> WriteIfUnchangedAsync(
        string name,
        byte[] content,
        string ifMatch,
        CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="content"/> only if the artifact does not exist yet.</summary>
    /// <param name="name">Blob name.</param>
    /// <param name="content">The bytes to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="PreconditionOutcome.Written"/> or <see cref="PreconditionOutcome.AlreadyExists"/>.</returns>
    Task<PreconditionOutcome> CreateIfAbsentAsync(
        string name,
        byte[] content,
        CancellationToken cancellationToken);
}
