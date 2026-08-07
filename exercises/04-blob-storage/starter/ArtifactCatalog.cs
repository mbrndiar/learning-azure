namespace LearningAzure.Exercises.BlobStorage;

/// <summary>Lists artifacts one page at a time, lazily.</summary>
public static class ArtifactCatalog
{
    /// <summary>Streams every artifact under a prefix, fetching pages only as needed.</summary>
    /// <param name="source">The listing seam.</param>
    /// <param name="prefix">Virtual-directory prefix to scan.</param>
    /// <param name="pageSize">Artifacts per service call.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>Every artifact under the prefix, in service order.</returns>
    public static IAsyncEnumerable<ArtifactListing> ListAsync(
        IArtifactPageSource source,
        string prefix,
        int pageSize,
        CancellationToken cancellationToken) =>
        // GAP 7 — Return an IAsyncEnumerable that fetches lazily.
        //
        //   * Fetch the first page, yield its items one at a time, then follow
        //     the continuation token; stop when the token is null.
        //   * Do NOT collect the pages into a List and return it. A caller that
        //     wants the first ten artifacts of a million-artifact container must
        //     cost one service call, not a hundred thousand — and the evaluator
        //     counts calls to prove it.
        //   * Use [EnumeratorCancellation] on the token parameter of the iterator
        //     so 'await foreach (... .WithCancellation(token))' actually cancels.
        //   * Pass the token to every GetPageAsync call. A cancellation between
        //     pages must stop the enumeration, not finish it quietly.
        //
        // A guard clause that throws (null source, blank prefix, pageSize < 1)
        // cannot live in an async iterator, because iterators do not run until
        // enumerated. Split the method: validate eagerly, then delegate to a
        // private iterator.
        throw new NotImplementedException(
            "GAP 7: implement ArtifactCatalog.ListAsync. See "
            + "lessons/04-blob-storage/README.md#listing-is-paged-and-lazy.");

    /// <summary>Counts the service calls a full enumeration of a prefix will cost.</summary>
    /// <param name="totalArtifacts">Artifacts under the prefix.</param>
    /// <param name="pageSize">Artifacts per service call.</param>
    /// <returns>The number of list requests, which is what Azure bills.</returns>
    public static int RequestCount(int totalArtifacts, int pageSize) =>
        // GAP 8 — ceil(totalArtifacts / pageSize), and exactly 1 for an empty
        // prefix: the service still has to be asked before anyone knows it is
        // empty. Reject a pageSize below 1 and a negative count.
        throw new NotImplementedException(
            "GAP 8: implement ArtifactCatalog.RequestCount. See "
            + "lessons/04-blob-storage/README.md#listing-is-paged-and-lazy.");
}
