namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>What one projection pass did, and what it cost.</summary>
/// <param name="Written">Entries stored.</param>
/// <param name="Superseded">Entries the store already held at this position or later.</param>
/// <param name="ConcurrencyRetries">Attempts that lost an ETag race and were recomputed.</param>
/// <param name="ThrottleRetries">Attempts the service rate limited and the projector retried.</param>
/// <param name="RequestCharge">Total request units consumed, including losing attempts.</param>
public sealed record ProjectionReport(
    int Written,
    int Superseded,
    int ConcurrencyRetries,
    int ThrottleRetries,
    double RequestCharge);

/// <summary>Projects handled telemetry into the queryable journal.</summary>
/// <remarks>
/// <para>
/// Milestone 4. Cosmos DB is provisioned-throughput storage, so three properties
/// that are invisible in Blob, Queue, and Table Storage become first-class here:
/// </para>
/// <list type="bullet">
/// <item><b>The partition key is on every operation.</b> A point read that names
/// the key costs about 1 RU; the same read without it fans out to every physical
/// partition and costs in proportion to the container, not to the answer.</item>
/// <item><b>429 is a normal answer.</b> Exceeding provisioned throughput is
/// rate limiting, not an outage, and the service says how long to wait. Treating
/// it as a failure produces an application that falls over under exactly the load
/// it was provisioned for.</item>
/// <item><b>Every attempt is charged</b>, including the ones that lose. A retry
/// loop with no budget is a bill with no ceiling.</item>
/// </list>
/// <para>
/// Idempotency here is a <em>comparison</em>, not a lock. The entry carries the
/// stream position it was projected from, so a replayed event finds a stored
/// entry at the same or a later position and stops. That is what lets the
/// projection absorb the duplicates the checkpoint interval guarantees.
/// </para>
/// </remarks>
/// <param name="projection">The journal container.</param>
/// <param name="maxThrottleRetries">Rate-limited attempts allowed per operation.</param>
/// <param name="maxConcurrencyRetries">ETag races allowed per entry.</param>
public sealed class JournalProjector(
    IJournalProjection projection,
    int maxThrottleRetries = 3,
    int maxConcurrencyRetries = 5)
{
    private readonly IJournalProjection _projection =
        projection ?? throw new ArgumentNullException(nameof(projection));

    private readonly int _maxThrottleRetries = maxThrottleRetries >= 0
        ? maxThrottleRetries
        : throw new ArgumentOutOfRangeException(nameof(maxThrottleRetries));

    private readonly int _maxConcurrencyRetries = maxConcurrencyRetries > 0
        ? maxConcurrencyRetries
        : throw new ArgumentOutOfRangeException(nameof(maxConcurrencyRetries));

    /// <summary>How long the projector waited for the service, in total.</summary>
    public TimeSpan ThrottleDelay { get; private set; }

    /// <summary>Projects one entry, absorbing replays and losing races safely.</summary>
    /// <param name="entry">The entry to project.</param>
    /// <param name="delay">How the projector waits between throttled attempts.</param>
    /// <param name="cancellationToken">Cancels the projection.</param>
    /// <returns>What the write did and what it cost.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The concurrency budget was spent.</exception>
    /// <exception cref="ThrottledException">The throttle budget was spent.</exception>
    public async Task<ProjectionReport> ProjectAsync(
        JournalEntry entry,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var charge = 0.0;
        var concurrencyRetries = 0;
        var throttleRetries = 0;

        for (var attempt = 1; attempt <= _maxConcurrencyRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GAP 16 — Read by partition key AND id, inside the loop.
            //
            // A read that omits the partition key is a cross-partition query
            // wearing a point read's clothes. Re-reading inside the loop is what
            // makes the second attempt decide against fresh state instead of
            // resending a stale conclusion under a fresh ETag.
            var stored = await WithThrottleRetryAsync(
                token => _projection.TryReadAsync(entry.StationId, entry.Id, token),
                delay,
                charge: value => charge += value,
                retried: () => throttleRetries++,
                cancellationToken).ConfigureAwait(false);

            // A stored entry at the same position or later means a replay of an
            // event already projected. Writing anyway would be correct and
            // wasteful; the point of the comparison is that it is neither.
            if (stored is not null && stored.SequenceNumber >= entry.SequenceNumber)
            {
                return new ProjectionReport(0, 1, concurrencyRetries, throttleRetries, charge);
            }

            var result = await WithThrottleRetryAsync(
                token => _projection.WriteAsync(entry, stored?.ETag, token),
                delay,
                charge: value => charge += value,
                retried: () => throttleRetries++,
                cancellationToken).ConfigureAwait(false);

            charge += result.RequestCharge;

            switch (result.Outcome)
            {
                case ProjectionOutcome.Written:
                    return new ProjectionReport(1, 0, concurrencyRetries, throttleRetries, charge);

                case ProjectionOutcome.Superseded:
                    return new ProjectionReport(0, 1, concurrencyRetries, throttleRetries, charge);

                default:
                    // GAP 17 — A lost ETag race is re-decided, not re-sent.
                    //
                    // Resending the same body under a fresh ETag is the lost
                    // update the ETag existed to prevent, dressed as a working
                    // retry. Go round the loop: read again, compare again.
                    concurrencyRetries++;
                    break;
            }
        }

        throw new InvalidOperationException(
            $"Journal entry '{entry.Id}' in station '{entry.StationId}' stayed contended for "
            + $"{_maxConcurrencyRetries} attempts. Something else is writing the same document, "
            + "which is a modelling problem rather than a retry problem.");
    }

    /// <summary>Reads a whole station by paging a single-partition query to the end.</summary>
    /// <param name="stationId">The partition key the query is scoped to.</param>
    /// <param name="pageSize">How many entries each page is asked for.</param>
    /// <param name="delay">How the reader waits between throttled attempts.</param>
    /// <param name="cancellationToken">Cancels the read between pages.</param>
    /// <returns>Every entry of the station, and what reading them cost.</returns>
    public async Task<(IReadOnlyList<JournalEntry> Entries, double RequestCharge, int Pages)> ReadStationAsync(
        string stationId,
        int pageSize,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var entries = new List<JournalEntry>();
        var charge = 0.0;
        var pages = 0;
        string? continuation = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = continuation;
            var page = await WithThrottleRetryAsync(
                ct => _projection.QueryStationAsync(stationId, pageSize, token, ct),
                delay,
                charge: value => charge += value,
                retried: () => { },
                cancellationToken).ConfigureAwait(false);

            pages++;
            entries.AddRange(page.Entries);
            charge += page.RequestCharge;
            continuation = page.ContinuationToken;

            // GAP 18 — The continuation token is the only end-of-results signal.
            //
            // Cosmos may cut a page short at a size or time budget and still have
            // more to give, so "fewer items than I asked for" means nothing. A
            // reader that stops on a short page silently truncates its answer,
            // and does so only under load, which is when it matters most.
        }
        while (continuation is not null);

        return (entries, charge, pages);
    }

    private async Task<T> WithThrottleRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Action<double> charge,
        Action retried,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (ThrottledException throttled) when (attempt < _maxThrottleRetries)
            {
                // The refused attempt is still charged. Counting only successful
                // attempts is how a throttled workload's real cost stays hidden
                // until the invoice arrives.
                charge(throttled.RequestCharge);
                retried();
                ThrottleDelay += throttled.RetryAfter;

                if (delay is not null)
                {
                    await delay(throttled.RetryAfter, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
