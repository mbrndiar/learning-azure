namespace LearningAzure.Exercises.BlobStorage;

/// <summary>How a transfer moves bytes between the process and the service.</summary>
public enum TransferMode
{
    /// <summary>The whole payload is held in memory before the first byte is sent.</summary>
    Buffered,

    /// <summary>Bytes are staged in bounded blocks as they are read.</summary>
    Streamed,
}

/// <summary>One staged block of an artifact upload.</summary>
/// <param name="BlockId">Ordered, fixed-length, Base64 block identifier.</param>
/// <param name="Length">Number of bytes in the block.</param>
public sealed record StagedBlock(string BlockId, int Length);

/// <summary>An artifact as the catalog lists it.</summary>
/// <param name="Name">Full blob name, including its virtual directory prefix.</param>
/// <param name="Length">Size in bytes.</param>
public sealed record ArtifactListing(string Name, long Length);

/// <summary>One page of a listing, plus the token that fetches the next one.</summary>
/// <param name="Items">The artifacts on this page.</param>
/// <param name="ContinuationToken">Token for the next page, or <c>null</c> at the end.</param>
public sealed record ArtifactPage(IReadOnlyList<ArtifactListing> Items, string? ContinuationToken);

/// <summary>
/// The upload seam. An adapter over <c>BlockBlobClient</c> implements it; the
/// evaluator implements it with a recorder.
/// </summary>
public interface IArtifactTransport
{
    /// <summary>Stages one block without committing it.</summary>
    /// <param name="blockId">Ordered, fixed-length, Base64 block identifier.</param>
    /// <param name="block">The bytes of this block.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    Task StageBlockAsync(string blockId, ReadOnlyMemory<byte> block, CancellationToken cancellationToken);

    /// <summary>Commits the staged blocks, in order, as the blob's content.</summary>
    /// <param name="blockIds">Every staged block id, in the order they must be assembled.</param>
    /// <param name="metadata">Blob metadata to apply with the commit.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    Task CommitAsync(
        IReadOnlyList<string> blockIds,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);
}

/// <summary>The listing seam: one call, one page, one continuation token.</summary>
public interface IArtifactPageSource
{
    /// <summary>Fetches one page of artifacts.</summary>
    /// <param name="prefix">Virtual-directory prefix to scan.</param>
    /// <param name="continuationToken">Token from the previous page, or <c>null</c> for the first.</param>
    /// <param name="pageSize">Maximum number of artifacts on the page.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>One page, and the token for the next.</returns>
    Task<ArtifactPage> GetPageAsync(
        string prefix,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken);
}
