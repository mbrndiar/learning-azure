namespace LearningAzure.Exercises.BlobStorage;

/// <summary>Decides how a transfer should move bytes, and what it will cost.</summary>
public static class TransferPlanner
{
    /// <summary>Artifacts at or below this size are cheap to hold whole.</summary>
    public const long SmallArtifactBytes = 256 * 1024;

    /// <summary>Chooses a transfer mode for one artifact.</summary>
    /// <param name="artifactBytes">Size of the artifact, or -1 when unknown.</param>
    /// <param name="memoryBudgetBytes">Bytes this process may spend on one transfer.</param>
    /// <returns>The mode that fits inside the budget.</returns>
    public static TransferMode Choose(long artifactBytes, long memoryBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBudgetBytes, 1);

        // An unknown length is the dangerous case, not the harmless one: "buffer
        // it and see" is how a service is killed by one unexpectedly large upload.
        if (artifactBytes < 0)
        {
            return TransferMode.Streamed;
        }

        if (artifactBytes > memoryBudgetBytes)
        {
            return TransferMode.Streamed;
        }

        // Below this size a buffered upload is a single request, while a streamed
        // one is stage plus commit, so buffering is genuinely cheaper.
        return artifactBytes <= SmallArtifactBytes ? TransferMode.Buffered : TransferMode.Streamed;
    }

    /// <summary>Peak bytes one transfer holds in memory.</summary>
    /// <param name="mode">The chosen mode.</param>
    /// <param name="artifactBytes">Size of the artifact.</param>
    /// <param name="blockSize">Block size used when streaming.</param>
    /// <returns>Peak resident bytes for the transfer.</returns>
    public static long PeakMemoryBytes(TransferMode mode, long artifactBytes, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(artifactBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);

        return mode switch
        {
            TransferMode.Buffered => artifactBytes,
            TransferMode.Streamed => Math.Min(artifactBytes, blockSize),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transfer mode."),
        };
    }
}
