namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>An event as it arrives at a handler.</summary>
/// <param name="PartitionId">The partition it was read from.</param>
/// <param name="SequenceNumber">Its position within that partition. Unique and increasing per partition, and meaningless across partitions.</param>
/// <param name="Body">The payload. Opaque to everything in this exercise.</param>
public sealed record HandledEvent(string PartitionId, long SequenceNumber, string Body);

/// <summary>Where a processor should start reading a partition.</summary>
/// <param name="SequenceNumber">The recorded sequence number, or -1 when there is none.</param>
/// <param name="IsInclusive">Whether the event at <paramref name="SequenceNumber"/> is delivered again.</param>
/// <param name="IsFromStart">Whether the partition has no recorded position at all.</param>
public sealed record ResumePosition(long SequenceNumber, bool IsInclusive, bool IsFromStart);

/// <summary>Why a checkpoint is being written — or why it is not.</summary>
public enum CheckpointReason
{
    /// <summary>Do not checkpoint yet.</summary>
    None = 0,

    /// <summary>Enough events have been handled since the last checkpoint.</summary>
    EventCount,

    /// <summary>Enough time has passed since the last checkpoint.</summary>
    Elapsed,

    /// <summary>The partition is being released, so the position must be recorded now.</summary>
    PartitionClosing,
}

/// <summary>The service's view of a partition.</summary>
/// <param name="PartitionId">The partition identifier.</param>
/// <param name="LastEnqueuedSequenceNumber">The highest sequence number the service holds, or -1 when the partition is empty.</param>
public sealed record PartitionSnapshot(string PartitionId, long LastEnqueuedSequenceNumber);

/// <summary>How far behind one partition is.</summary>
/// <param name="PartitionId">The partition identifier.</param>
/// <param name="CheckpointedSequenceNumber">The recorded position, or -1 when there is none.</param>
/// <param name="LastEnqueuedSequenceNumber">The highest sequence number the service holds.</param>
/// <param name="Lag">How many events sit between the two.</param>
/// <param name="HasCheckpoint">Whether the consumer group has ever recorded a position here.</param>
public sealed record PartitionLag(
    string PartitionId,
    long CheckpointedSequenceNumber,
    long LastEnqueuedSequenceNumber,
    long Lag,
    bool HasCheckpoint);

/// <summary>How far behind a consumer group is, in total and per partition.</summary>
/// <param name="Partitions">One entry per partition, ordered by partition id.</param>
/// <param name="TotalLag">The sum of the per-partition lags.</param>
/// <param name="PartitionsWithoutCheckpoint">How many partitions have no recorded position.</param>
public sealed record ConsumerLag(
    IReadOnlyList<PartitionLag> Partitions,
    long TotalLag,
    int PartitionsWithoutCheckpoint);

/// <summary>What a set of ownership observations says about a consumer deployment.</summary>
public enum OwnershipVerdict
{
    /// <summary>Every partition is owned and no processor is idle.</summary>
    Balanced = 0,

    /// <summary>There are more processors than partitions, so some can never read anything.</summary>
    IdleProcessors,

    /// <summary>At least one partition has no owner, so its events are not being read.</summary>
    UnownedPartitions,

    /// <summary>Ownership keeps moving, so processors spend their time rebalancing instead of reading.</summary>
    Thrashing,
}

/// <summary>A point-in-time view of who owns what.</summary>
/// <param name="PartitionCount">How many partitions the hub has.</param>
/// <param name="ProcessorCount">How many processor instances are running.</param>
/// <param name="OwnedPartitionsByProcessor">Partition ids owned by each processor instance.</param>
/// <param name="OwnershipChangesInLastMinute">How many times ownership moved in the last minute.</param>
public sealed record OwnershipSnapshot(
    int PartitionCount,
    int ProcessorCount,
    IReadOnlyDictionary<string, IReadOnlyList<string>> OwnedPartitionsByProcessor,
    int OwnershipChangesInLastMinute);

/// <summary>What one pump run did.</summary>
/// <param name="Applied">How many events changed the projection.</param>
/// <param name="Skipped">How many events were recognised as already applied.</param>
/// <param name="Checkpoints">How many checkpoints were written.</param>
/// <param name="Cancelled">Whether the run stopped because it was cancelled.</param>
public sealed record PumpResult(int Applied, int Skipped, int Checkpoints, bool Cancelled);
