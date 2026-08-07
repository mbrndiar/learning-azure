namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>What a claim attempt found.</summary>
public enum ClaimOutcome
{
    /// <summary>The observation had never been seen; this caller owns it.</summary>
    Claimed,

    /// <summary>Another delivery claimed it and has already finished. Do not repeat the effect.</summary>
    AlreadyJournaled,

    /// <summary>A previous attempt claimed it and did not confirm. It may or may not have run.</summary>
    Resumed,

    /// <summary>The observation was quarantined and needs a human, not another attempt.</summary>
    Quarantined,
}

/// <summary>The ledger every stage of the journal reads and writes.</summary>
/// <remarks>
/// <para>
/// Milestone 2. The registry is not a report; it is the pipeline's ledger, and
/// three distinct guarantees rest on it:
/// </para>
/// <list type="bullet">
/// <item>a conditional <b>insert</b> decides which delivery of a duplicated
/// observation gets to apply the effect;</item>
/// <item>an <b>ETag replace</b> stops two workers from losing each other's count
/// on the contended watermark row; and</item>
/// <item>the watermark row remembers how far the consumer got on each stream
/// partition, so a replay after a restart is recognised rather than reapplied.</item>
/// </list>
/// <para>
/// The row records that an effect <b>completed</b>, not that it was attempted, so
/// a row left <see cref="StationPhase.Pending"/> by a crashed worker means "this
/// may or may not have run" — and the only safe reading of that is to run it
/// again.
/// </para>
/// </remarks>
/// <param name="registry">The registry this ledger writes to.</param>
/// <param name="clock">The clock every row is stamped with.</param>
/// <param name="maxConcurrencyRetries">Attempts allowed on the contended watermark row.</param>
public sealed class StationLedger(IStationRegistry registry, TimeProvider clock, int maxConcurrencyRetries = 5)
{
    private readonly IStationRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly int _maxConcurrencyRetries = maxConcurrencyRetries > 0
        ? maxConcurrencyRetries
        : throw new ArgumentOutOfRangeException(
            nameof(maxConcurrencyRetries),
            maxConcurrencyRetries,
            "At least one attempt is required.");

    /// <summary>How many conditional replaces the ledger has attempted.</summary>
    public int ReplaceAttempts { get; private set; }

    /// <summary>Claims one observation for this attempt, or reports who got there first.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="artifactName">The artifact the row refers to.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>What the claim found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    public async Task<ClaimOutcome> TryClaimAsync(
        ObservationKey key,
        string artifactName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);

        var row = new StationState
        {
            StationId = key.StationId,
            RowKey = key.ObservationId,
            Phase = StationPhase.Pending,
            ArtifactName = artifactName,
            UpdatedUtc = _clock.GetUtcNow(),
        };

        // GAP 6 — The claim is a conditional insert, and its failure is the answer.
        //
        // "Read, then insert if absent" is the same race intake avoided one stage
        // earlier, except now two workers both read "absent" and both apply the
        // effect. Let the service arbitrate: an insert that loses is how a
        // duplicate is detected.
        var etag = await _registry.TryInsertAsync(row, cancellationToken).ConfigureAwait(false);
        if (etag is not null)
        {
            return ClaimOutcome.Claimed;
        }

        var existing = await _registry
            .TryGetAsync(key.StationId, key.ObservationId, cancellationToken)
            .ConfigureAwait(false);

        // A row that vanished between the failed insert and this read is a row
        // somebody else deleted, which is a teardown racing a worker. Treat it as
        // a fresh claim rather than inventing a phase for it.
        return existing?.Phase switch
        {
            StationPhase.Journaled => ClaimOutcome.AlreadyJournaled,
            StationPhase.Quarantined => ClaimOutcome.Quarantined,
            StationPhase.Pending => ClaimOutcome.Resumed,
            _ => ClaimOutcome.Claimed,
        };
    }

    /// <summary>Records that the effect for one observation completed.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="phase">The phase to record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><c>true</c> when the row was moved to <paramref name="phase"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    public async Task<bool> ConfirmAsync(
        ObservationKey key,
        StationPhase phase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var existing = await _registry
            .TryGetAsync(key.StationId, key.ObservationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        existing.Phase = phase;
        existing.JournaledCount = phase == StationPhase.Journaled ? 1 : 0;
        existing.UpdatedUtc = _clock.GetUtcNow();

        ReplaceAttempts++;
        var etag = await _registry
            .TryReplaceAsync(existing, existing.ETag, cancellationToken)
            .ConfigureAwait(false);

        return etag is not null;
    }

    /// <summary>Advances the station watermark past <paramref name="sequenceNumber"/>.</summary>
    /// <param name="stationId">The station whose watermark moves.</param>
    /// <param name="sequenceNumber">The stream position that was fully handled.</param>
    /// <param name="journaledDelta">How many observations this call journaled.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The watermark row as it now stands.</returns>
    /// <exception cref="InvalidOperationException">The retry budget was spent without landing a write.</exception>
    public async Task<StationState> AdvanceWatermarkAsync(
        string stationId,
        long sequenceNumber,
        int journaledDelta,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        for (var attempt = 1; attempt <= _maxConcurrencyRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GAP 7 — The re-read belongs INSIDE the loop.
            //
            // A retry that resends the same stale ETag fails identically forever.
            // A retry that resends a fresh ETag carrying the value computed from
            // the stale read silently reintroduces the lost update the ETag
            // existed to prevent. Both look like a working retry from the outside;
            // only the second one corrupts the count.
            var current = await _registry
                .TryGetAsync(stationId, ExpeditionNaming.WatermarkRowKey, cancellationToken)
                .ConfigureAwait(false);

            if (current is null)
            {
                var seeded = new StationState
                {
                    StationId = stationId,
                    RowKey = ExpeditionNaming.WatermarkRowKey,
                    Phase = StationPhase.Journaled,
                    LastSequenceNumber = sequenceNumber,
                    JournaledCount = journaledDelta,
                    UpdatedUtc = _clock.GetUtcNow(),
                };

                var inserted = await _registry.TryInsertAsync(seeded, cancellationToken).ConfigureAwait(false);
                if (inserted is not null)
                {
                    seeded.ETag = inserted;
                    return seeded;
                }

                continue;
            }

            // The watermark only ever moves forward. An out-of-order or replayed
            // event carries a position the row has already passed, and accepting
            // it would let a replay rewind the consumer and redeliver everything
            // after it.
            current.LastSequenceNumber = Math.Max(current.LastSequenceNumber, sequenceNumber);
            current.JournaledCount += journaledDelta;
            current.Phase = StationPhase.Journaled;
            current.UpdatedUtc = _clock.GetUtcNow();

            ReplaceAttempts++;
            var etag = await _registry
                .TryReplaceAsync(current, current.ETag, cancellationToken)
                .ConfigureAwait(false);

            if (etag is not null)
            {
                current.ETag = etag;
                return current;
            }
        }

        // GAP 8 — Retries are bounded.
        //
        // An unbounded loop against a hot row is an outage that presents as a
        // hang, which is the hardest kind to diagnose. Failing loudly after a
        // budget is the behaviour an operator can act on.
        throw new InvalidOperationException(
            $"The watermark row for '{stationId}' stayed contended for {_maxConcurrencyRetries} attempts. "
            + "Something is writing it far faster than this consumer can, which is a partitioning "
            + "problem rather than a retry problem.");
    }

    /// <summary>Reads the watermark row of one station.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The watermark row, or <c>null</c> when the station has none yet.</returns>
    public Task<StationState?> TryReadWatermarkAsync(string stationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        return _registry.TryGetAsync(stationId, ExpeditionNaming.WatermarkRowKey, cancellationToken);
    }
}
