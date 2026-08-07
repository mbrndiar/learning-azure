namespace LearningAzure.Projects.FieldStation;

/// <summary>What claiming an observation for processing established.</summary>
public enum ClaimOutcome
{
    /// <summary>This caller created the row and owns the first attempt.</summary>
    Claimed,

    /// <summary>A previous attempt claimed the row and never confirmed; this delivery resumes it.</summary>
    Resumed,

    /// <summary>The effect has already been applied and confirmed. Do not apply it again.</summary>
    AlreadyProcessed,

    /// <summary>The observation was quarantined; it needs a human, not another attempt.</summary>
    AlreadyQuarantined,
}

/// <summary>
/// Records where every observation of every station has got to, as point-readable
/// rows with concurrency-safe updates.
/// </summary>
/// <remarks>
/// <para>
/// Milestone 4. The index is not a report: it is the pipeline's ledger. The
/// observation row is the idempotency gate — an insert that loses to an existing
/// row is precisely how a duplicate delivery is detected — and the per-station
/// summary row is the contended value that proves the ETag discipline works.
/// </para>
/// <para>
/// The distinction the states encode is worth stating once: the row records that
/// the effect <em>completed</em>, not that it was attempted. A row left
/// <see cref="ProcessingState.Pending"/> by a crashed worker therefore means "the
/// effect may or may not have happened", and the only safe reading of that is to
/// run it again. Recording completion is what makes the redelivery of a finished
/// work order free.
/// </para>
/// </remarks>
/// <param name="index">The status index to project into.</param>
/// <param name="clock">The clock stamped on every row; injected so runs are reproducible.</param>
public sealed class StationStatusProjector(IStationStatusIndex index, TimeProvider clock)
{
    /// <summary>The index this projector writes to.</summary>
    public IStationStatusIndex Index { get; } = index ?? throw new ArgumentNullException(nameof(index));

    /// <summary>The clock every row is stamped with.</summary>
    public TimeProvider Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Claims one observation for processing.</summary>
    /// <param name="order">The work order being processed.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>What the claim established about this delivery.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is <c>null</c>.</exception>
    public async Task<ClaimOutcome> TryClaimAsync(WorkOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var row = new StationStatus
        {
            StationId = order.StationId,
            RowKey = StationNaming.StatusRowKey(order.ObservationId),
            State = ProcessingState.Pending,
            ProcessedCount = 0,
            ArtifactName = order.ArtifactName,
            UpdatedUtc = Clock.GetUtcNow(),
        };

        // GAP 7 — The claim is an INSERT, and its failure is the answer.
        //
        // "Read, and insert if absent" is the same race intake avoided one
        // milestone ago, and here it is worse: two workers holding the same
        // redelivered message both read "absent" and both apply the effect. The
        // service arbitrates a conditional insert; nothing else does.
        if (await Index.TryInsertAsync(row, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ClaimOutcome.Claimed;
        }

        var existing = await Index
            .TryGetAsync(order.StationId, row.RowKey, cancellationToken)
            .ConfigureAwait(false);

        // A row that vanished between the failed insert and this read is a
        // concurrent cleanup, not a duplicate. Treat it as unclaimed rather than
        // inventing a state nobody stored.
        return existing?.State switch
        {
            ProcessingState.Processed => ClaimOutcome.AlreadyProcessed,
            ProcessingState.Quarantined => ClaimOutcome.AlreadyQuarantined,
            _ => ClaimOutcome.Resumed,
        };
    }

    /// <summary>Confirms that the effect for one observation completed.</summary>
    /// <param name="order">The work order whose effect completed.</param>
    /// <param name="maxAttempts">How many times to re-read and retry a contended update.</param>
    /// <param name="cancellationToken">Cancels the confirmation.</param>
    /// <returns>The station's processed total after this confirmation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The row disappeared, or contention never cleared.</exception>
    public async Task<int> ConfirmProcessedAsync(
        WorkOrder order,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var rowKey = StationNaming.StatusRowKey(order.ObservationId);
        var transitioned = await TransitionAsync(
            order.StationId,
            rowKey,
            ProcessingState.Processed,
            maxAttempts,
            cancellationToken).ConfigureAwait(false);

        // Only the transition that actually moved the row out of Pending may
        // count towards the station total. Counting on every confirmation turns
        // one redelivered message into an inflated station report.
        return transitioned
            ? await IncrementStationTotalAsync(order.StationId, maxAttempts, cancellationToken).ConfigureAwait(false)
            : await ReadStationTotalAsync(order.StationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that one observation was moved aside for a human.</summary>
    /// <param name="order">The work order that was quarantined.</param>
    /// <param name="maxAttempts">How many times to re-read and retry a contended update.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>A task that completes when the row records the quarantine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is <c>null</c>.</exception>
    public async Task MarkQuarantinedAsync(
        WorkOrder order,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        await TransitionAsync(
            order.StationId,
            StationNaming.StatusRowKey(order.ObservationId),
            ProcessingState.Quarantined,
            maxAttempts,
            cancellationToken,
            createIfMissing: new StationStatus
            {
                StationId = order.StationId,
                RowKey = StationNaming.StatusRowKey(order.ObservationId),
                State = ProcessingState.Quarantined,
                ProcessedCount = 0,
                ArtifactName = order.ArtifactName,
                UpdatedUtc = Clock.GetUtcNow(),
            }).ConfigureAwait(false);
    }

    /// <summary>Reads the station's processed total from its summary row.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The total, or zero when the station has processed nothing.</returns>
    public async Task<int> ReadStationTotalAsync(string stationId, CancellationToken cancellationToken)
    {
        var summary = await Index
            .TryGetAsync(stationId, StationNaming.SummaryRowKey, cancellationToken)
            .ConfigureAwait(false);

        return summary?.ProcessedCount ?? 0;
    }

    /// <summary>Adds one to the station's contended summary row, safely.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="maxAttempts">How many times to re-read and retry.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The total after this increment.</returns>
    /// <exception cref="InvalidOperationException">Contention never cleared within the attempt budget.</exception>
    public async Task<int> IncrementStationTotalAsync(
        string stationId,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // GAP 8 — The RE-READ belongs inside the loop.
            //
            // A retry that resends the same stale ETag fails identically forever;
            // a retry that resends a fresh ETag carrying the value computed from
            // the STALE read silently reintroduces the lost update the ETag was
            // there to prevent. Both look like a working retry from outside.
            var summary = await Index
                .TryGetAsync(stationId, StationNaming.SummaryRowKey, cancellationToken)
                .ConfigureAwait(false);

            if (summary is null)
            {
                var created = new StationStatus
                {
                    StationId = stationId,
                    RowKey = StationNaming.SummaryRowKey,
                    State = ProcessingState.Processed,
                    ProcessedCount = 1,
                    UpdatedUtc = Clock.GetUtcNow(),
                };

                if (await Index.TryInsertAsync(created, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return 1;
                }

                // Someone created the summary first. Fall through and contend
                // for it the normal way instead of overwriting their count.
                continue;
            }

            var next = summary.ProcessedCount + 1;
            summary.ProcessedCount = next;
            summary.UpdatedUtc = Clock.GetUtcNow();

            if (await Index.TryReplaceAsync(summary, summary.ETag, cancellationToken).ConfigureAwait(false) is not null)
            {
                return next;
            }
        }

        throw new InvalidOperationException(
            $"The summary row for station '{stationId}' stayed contended for {maxAttempts} attempts. "
            + "Raise the attempt budget or reduce writer concurrency.");
    }

    private async Task<bool> TransitionAsync(
        string stationId,
        string rowKey,
        ProcessingState target,
        int maxAttempts,
        CancellationToken cancellationToken,
        StationStatus? createIfMissing = null)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var row = await Index.TryGetAsync(stationId, rowKey, cancellationToken).ConfigureAwait(false);

            if (row is null)
            {
                if (createIfMissing is null)
                {
                    throw new InvalidOperationException(
                        $"Status row '{stationId}/{rowKey}' is missing; it must be claimed before it is settled.");
                }

                if (await Index.TryInsertAsync(createIfMissing, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return true;
                }

                continue;
            }

            if (row.State == target)
            {
                // A redelivered confirmation is not an error and must not count
                // a second time.
                return false;
            }

            row.State = target;
            row.ProcessedCount = target == ProcessingState.Processed ? 1 : row.ProcessedCount;
            row.UpdatedUtc = Clock.GetUtcNow();

            if (await Index.TryReplaceAsync(row, row.ETag, cancellationToken).ConfigureAwait(false) is not null)
            {
                return true;
            }
        }

        throw new InvalidOperationException(
            $"Status row '{stationId}/{rowKey}' stayed contended for {maxAttempts} attempts.");
    }
}
