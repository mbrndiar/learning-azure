namespace LearningAzure.Exercises.DataMap;

/// <summary>The five Azure data primitives a workload can be routed to.</summary>
public enum Primitive
{
    /// <summary>Azure Blob Storage — durable opaque objects.</summary>
    Blob,

    /// <summary>Azure Queue Storage — work messages handed to one consumer.</summary>
    Queue,

    /// <summary>Azure Table Storage — keyed entities in a partitioned index.</summary>
    Table,

    /// <summary>Azure Event Hubs — a retained, partitioned event stream.</summary>
    EventStream,

    /// <summary>Azure Cosmos DB for NoSQL — queryable JSON documents.</summary>
    Document,
}

/// <summary>What one durable item is, from the service's point of view.</summary>
public enum DurabilityUnit
{
    /// <summary>An opaque byte range addressed by name.</summary>
    OpaqueObject,

    /// <summary>A message that exists until a consumer deletes it.</summary>
    Message,

    /// <summary>A flat set of properties under a partition and row key.</summary>
    Entity,

    /// <summary>An append-only record inside a partition.</summary>
    Event,

    /// <summary>A JSON document with an arbitrary shape.</summary>
    Document,
}

/// <summary>The ordering guarantee a primitive actually makes.</summary>
public enum OrderingGuarantee
{
    /// <summary>No ordering is promised between items.</summary>
    None,

    /// <summary>Roughly first-in-first-out, but explicitly not guaranteed.</summary>
    BestEffortFifo,

    /// <summary>Items are sorted by their row key inside one partition.</summary>
    SortedWithinPartition,

    /// <summary>Items are strictly ordered inside one partition and unordered across partitions.</summary>
    StrictWithinPartition,
}

/// <summary>How a primitive spreads data across units of scale.</summary>
public enum PartitionModel
{
    /// <summary>No partition key; the name prefix is the only grouping.</summary>
    NamePrefixOnly,

    /// <summary>The service hands out the next visible item; the caller does not choose.</summary>
    ServiceManaged,

    /// <summary>An explicit partition key chosen by the application.</summary>
    PartitionKey,

    /// <summary>An explicit partition key, with an offset addressing each item in the partition.</summary>
    PartitionKeyWithOffsets,
}

/// <summary>Whether an item can be read again after it has been consumed.</summary>
public enum ReplayModel
{
    /// <summary>Reading never consumes; an item can be read forever.</summary>
    Unlimited,

    /// <summary>The item survives until a consumer deletes it, and reappears if it does not.</summary>
    UntilDeleted,

    /// <summary>Any consumer may re-read the item, but only inside the retention window.</summary>
    WithinRetentionWindow,
}

/// <summary>What the bill is actually a function of.</summary>
public enum CostDriver
{
    /// <summary>Stored bytes over time, plus a charge per operation.</summary>
    StoredBytesAndOperations,

    /// <summary>Operations only — including polls that return nothing.</summary>
    OperationsOnly,

    /// <summary>Capacity reserved ahead of time, whether or not it is used.</summary>
    ProvisionedThroughput,

    /// <summary>Request units consumed by each read, write, and query.</summary>
    RequestUnits,
}

/// <summary>The characteristics that decide between two adjacent primitives.</summary>
/// <param name="Unit">What one durable item is.</param>
/// <param name="Ordering">The ordering guarantee the service makes.</param>
/// <param name="Partitioning">How the primitive spreads data across units of scale.</param>
/// <param name="Replay">Whether a consumed item can be read again.</param>
/// <param name="Cost">What the bill is a function of.</param>
/// <param name="MaxItemBytes">The largest single item the primitive accepts.</param>
public sealed record PrimitiveFacts(
    DurabilityUnit Unit,
    OrderingGuarantee Ordering,
    PartitionModel Partitioning,
    ReplayModel Replay,
    CostDriver Cost,
    long MaxItemBytes);

/// <summary>The characteristic that decided a routing choice.</summary>
public enum DecidingFactor
{
    /// <summary>Several consumers need the same items, and need to re-read them.</summary>
    ReplayForIndependentConsumers,

    /// <summary>Each item is work handed to exactly one worker and then finished.</summary>
    CompetingConsumerHandoff,

    /// <summary>Queries filter on fields that are not part of the key.</summary>
    ServerSideQueryOnNonKeyFields,

    /// <summary>Lookups already know the key, so an index beyond the key is waste.</summary>
    PointLookupByKey,

    /// <summary>The item is opaque bytes, and its size is what rules the alternatives out.</summary>
    OpaquePayloadSize,
}

/// <summary>One expedition workload, described without naming a primitive.</summary>
/// <param name="Name">Human-readable workload name, used in diagnostics.</param>
/// <param name="TypicalItemBytes">Size of one item, in bytes.</param>
/// <param name="ConsumersAreIndependentAndReplay">Several consumers read the same items, independently, and may re-read them.</param>
/// <param name="ItemIsHandedToExactlyOneWorker">Each item is work that one worker completes and then removes.</param>
/// <param name="QueriesFilterOnNonKeyFields">Reads filter on fields the key does not address.</param>
/// <param name="LookupsAreByKnownKey">The caller already knows the identifier it wants.</param>
public sealed record Workload(
    string Name,
    long TypicalItemBytes,
    bool ConsumersAreIndependentAndReplay,
    bool ItemIsHandedToExactlyOneWorker,
    bool QueriesFilterOnNonKeyFields,
    bool LookupsAreByKnownKey);

/// <summary>A routing decision, together with the option it was chosen over.</summary>
/// <param name="Chosen">The primitive the workload should use.</param>
/// <param name="RunnerUp">The adjacent primitive that was rejected.</param>
/// <param name="Factor">The characteristic that decided it.</param>
/// <param name="RequiresClaimCheck">
/// True when one item is larger than the chosen primitive accepts, so the payload
/// must live in a blob and the item must carry only its name.
/// </param>
/// <param name="Justification">Prose that names the runner-up and says why it lost.</param>
public sealed record PrimitiveDecision(
    Primitive Chosen,
    Primitive RunnerUp,
    DecidingFactor Factor,
    bool RequiresClaimCheck,
    string Justification);
