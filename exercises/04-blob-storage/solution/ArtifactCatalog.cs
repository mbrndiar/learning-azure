using System.Runtime.CompilerServices;

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
        CancellationToken cancellationToken)
    {
        // Validation lives here rather than in the iterator: an async iterator
        // does not run a single statement until it is enumerated, so a guard
        // clause inside it would throw at the wrong time, or never.
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return Enumerate(source, prefix, pageSize, cancellationToken);
    }

    /// <summary>Counts the service calls a full enumeration of a prefix will cost.</summary>
    /// <param name="totalArtifacts">Artifacts under the prefix.</param>
    /// <param name="pageSize">Artifacts per service call.</param>
    /// <returns>The number of list requests, which is what Azure bills.</returns>
    public static int RequestCount(int totalArtifacts, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalArtifacts);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        // An empty prefix still costs one request: the service has to be asked
        // before anyone knows there is nothing there.
        return totalArtifacts == 0 ? 1 : (totalArtifacts + pageSize - 1) / pageSize;
    }

    private static async IAsyncEnumerable<ArtifactListing> Enumerate(
        IArtifactPageSource source,
        string prefix,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuationToken = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await source
                .GetPageAsync(prefix, continuationToken, pageSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in page.Items)
            {
                // Yielding item by item is what makes 'take the first ten' cost
                // one service call instead of a full scan: the next page is not
                // requested until the consumer asks for an item beyond this one.
                yield return item;
            }

            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);
    }
}
