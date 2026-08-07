using System.Text;

namespace LearningAzure.Exercises.BlobStorage.Tests;

/// <summary>Verifies that uploads stream, stay bounded, and never publish a partial artifact.</summary>
public sealed class BlockStreamingUploaderTests
{
    private const int BlockSize = 1024;

    private static readonly Dictionary<string, string> Metadata = new(StringComparer.Ordinal)
    {
        ["station"] = "station-bravo",
        ["caption"] = "ice shelf calving",
    };

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private static async Task<(RecordingTransport Transport, ProbeStream Source, IReadOnlyList<StagedBlock> Blocks)>
        UploadAsync(int length, CancellationToken cancellationToken, int blockSize = BlockSize)
    {
        var source = new ProbeStream(Payload(length));
        var transport = new RecordingTransport(source);
        var blocks = await BlockStreamingUploader
            .UploadAsync(transport, source, Metadata, blockSize, cancellationToken)
            .ConfigureAwait(true);
        return (transport, source, blocks);
    }

    [Fact]
    public async Task TheFirstBlockIsStagedBeforeTheSourceIsFullyRead()
    {
        // This is the whole module in one assertion. An implementation that reads
        // the source into memory and then uploads has consumed every byte by the
        // time it makes its first service call.
        var (transport, _, _) = await UploadAsync(BlockSize * 8, TestContext.Current.CancellationToken);

        Assert.Equal(BlockSize, transport.Staged[0].ConsumedAtCall);
    }

    [Fact]
    public async Task EachStageOnlyEverReadsOneMoreBlock()
    {
        var (transport, _, _) = await UploadAsync(BlockSize * 8, TestContext.Current.CancellationToken);

        for (var index = 0; index < transport.Staged.Count; index++)
        {
            Assert.Equal(BlockSize * (index + 1), transport.Staged[index].ConsumedAtCall);
        }
    }

    [Fact]
    public async Task NoBlockExceedsTheBlockSize()
    {
        var (transport, _, _) = await UploadAsync((BlockSize * 5) + 17, TestContext.Current.CancellationToken);

        Assert.All(transport.Staged, staged => Assert.True(staged.Length <= BlockSize, $"{staged.Length}"));
    }

    [Fact]
    public async Task NoReadEverAsksForMoreThanOneBlock()
    {
        var (_, source, _) = await UploadAsync(BlockSize * 8, TestContext.Current.CancellationToken);

        Assert.True(
            source.LargestRead <= BlockSize,
            $"A read asked for {source.LargestRead} bytes with a block size of {BlockSize}.");
    }

    [Fact]
    public async Task ThePayloadIsSplitIntoTheExpectedNumberOfBlocks()
    {
        var (_, _, blocks) = await UploadAsync((BlockSize * 5) + 17, TestContext.Current.CancellationToken);

        Assert.Equal(6, blocks.Count);
    }

    [Fact]
    public async Task TheFinalShortBlockIsStaged()
    {
        var (_, _, blocks) = await UploadAsync((BlockSize * 5) + 17, TestContext.Current.CancellationToken);

        Assert.Equal(17, blocks[^1].Length);
    }

    [Fact]
    public async Task EveryByteIsAccountedFor()
    {
        const int length = (BlockSize * 3) + 5;
        var (_, _, blocks) = await UploadAsync(length, TestContext.Current.CancellationToken);

        Assert.Equal(length, blocks.Sum(block => block.Length));
    }

    [Fact]
    public async Task AnEmptySourceStagesNothingAndStillCommits()
    {
        var (transport, _, blocks) = await UploadAsync(0, TestContext.Current.CancellationToken);

        Assert.Empty(blocks);
        Assert.Equal(1, transport.CommitCount);
        Assert.Empty(transport.CommittedBlockIds!);
    }

    [Fact]
    public async Task TheUploadCommitsExactlyOnce()
    {
        var (transport, _, _) = await UploadAsync(BlockSize * 4, TestContext.Current.CancellationToken);

        Assert.Equal(1, transport.CommitCount);
    }

    [Fact]
    public async Task TheCommitListsEveryBlockInOrder()
    {
        var (transport, _, blocks) = await UploadAsync(BlockSize * 4, TestContext.Current.CancellationToken);

        Assert.Equal(blocks.Select(block => block.BlockId), transport.CommittedBlockIds!);
    }

    [Fact]
    public async Task MetadataIsAppliedWithTheCommit()
    {
        var (transport, _, _) = await UploadAsync(BlockSize * 2, TestContext.Current.CancellationToken);

        Assert.Equal("station-bravo", transport.CommittedMetadata!["station"]);
    }

    [Fact]
    public async Task ACancellationBetweenBlocksPublishesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ProbeStream(Payload(BlockSize * 8));
        var transport = new RecordingTransport(source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var upload = BlockStreamingUploader.UploadAsync(
                new CancellingTransport(transport, cancellation, afterBlocks: 2),
                source,
                Metadata,
                BlockSize,
                cancellation.Token);
            await upload;
        });

        // A committed blob assembled from a partial block list is a silently
        // truncated artifact, which is worse than no artifact at all.
        Assert.Equal(0, transport.CommitCount);
    }

    [Fact]
    public async Task AStagingFailurePublishesNothing()
    {
        var source = new ProbeStream(Payload(BlockSize * 8));
        var transport = new RecordingTransport(source) { FailAfterBlock = 2 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BlockStreamingUploader.UploadAsync(
                transport,
                source,
                Metadata,
                BlockSize,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, transport.CommitCount);
    }

    [Fact]
    public void BlockIdsAreAllTheSameDecodedLength()
    {
        var lengths = Enumerable.Range(0, 200)
            .Select(index => Encoding.UTF8.GetString(Convert.FromBase64String(BlockStreamingUploader.BlockId(index))).Length)
            .Distinct()
            .ToArray();

        // Mixed-length block ids are rejected with InvalidBlockList at commit
        // time, after every byte has already been uploaded.
        Assert.Single(lengths);
    }

    [Fact]
    public void BlockIdsSortIntoCommitOrder()
    {
        var ids = Enumerable.Range(0, 200).Select(BlockStreamingUploader.BlockId).ToArray();

        Assert.Equal<IEnumerable<string>>(ids, [.. ids.OrderBy(id => id, StringComparer.Ordinal)]);
    }

    [Fact]
    public void BlockIdsAreValidBase64()
    {
        var id = BlockStreamingUploader.BlockId(7);

        Assert.Equal(id, Convert.ToBase64String(Convert.FromBase64String(id)));
    }

    [Fact]
    public void BlockIdsAreDistinct()
    {
        var ids = Enumerable.Range(0, 500).Select(BlockStreamingUploader.BlockId).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ANullTransportIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => BlockStreamingUploader.UploadAsync(
                null!,
                new ProbeStream([]),
                Metadata,
                BlockSize,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANonPositiveBlockSizeIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BlockStreamingUploader.UploadAsync(
                new RecordingTransport(),
                new ProbeStream([]),
                Metadata,
                0,
                TestContext.Current.CancellationToken));
    }

    private sealed class CancellingTransport(
        RecordingTransport inner,
        CancellationTokenSource cancellation,
        int afterBlocks) : IArtifactTransport
    {
        private int _staged;

        public async Task StageBlockAsync(
            string blockId,
            ReadOnlyMemory<byte> block,
            CancellationToken cancellationToken)
        {
            await inner.StageBlockAsync(blockId, block, cancellationToken).ConfigureAwait(false);
            if (++_staged >= afterBlocks)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        public Task CommitAsync(
            IReadOnlyList<string> blockIds,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken) =>
            inner.CommitAsync(blockIds, metadata, cancellationToken);
    }
}
