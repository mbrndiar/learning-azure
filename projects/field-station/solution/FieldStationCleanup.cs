namespace LearningAzure.Projects.FieldStation;

/// <summary>What a cleanup pass removed, and what it could not.</summary>
/// <param name="ArtifactsDeleted">Artifacts removed from the store.</param>
/// <param name="StatusRowsDeleted">Status rows removed from the index, including the summary row.</param>
/// <param name="MessagesRemaining">Approximate queue depth after the pass.</param>
public sealed record CleanupReport(int ArtifactsDeleted, int StatusRowsDeleted, int MessagesRemaining)
{
    /// <summary>True when nothing the run created is left behind.</summary>
    public bool IsComplete => MessagesRemaining == 0;
}

/// <summary>Removes everything one station's run created.</summary>
/// <remarks>
/// <para>
/// Milestone 5, second half. Cleanup is part of the exercise rather than an
/// afterthought: a course that leaves resources behind teaches a habit that costs
/// money in a real subscription, and a cleanup nobody verifies is indistinguishable
/// from no cleanup at all.
/// </para>
/// <para>
/// The pass therefore reports what it removed and what is still there, and the
/// caller is expected to fail on a non-empty report rather than log it.
/// </para>
/// </remarks>
/// <param name="store">The artifact store to clear.</param>
/// <param name="index">The status index to clear.</param>
/// <param name="queue">The queue whose residual depth is checked.</param>
public sealed class FieldStationCleanup(IArtifactStore store, IStationStatusIndex index, IWorkBacklog queue)
{
    /// <summary>The artifact store this cleanup clears.</summary>
    public IArtifactStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The status index this cleanup clears.</summary>
    public IStationStatusIndex Index { get; } = index ?? throw new ArgumentNullException(nameof(index));

    /// <summary>The queue whose residual depth this cleanup reports.</summary>
    public IWorkBacklog Queue { get; } = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>Removes every artifact and status row belonging to one station.</summary>
    /// <param name="stationId">The station to remove.</param>
    /// <param name="cancellationToken">Cancels the pass between deletes.</param>
    /// <returns>What was removed, and whether anything was left.</returns>
    public async Task<CleanupReport> RemoveStationAsync(string stationId, CancellationToken cancellationToken)
    {
        var artifacts = 0;
        var rows = 0;

        // GAP 13 — Enumerate, then delete; never assume the names.
        //
        // Deleting only the artifacts this process happens to remember leaves
        // behind everything a previous, crashed run created — which is exactly
        // the state cleanup exists to resolve. Listing by prefix is the only
        // view that includes work this process never saw.
        await foreach (var name in Store.ListNamesAsync(StationNaming.StationPrefix(stationId), cancellationToken)
            .ConfigureAwait(false))
        {
            if (await Store.DeleteIfExistsAsync(name, cancellationToken).ConfigureAwait(false))
            {
                artifacts++;
            }
        }

        // The status rows are read into a list first: deleting from a partition
        // while paging through the same partition is a well-known way to skip
        // rows and then report a clean teardown that is not clean.
        var statusRows = new List<StationStatus>();
        await foreach (var row in Index.QueryStationAsync(stationId, cancellationToken).ConfigureAwait(false))
        {
            statusRows.Add(row);
        }

        foreach (var row in statusRows)
        {
            if (await Index.DeleteAsync(stationId, row.RowKey, cancellationToken).ConfigureAwait(false))
            {
                rows++;
            }
        }

        var remaining = await Queue.ApproximateDepthAsync(cancellationToken).ConfigureAwait(false);
        return new CleanupReport(artifacts, rows, remaining);
    }
}
