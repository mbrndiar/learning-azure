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
    public async Task<IntakeResult> PreserveAsync(
        ArtifactKey key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var name = StationNaming.ArtifactName(key);

        // GAP 4 — "Only if it is not there yet" is a precondition, not a lookup.
        //
        // TryReadAsync followed by a write is two round trips with a race between
        // them, and the race is not theoretical: a retrying uploader is exactly
        // the caller most likely to be in the window. CreateIfAbsentAsync puts
        // If-None-Match: * on the wire and lets the service arbitrate.
        var result = await Store
            .CreateIfAbsentAsync(name, content, contentType, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            WriteOutcome.Written => new IntakeResult(IntakeOutcome.Stored, name, result.ETag),
            WriteOutcome.AlreadyExists => new IntakeResult(IntakeOutcome.Duplicate, name, null),
            _ => throw new InvalidOperationException(
                $"A create returned {result.Outcome}, which only a conditional replace can return."),
        };
    }

    /// <summary>Amends an artifact the caller has already read, without losing a competitor's write.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="content">The replacement bytes.</param>
    /// <param name="contentType">The content type to record.</param>
    /// <param name="ifMatch">The version the replacement was computed from.</param>
    /// <param name="cancellationToken">Cancels the amendment.</param>
    /// <returns><see cref="IntakeOutcome.Amended"/>, or <see cref="IntakeOutcome.Conflict"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="content"/> is <c>null</c>.</exception>
    public async Task<IntakeResult> AmendAsync(
        ArtifactKey key,
        Stream content,
        string contentType,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);

        var name = StationNaming.ArtifactName(key);

        // GAP 5 — Bet on the version the replacement was computed from.
        //
        // Passing "whatever is there now" is last-write-wins with extra steps.
        // The only safe precondition is the ETag that came back with the bytes
        // this amendment is based on.
        var result = await Store
            .ReplaceIfUnchangedAsync(name, content, contentType, ifMatch, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            WriteOutcome.Written => new IntakeResult(IntakeOutcome.Amended, name, result.ETag),
            WriteOutcome.Stale => new IntakeResult(IntakeOutcome.Conflict, name, null),
            _ => throw new InvalidOperationException(
                $"A conditional replace returned {result.Outcome}, which only a create can return."),
        };
    }

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
