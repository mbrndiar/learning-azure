namespace LearningAzure.Exercises.BlobStorage.Tests;

/// <summary>Verifies the buffered-versus-streamed decision and its memory cost.</summary>
public sealed class TransferPlannerTests
{
    private const long Budget = 64 * 1024 * 1024;

    [Fact]
    public void AnUnknownLengthIsAlwaysStreamed()
    {
        // "Buffer it and see" is how a service is killed by one unexpectedly
        // large upload from a pipe or a network stream.
        Assert.Equal(TransferMode.Streamed, TransferPlanner.Choose(-1, Budget));
    }

    [Fact]
    public void AnArtifactLargerThanTheBudgetIsStreamed()
    {
        Assert.Equal(TransferMode.Streamed, TransferPlanner.Choose(Budget + 1, Budget));
    }

    [Fact]
    public void ASmallArtifactIsBuffered()
    {
        Assert.Equal(TransferMode.Buffered, TransferPlanner.Choose(64 * 1024, Budget));
    }

    [Fact]
    public void TheSmallArtifactBoundaryIsInclusive()
    {
        Assert.Equal(
            TransferMode.Buffered,
            TransferPlanner.Choose(TransferPlanner.SmallArtifactBytes, Budget));
        Assert.Equal(
            TransferMode.Streamed,
            TransferPlanner.Choose(TransferPlanner.SmallArtifactBytes + 1, Budget));
    }

    [Fact]
    public void AMidSizedArtifactInsideTheBudgetIsStillStreamed()
    {
        // Fitting in the budget is not a reason to spend it: a 32 MiB buffer per
        // concurrent upload is what turns twenty uploads into an OutOfMemoryException.
        Assert.Equal(TransferMode.Streamed, TransferPlanner.Choose(32 * 1024 * 1024, Budget));
    }

    [Fact]
    public void AnEmptyArtifactIsBuffered()
    {
        Assert.Equal(TransferMode.Buffered, TransferPlanner.Choose(0, Budget));
    }

    [Fact]
    public void ANonPositiveBudgetIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferPlanner.Choose(1024, 0));
    }

    [Fact]
    public void BufferedCostsTheWholeArtifact()
    {
        Assert.Equal(
            4L * 1024 * 1024 * 1024,
            TransferPlanner.PeakMemoryBytes(TransferMode.Buffered, 4L * 1024 * 1024 * 1024, BlockStreamingUploader.DefaultBlockSize));
    }

    [Fact]
    public void StreamedCostsOneBlock()
    {
        Assert.Equal(
            BlockStreamingUploader.DefaultBlockSize,
            TransferPlanner.PeakMemoryBytes(TransferMode.Streamed, 4L * 1024 * 1024 * 1024, BlockStreamingUploader.DefaultBlockSize));
    }

    [Fact]
    public void StreamingASmallArtifactCostsOnlyTheArtifact()
    {
        Assert.Equal(1024, TransferPlanner.PeakMemoryBytes(TransferMode.Streamed, 1024, 4 * 1024 * 1024));
    }

    [Fact]
    public void StreamingIsAThousandFoldMemorySavingOnALargeCapture()
    {
        const long capture = 4L * 1024 * 1024 * 1024;
        var buffered = TransferPlanner.PeakMemoryBytes(TransferMode.Buffered, capture, BlockStreamingUploader.DefaultBlockSize);
        var streamed = TransferPlanner.PeakMemoryBytes(TransferMode.Streamed, capture, BlockStreamingUploader.DefaultBlockSize);

        Assert.True(buffered / streamed >= 1000, $"{buffered} / {streamed}");
    }

    [Fact]
    public void PeakMemoryRejectsANegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransferPlanner.PeakMemoryBytes(TransferMode.Streamed, -1, 4096));
    }

    [Fact]
    public void PeakMemoryRejectsANonPositiveBlockSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransferPlanner.PeakMemoryBytes(TransferMode.Streamed, 1024, 0));
    }

    [Fact]
    public void EveryChosenModeFitsInsideTheBudget()
    {
        long[] sizes = [0, 1024, 256 * 1024, 1024 * 1024, Budget, Budget + 1, 4L * 1024 * 1024 * 1024];

        foreach (var size in sizes)
        {
            var mode = TransferPlanner.Choose(size, Budget);
            var peak = TransferPlanner.PeakMemoryBytes(mode, size, BlockStreamingUploader.DefaultBlockSize);
            Assert.True(peak <= Budget, $"{size} bytes as {mode} peaks at {peak}, over the {Budget} budget.");
        }
    }
}
