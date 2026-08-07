namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>What a teardown pass removed, and what it could not.</summary>
/// <param name="ReportsDeleted">Report blobs removed.</param>
/// <param name="CheckpointsDeleted">Checkpoint blobs removed.</param>
/// <param name="StationRowsDeleted">Table rows removed, including watermark rows.</param>
/// <param name="JournalEntriesDeleted">Cosmos documents removed.</param>
/// <param name="MessagesRemaining">Approximate queue depth after the pass.</param>
public sealed record TeardownReport(
    int ReportsDeleted,
    int CheckpointsDeleted,
    int StationRowsDeleted,
    int JournalEntriesDeleted,
    int MessagesRemaining)
{
    /// <summary>True when nothing the run created is left behind.</summary>
    public bool IsComplete => MessagesRemaining == 0;
}

/// <summary>Removes everything one expedition run created, across all five services.</summary>
/// <remarks>
/// <para>
/// Milestone 5, second half. Cleanup is part of the exercise rather than an
/// afterthought: a course that leaves resources behind teaches a habit that costs
/// money in a real subscription, and a cleanup nobody verifies is
/// indistinguishable from no cleanup at all.
/// </para>
/// <para>
/// Of the five services, only Cosmos DB bills for capacity that exists whether or
/// not anything is stored in it, which is why the guide's teardown deletes the
/// account rather than only its documents. Everything here is the data-level
/// pass: it is what makes a re-run reproducible, and what a shared subscription
/// needs between runs.
/// </para>
/// </remarks>
/// <param name="vault">The blob vault holding reports.</param>
/// <param name="checkpoints">The checkpoint store.</param>
/// <param name="registry">The station registry.</param>
/// <param name="queue">The queue whose residual depth is checked.</param>
/// <param name="projection">The journal projection.</param>
public sealed class ExpeditionCleanup(
    IArtifactVault vault,
    ICheckpointStore checkpoints,
    IStationRegistry registry,
    IWorkBacklog queue,
    IJournalProjection projection)
{
    private readonly IArtifactVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly ICheckpointStore _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    private readonly IStationRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IWorkBacklog _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IJournalProjection _projection = projection ?? throw new ArgumentNullException(nameof(projection));

    /// <summary>Removes everything belonging to one station, then the shared checkpoints.</summary>
    /// <param name="stationIds">The stations to remove.</param>
    /// <param name="pageSize">How many journal entries each read page requests.</param>
    /// <param name="cancellationToken">Cancels the pass between deletes.</param>
    /// <returns>What was removed, and whether anything was left.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stationIds"/> is <c>null</c>.</exception>
    public async Task<TeardownReport> RemoveAsync(
        IReadOnlyList<string> stationIds,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stationIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var reports = 0;
        var rows = 0;
        var entries = 0;

        foreach (var stationId in stationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GAP 25 — Enumerate, then delete; never assume the names.
            //
            // Deleting only what this process happens to remember leaves behind
            // everything a previous, crashed run created — which is exactly the
            // state cleanup exists to resolve. Listing by prefix is the only view
            // that includes work this process never saw.
            await foreach (var name in _vault
                .ListNamesAsync(ExpeditionNaming.StationPrefix(stationId), cancellationToken)
                .ConfigureAwait(false))
            {
                if (await _vault.DeleteIfExistsAsync(name, cancellationToken).ConfigureAwait(false))
                {
                    reports++;
                }
            }

            // The rows are read into a list first: deleting from a partition while
            // paging through the same partition is a well-known way to skip rows
            // and then report a clean teardown that is not clean.
            var stationRows = new List<StationState>();
            await foreach (var row in _registry.QueryStationAsync(stationId, cancellationToken).ConfigureAwait(false))
            {
                stationRows.Add(row);
            }

            foreach (var row in stationRows)
            {
                if (await _registry.DeleteAsync(stationId, row.RowKey, cancellationToken).ConfigureAwait(false))
                {
                    rows++;
                }
            }

            var ids = new List<string>();
            string? continuation = null;
            do
            {
                var page = await _projection
                    .QueryStationAsync(stationId, pageSize, continuation, cancellationToken)
                    .ConfigureAwait(false);

                ids.AddRange(page.Entries.Select(entry => entry.Id));
                continuation = page.ContinuationToken;
            }
            while (continuation is not null);

            foreach (var id in ids)
            {
                if (await _projection.DeleteAsync(stationId, id, cancellationToken).ConfigureAwait(false))
                {
                    entries++;
                }
            }
        }

        // The checkpoints are shared by every station, so they are cleared once,
        // after the stations they describe are gone. Clearing them first would
        // let a concurrent processor re-read the stream from the beginning and
        // recreate what this pass is removing.
        var checkpointsRemoved = await _checkpoints.ClearAsync(cancellationToken).ConfigureAwait(false);
        var remaining = await _queue.ApproximateDepthAsync(cancellationToken).ConfigureAwait(false);

        return new TeardownReport(reports, checkpointsRemoved, rows, entries, remaining);
    }
}
