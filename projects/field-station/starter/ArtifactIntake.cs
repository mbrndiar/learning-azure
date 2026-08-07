namespace LearningAzure.Projects.FieldStation;

/// <summary>What an intake attempt did to the store.</summary>
public enum IntakeOutcome
{
    /// <summary>The artifact was new and the bytes were streamed in.</summary>
    Stored,

    /// <summary>The same observation was already preserved; nothing was written.</summary>
    Duplicate,

    /// <summary>An existing artifact was replaced under a matching precondition.</summary>
    Amended,

    /// <summary>The amendment lost a race: the caller's version was stale.</summary>
    Conflict,
}

/// <summary>The result of one intake attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ArtifactName">The derived name the attempt addressed.</param>
/// <param name="ETag">The current version, when the attempt wrote one.</param>
public sealed record IntakeResult(IntakeOutcome Outcome, string ArtifactName, string? ETag);

/// <summary>Preserves incoming observations as artifacts, exactly once.</summary>
/// <remarks>
/// <para>
/// Milestone 2. Intake is the pipeline's first idempotency boundary: a field
/// laptop that retries an upload after a timeout must not create a second
/// artifact, and two stations amending the same artifact must not silently
/// overwrite each other.
/// </para>
/// <para>
/// Both guarantees come from preconditions rather than from a read-then-write
/// check. "Does it exist?" followed by "write it" is a race with a window; the
/// conditional header has no window.
/// </para>
/// </remarks>
/// <param name="store">The artifact store this intake writes to.</param>
public sealed class ArtifactIntake(IArtifactStore store)
{
    /// <summary>The store this intake writes to.</summary>
    public IArtifactStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Preserves one observation, or reports that it was already preserved.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="content">The artifact bytes, streamed rather than buffered.</param>
    /// <param name="contentType">The content type to record.</param>
    /// <param name="cancellationToken">Cancels the intake.</param>
    /// <returns>What happened, and the artifact name it happened to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="content"/> is <c>null</c>.</exception>
    public Task<IntakeResult> PreserveAsync(
        ArtifactKey key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
            // GAP 4 — "Only if it is not there yet" is a precondition, not a lookup.
            //
            // TryReadAsync followed by a write is two round trips with a race between
            // them, and the race is not theoretical: a retrying uploader is exactly
            // the caller most likely to be in the window.
            //
            // Call Store.CreateIfAbsentAsync and map its outcome: Written is Stored,
            // AlreadyExists is Duplicate. A create can never report Stale, so treat
            // that as a defect rather than folding it into one of the two.
            throw new NotImplementedException(
                "GAP 4: preserve the artifact with a conditional create. See "
                + "projects/field-station/README.md#milestone-2-preserving-artifacts.");

    /// <summary>Amends an artifact the caller has already read, without losing a competitor's write.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="content">The replacement bytes.</param>
    /// <param name="contentType">The content type to record.</param>
    /// <param name="ifMatch">The version the replacement was computed from.</param>
    /// <param name="cancellationToken">Cancels the amendment.</param>
    /// <returns><see cref="IntakeOutcome.Amended"/>, or <see cref="IntakeOutcome.Conflict"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="content"/> is <c>null</c>.</exception>
    public Task<IntakeResult> AmendAsync(
        ArtifactKey key,
        Stream content,
        string contentType,
        string ifMatch,
        CancellationToken cancellationToken) =>
            // GAP 5 — Bet on the version the replacement was computed from.
            //
            // Passing "whatever is there now" is last-write-wins with extra steps.
            // The only safe precondition is the ETag that came back with the bytes
            // this amendment is based on, which is what `ifMatch` carries.
            //
            // Call Store.ReplaceIfUnchangedAsync and map its outcome: Written is
            // Amended, Stale is Conflict.
            throw new NotImplementedException(
                "GAP 5: amend the artifact under its current version. See "
                + "projects/field-station/README.md#milestone-2-preserving-artifacts.");

    /// <summary>Reads back one artifact and the version it is currently at.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The revision, or <c>null</c> when nothing has been preserved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    public Task<ArtifactRevision?> ReadAsync(ArtifactKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Store.TryReadAsync(StationNaming.ArtifactName(key), cancellationToken);
    }
}
