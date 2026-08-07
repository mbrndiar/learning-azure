namespace LearningAzure.Exercises.EventHubsModel;

/// <summary>One sensor reading published by an expedition field station.</summary>
/// <param name="StationId">The station that produced the reading.</param>
/// <param name="ObservedAt">When the reading was taken.</param>
/// <param name="TemperatureC">The measured temperature in degrees Celsius.</param>
/// <param name="BodyBytes">The size of the serialized body, in bytes.</param>
public sealed record TelemetryReading(
    string StationId,
    DateTimeOffset ObservedAt,
    double TemperatureC,
    int BodyBytes);

/// <summary>
/// The batch abstraction the exercise packs into: the same three members the
/// SDK's <c>EventDataBatch</c> exposes, behind an application-owned port.
/// </summary>
/// <remarks>
/// A batch carries at most one partition key for every event inside it, which
/// is why <see cref="PartitionKey"/> belongs to the batch and not to the event.
/// </remarks>
public interface IEventBatch
{
    /// <summary>The partition key every event in this batch is sent under.</summary>
    string? PartitionKey { get; }

    /// <summary>How many events the batch currently holds.</summary>
    int Count { get; }

    /// <summary>The size of the batch on the wire, in bytes.</summary>
    long SizeInBytes { get; }

    /// <summary>The largest size this batch may reach, in bytes.</summary>
    long MaximumSizeInBytes { get; }

    /// <summary>Attempts to add one event.</summary>
    /// <param name="bodyBytes">The serialized body size, in bytes.</param>
    /// <returns>
    /// <c>true</c> when the event was added; <c>false</c> when it did not fit.
    /// This method does not throw and does not send.
    /// </returns>
    bool TryAdd(int bodyBytes);
}

/// <summary>Creates an empty batch bound to one partition key.</summary>
/// <param name="partitionKey">The key, or <c>null</c> for a keyless batch.</param>
/// <returns>The new, empty batch.</returns>
public delegate IEventBatch EventBatchFactory(string? partitionKey);

/// <summary>How a set of partition keys spread over a hub's partitions.</summary>
/// <param name="PartitionCount">The hub's fixed partition count.</param>
/// <param name="KeyCount">How many distinct keys were placed.</param>
/// <param name="KeysPerPartition">Keys landing on each partition, by index.</param>
public sealed record PartitionSkew(
    int PartitionCount,
    int KeyCount,
    IReadOnlyList<int> KeysPerPartition)
{
    /// <summary>How many partitions received no key at all.</summary>
    public int EmptyPartitions => KeysPerPartition.Count(count => count == 0);

    /// <summary>The busiest partition's share of keys.</summary>
    public int BusiestPartition => KeysPerPartition.Count == 0 ? 0 : KeysPerPartition.Max();

    /// <summary>
    /// The ratio between the busiest partition and a perfectly even share.
    /// A value of 1.0 is perfectly even; 2.0 means one partition carries twice
    /// its share and will saturate at half the hub's nominal capacity.
    /// </summary>
    public double SkewFactor =>
        KeyCount == 0 || PartitionCount == 0
            ? 1.0
            : BusiestPartition / (KeyCount / (double)PartitionCount);
}

/// <summary>The characteristics of a workload that has to be dispatched somehow.</summary>
/// <param name="Name">A label used in diagnostics.</param>
/// <param name="RequiresPerKeyOrdering">Events for one entity must be read in send order.</param>
/// <param name="RequiresReplay">The same data must be readable again later.</param>
/// <param name="IndependentReaderCount">How many unrelated readers need all of the data.</param>
/// <param name="RequiresPerItemAcknowledgement">Each item is completed or retried on its own.</param>
/// <param name="ItemDurationSpread">How unevenly long individual items take to handle.</param>
public sealed record WorkloadRequirement(
    string Name,
    bool RequiresPerKeyOrdering,
    bool RequiresReplay,
    int IndependentReaderCount,
    bool RequiresPerItemAcknowledgement,
    WorkDurationSpread ItemDurationSpread);

/// <summary>How evenly the handling cost of individual work items is distributed.</summary>
public enum WorkDurationSpread
{
    /// <summary>Every item costs roughly the same.</summary>
    Uniform,

    /// <summary>Some items cost orders of magnitude more than others.</summary>
    Wide,
}

/// <summary>The dispatch primitive a workload should use.</summary>
public enum DispatchPrimitive
{
    /// <summary>A partitioned, replayable event stream: Event Hubs.</summary>
    EventStream,

    /// <summary>A competing-consumer work queue: Queue Storage.</summary>
    WorkQueue,
}

/// <summary>A dispatch decision with the reason that produced it.</summary>
/// <param name="Primitive">What the workload should use.</param>
/// <param name="Reason">The single characteristic that decided it.</param>
public sealed record DispatchChoice(DispatchPrimitive Primitive, string Reason);

/// <summary>The measured shape of an ingest workload.</summary>
/// <param name="EventsPerSecond">Sustained peak event rate.</param>
/// <param name="AverageEventBytes">Average serialized event size.</param>
/// <param name="IndependentReaderCount">Consumer groups reading the whole stream.</param>
/// <param name="ConcurrentProcessorCount">Processor instances that must each own work.</param>
public sealed record IngestProfile(
    int EventsPerSecond,
    int AverageEventBytes,
    int IndependentReaderCount,
    int ConcurrentProcessorCount);

/// <summary>A sizing recommendation derived from an <see cref="IngestProfile"/>.</summary>
/// <param name="ThroughputUnits">Standard-tier throughput units required.</param>
/// <param name="Partitions">Minimum partition count.</param>
/// <param name="LimitedBy">Which limit set the throughput-unit number.</param>
public sealed record CapacityPlan(int ThroughputUnits, int Partitions, string LimitedBy);

/// <summary>A change somebody wants to make to an existing hub.</summary>
public enum HubChange
{
    /// <summary>Raise or lower the namespace's throughput units.</summary>
    ChangeThroughputUnits,

    /// <summary>Change how long events are retained.</summary>
    ChangeRetention,

    /// <summary>Add another consumer group to the hub.</summary>
    AddConsumerGroup,

    /// <summary>Increase the hub's partition count.</summary>
    IncreasePartitionCount,

    /// <summary>Decrease the hub's partition count.</summary>
    DecreasePartitionCount,
}

/// <summary>The Event Hubs tier a namespace runs on.</summary>
public enum EventHubsTier
{
    /// <summary>Basic: one consumer group, one day of retention.</summary>
    Basic,

    /// <summary>Standard: 20 consumer groups, up to seven days of retention.</summary>
    Standard,

    /// <summary>Premium: partition count may be increased after creation.</summary>
    Premium,
}

/// <summary>Whether a change can be made in place, and what it costs if not.</summary>
/// <param name="AllowedInPlace">Whether the running hub can absorb the change.</param>
/// <param name="Consequence">What happens, or what has to happen instead.</param>
public sealed record ChangeVerdict(bool AllowedInPlace, string Consequence);
