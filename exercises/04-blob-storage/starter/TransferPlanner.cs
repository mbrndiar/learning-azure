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
    public static TransferMode Choose(long artifactBytes, long memoryBudgetBytes) =>
        // GAP 9 — Apply the rules in order; the first match wins.
        //
        //   1. artifactBytes < 0 (unknown length)        -> Streamed
        //      A stream from a network or a pipe has no length. "Buffer it and
        //      see" is how a service is killed by one unexpectedly large upload.
        //   2. artifactBytes > memoryBudgetBytes         -> Streamed
        //   3. artifactBytes <= SmallArtifactBytes       -> Buffered
        //      Small payloads are one request buffered and several streamed, so
        //      buffering is genuinely cheaper here.
        //   4. otherwise                                 -> Streamed
        //
        // Reject a memoryBudgetBytes below 1.
        throw new NotImplementedException(
            "GAP 9: implement TransferPlanner.Choose. See "
            + "lessons/04-blob-storage/README.md#streaming-is-a-memory-decision.");

    /// <summary>Peak bytes one transfer holds in memory.</summary>
    /// <param name="mode">The chosen mode.</param>
    /// <param name="artifactBytes">Size of the artifact.</param>
    /// <param name="blockSize">Block size used when streaming.</param>
    /// <returns>Peak resident bytes for the transfer.</returns>
    public static long PeakMemoryBytes(TransferMode mode, long artifactBytes, int blockSize) =>
        // GAP 10 — Buffered costs artifactBytes; Streamed costs blockSize, or
        // artifactBytes when the artifact is smaller than one block.
        //
        // This is the number that makes the trade concrete: a 4 GiB capture costs
        // 4 GiB buffered and 4 MiB streamed, and the difference is the whole
        // module.
        throw new NotImplementedException(
            "GAP 10: implement TransferPlanner.PeakMemoryBytes. See "
            + "lessons/04-blob-storage/README.md#streaming-is-a-memory-decision.");
}
