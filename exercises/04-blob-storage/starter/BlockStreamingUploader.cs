namespace LearningAzure.Exercises.BlobStorage;

/// <summary>Uploads an artifact as bounded blocks, without ever holding it whole.</summary>
/// <remarks>
/// A 4 GiB expedition capture read into a byte array is 4 GiB of managed heap and
/// an <see cref="OutOfMemoryException"/> waiting for the second concurrent
/// upload. Streaming turns the memory cost into a constant.
/// </remarks>
public static class BlockStreamingUploader
{
    /// <summary>The block size the course uses: large enough to be efficient, small enough to bound memory.</summary>
    public const int DefaultBlockSize = 4 * 1024 * 1024;

    /// <summary>Formats the Base64 block id for block <paramref name="index"/>.</summary>
    /// <param name="index">Zero-based block index.</param>
    /// <returns>A fixed-length, Base64-encoded, lexicographically ordered id.</returns>
    public static string BlockId(int index) =>
        // GAP 5 — Every block id of one blob must be the SAME LENGTH once
        // decoded, and they must sort in commit order. Format the index as a
        // zero-padded decimal string of width 8 and Base64-encode its UTF-8
        // bytes.
        //
        // Ids of different lengths are rejected by the service with
        // InvalidBlockList, and the failure surfaces at commit time — after every
        // byte has already been uploaded.
        throw new NotImplementedException(
            "GAP 5: implement BlockStreamingUploader.BlockId. See "
            + "lessons/04-blob-storage/README.md#streaming-is-a-memory-decision.");

    /// <summary>Streams <paramref name="source"/> to <paramref name="transport"/> in blocks.</summary>
    /// <param name="transport">The staging and commit seam.</param>
    /// <param name="source">The artifact's bytes; may be non-seekable and of unknown length.</param>
    /// <param name="metadata">Metadata to apply at commit time.</param>
    /// <param name="blockSize">Maximum bytes per staged block.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The blocks that were staged, in commit order.</returns>
    public static Task<IReadOnlyList<StagedBlock>> UploadAsync(
        IArtifactTransport transport,
        Stream source,
        IReadOnlyDictionary<string, string> metadata,
        int blockSize,
        CancellationToken cancellationToken) =>
        // GAP 6 — Stream, do not buffer.
        //
        //   * Rent a buffer of blockSize from ArrayPool<byte>.Shared and return
        //     it in a finally.
        //   * Fill the buffer with ReadAtLeastAsync (or a read loop), stage the
        //     block as soon as it is full, and continue. The FIRST StageBlockAsync
        //     call must happen after reading blockSize bytes, not after reading
        //     the whole source: the evaluator records how much of the source had
        //     been consumed at the moment of each stage call, so
        //     'source.CopyToAsync(memoryStream)' followed by staging is detected
        //     and rejected.
        //   * Never call source.Length or source.Position. A network stream has
        //     neither, and an implementation that needs them cannot upload the
        //     thing this module exists to upload.
        //   * Stage a final short block when the source ends mid-buffer, and
        //     stage nothing at all for an empty source.
        //   * Commit ONCE, with every block id in order, and pass metadata.
        //   * Pass cancellationToken to every await. A cancellation between
        //     blocks must abandon the upload with no commit — a committed blob
        //     assembled from a partial block list is a silently truncated
        //     artifact.
        throw new NotImplementedException(
            "GAP 6: implement BlockStreamingUploader.UploadAsync. See "
            + "lessons/04-blob-storage/README.md#streaming-is-a-memory-decision.");
}
