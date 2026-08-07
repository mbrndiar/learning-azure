using System.Buffers;
using System.Globalization;
using System.Text;

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

    private const int BlockIdWidth = 8;

    /// <summary>Formats the Base64 block id for block <paramref name="index"/>.</summary>
    /// <param name="index">Zero-based block index.</param>
    /// <returns>A fixed-length, Base64-encoded, lexicographically ordered id.</returns>
    public static string BlockId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // Every block id of one blob must decode to the same length, or the
        // service rejects the commit with InvalidBlockList — after every byte has
        // already been uploaded.
        var ordinal = index.ToString(CultureInfo.InvariantCulture).PadLeft(BlockIdWidth, '0');
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(ordinal));
    }

    /// <summary>Streams <paramref name="source"/> to <paramref name="transport"/> in blocks.</summary>
    /// <param name="transport">The staging and commit seam.</param>
    /// <param name="source">The artifact's bytes; may be non-seekable and of unknown length.</param>
    /// <param name="metadata">Metadata to apply at commit time.</param>
    /// <param name="blockSize">Maximum bytes per staged block.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The blocks that were staged, in commit order.</returns>
    public static async Task<IReadOnlyList<StagedBlock>> UploadAsync(
        IArtifactTransport transport,
        Stream source,
        IReadOnlyDictionary<string, string> metadata,
        int blockSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);

        var blocks = new List<StagedBlock>();
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);

        try
        {
            while (true)
            {
                // Fill exactly one block, then stage it. The source is never read
                // ahead of what is about to be sent, which is what keeps peak
                // memory at blockSize regardless of artifact size.
                var filled = await source
                    .ReadAtLeastAsync(buffer.AsMemory(0, blockSize), blockSize, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                if (filled == 0)
                {
                    break;
                }

                var blockId = BlockId(blocks.Count);
                await transport
                    .StageBlockAsync(blockId, buffer.AsMemory(0, filled), cancellationToken)
                    .ConfigureAwait(false);
                blocks.Add(new StagedBlock(blockId, filled));

                if (filled < blockSize)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Committing is what makes the blob exist. Reaching this line means every
        // block was staged; a cancellation above abandons the upload with no
        // commit, so no truncated artifact is ever published.
        await transport
            .CommitAsync([.. blocks.Select(block => block.BlockId)], metadata, cancellationToken)
            .ConfigureAwait(false);

        return blocks;
    }
}
