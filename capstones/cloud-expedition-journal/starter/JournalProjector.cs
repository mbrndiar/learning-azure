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
    public Task<ProjectionReport> ProjectAsync(
        JournalEntry entry,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        // Loop up to _maxConcurrencyRetries times, running every service call through
        // WithThrottleRetryAsync so a 429 is waited out and its charge still counted.
        //
        // GAP 16 — Read by partition key AND id, inside the loop.
        //
        // A read that omits the partition key is a cross-partition query wearing a
        // point read's clothes. Re-reading inside the loop is what makes the second
        // attempt decide against fresh state instead of resending a stale conclusion
        // under a fresh ETag. A stored entry at the same stream position or later is
        // a replay of an event already projected: report it as Superseded and stop.
        // Writing anyway would be correct and wasteful; the point of the comparison
        // is that it is neither.
        //
        // Pass the stored ETag — or null when nothing is stored — to WriteAsync.
        //
        // GAP 17 — A lost ETag race is re-decided, not re-sent.
        //
        // Resending the same body under a fresh ETag is the lost update the ETag
        // existed to prevent, dressed as a working retry. Go round the loop: read
        // again, compare again. A Conflict is somebody else's create and is already
        // Superseded; only Stale goes round again.
        //
        // A spent concurrency budget is an InvalidOperationException, not a silent
        // give-up: it is a modelling problem rather than a retry problem.
        throw new NotImplementedException(
            "GAP 16: project one entry idempotently under optimistic concurrency. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-4-the-journal-projection.");
    }

    /// <summary>Reads a whole station by paging a single-partition query to the end.</summary>
    /// <param name="stationId">The partition key the query is scoped to.</param>
    /// <param name="pageSize">How many entries each page is asked for.</param>
    /// <param name="delay">How the reader waits between throttled attempts.</param>
    /// <param name="cancellationToken">Cancels the read between pages.</param>
    /// <returns>Every entry of the station, and what reading them cost.</returns>
    public Task<(IReadOnlyList<JournalEntry> Entries, double RequestCharge, int Pages)> ReadStationAsync(
        string stationId,
        int pageSize,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        // GAP 18 — The continuation token is the only end-of-results signal.
        //
        // Page through IJournalProjection.QueryStationAsync until the continuation
        // token comes back null, accumulating entries, pages, and request charge.
        // Cosmos may cut a page short at a size or time budget and still have more
        // to give, so "fewer items than I asked for" means nothing. A reader that
        // stops on a short page silently truncates its answer, and does so only
        // under load, which is when it matters most.
        throw new NotImplementedException(
            "GAP 18: page a single-partition query to the end. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-4-the-journal-projection.");
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
