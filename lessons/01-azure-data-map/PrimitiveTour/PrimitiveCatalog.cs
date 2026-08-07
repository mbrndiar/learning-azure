using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace LearningAzure.Lessons.DataMap;

/// <summary>The five Azure data primitives this course teaches.</summary>
internal enum Primitive
{
    /// <summary>Durable opaque objects — Azure Blob Storage.</summary>
    Blob,

    /// <summary>Work messages handed to one consumer — Azure Queue Storage.</summary>
    Queue,

    /// <summary>Keyed entities in a partitioned index — Azure Table Storage.</summary>
    Table,

    /// <summary>A retained, partitioned event stream — Azure Event Hubs.</summary>
    EventStream,

    /// <summary>Queryable JSON documents — Azure Cosmos DB for NoSQL.</summary>
    Document,
}

/// <summary>The characteristics that decide between two adjacent primitives.</summary>
/// <param name="Primitive">The primitive being described.</param>
/// <param name="StoredThing">What one stored item actually is.</param>
/// <param name="KeyModel">How an item is addressed.</param>
/// <param name="Ordering">What ordering the service guarantees.</param>
/// <param name="Replay">Whether a consumed item can be read again.</param>
/// <param name="CostDriver">What the bill is actually a function of.</param>
/// <param name="ClientType">The .NET client type the primitive is reached through.</param>
internal sealed record PrimitiveProfile(
    Primitive Primitive,
    string StoredThing,
    string KeyModel,
    string Ordering,
    string Replay,
    string CostDriver,
    string ClientType);

/// <summary>The comparison table the module's decision rules are derived from.</summary>
internal static class PrimitiveCatalog
{
    /// <summary>
    /// Printed for a primitive whose SDK package this module deliberately does not
    /// reference, so the tour never claims to resolve a type it cannot see.
    /// </summary>
    private const string TaughtLater = " (package referenced in a later module)";

    /// <summary>Every profile, in the order the narrative compares them.</summary>
    internal static IReadOnlyList<PrimitiveProfile> All { get; } =
    [
        new(
            Primitive.Blob,
            "one opaque byte range with metadata",
            "container + blob name (a flat namespace with '/' in the name)",
            "none across blobs; last write wins per blob",
            "unlimited — reading never consumes",
            "stored GiB-months, plus per-operation and egress charges",
            typeof(BlobContainerClient).FullName!),
        new(
            Primitive.Queue,
            "one work message up to 64 KiB encoded",
            "no key — the next visible message is handed out",
            "best-effort FIFO, not guaranteed",
            "until deleted; redelivery after the visibility timeout",
            "per operation, including every empty poll",
            typeof(QueueClient).FullName!),
        new(
            Primitive.Table,
            "one entity: partition key, row key, and flat properties",
            "PartitionKey + RowKey, which is the only index",
            "rows sorted by RowKey inside a partition",
            "unlimited — reading never consumes",
            "stored GiB-months, plus per-transaction charges",
            typeof(TableClient).FullName!),
        new(
            Primitive.EventStream,
            "one event appended to a partition",
            "partition key chooses a partition, offsets address events",
            "strict order inside a partition, none across partitions",
            "any consumer may replay within the retention window",
            "provisioned throughput units, not stored bytes",
            "Azure.Messaging.EventHubs.Producer.EventHubProducerClient" + TaughtLater),
        new(
            Primitive.Document,
            "one JSON document with an arbitrary shape",
            "partition key + id, plus a secondary index over properties",
            "none across documents",
            "unlimited — reading never consumes",
            "provisioned or consumed request units, plus stored GiB",
            "Microsoft.Azure.Cosmos.CosmosClient" + TaughtLater),
    ];
}
