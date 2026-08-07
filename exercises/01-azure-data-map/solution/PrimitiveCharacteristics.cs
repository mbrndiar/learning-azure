namespace LearningAzure.Exercises.DataMap;

/// <summary>The characteristics of each Azure data primitive.</summary>
/// <remarks>
/// <para>
/// This table is the data the routing rules in <see cref="PrimitiveSelector"/>
/// read. Getting it right is the point: a selection rule can only be as good as
/// the characteristics it compares.
/// </para>
/// <para>
/// The narrative derives every value in
/// <c>lessons/01-azure-data-map/README.md</c>, and
/// <c>dotnet run --project lessons/01-azure-data-map/PrimitiveTour</c> prints
/// them against one real expedition record.
/// </para>
/// </remarks>
public static class PrimitiveCharacteristics
{
    /// <summary>
    /// A Queue Storage message is limited to 64 KiB *after* encoding, and the
    /// SDK's default Base64 encoding expands a payload by four thirds — so the
    /// largest raw payload that still fits is 48 KiB.
    /// </summary>
    public const long MaxQueueMessagePayloadBytes = 49_152;

    /// <summary>Blob Storage accepts single blobs far larger than any expedition artifact.</summary>
    public const long MaxBlobBytes = 190_711_820_083_200;

    /// <summary>One table entity, across all of its properties.</summary>
    public const long MaxTableEntityBytes = 1_048_576;

    /// <summary>One event, including its properties and system overhead.</summary>
    public const long MaxEventBytes = 1_048_576;

    /// <summary>One Cosmos DB for NoSQL document.</summary>
    public const long MaxDocumentBytes = 2_097_152;

    /// <summary>Returns the characteristics of <paramref name="primitive"/>.</summary>
    /// <param name="primitive">The primitive to describe.</param>
    /// <returns>The durability unit, ordering, partitioning, replay, cost driver, and item ceiling.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The primitive is not one of the five taught here.</exception>
    public static PrimitiveFacts For(Primitive primitive) => primitive switch
    {
        // A blob is an opaque object in a flat namespace: nothing orders blobs
        // against each other, and reading one never consumes it.
        Primitive.Blob => new PrimitiveFacts(
            DurabilityUnit.OpaqueObject,
            OrderingGuarantee.None,
            PartitionModel.NamePrefixOnly,
            ReplayModel.Unlimited,
            CostDriver.StoredBytesAndOperations,
            MaxBlobBytes),

        // A queue hands out the next visible message; the caller never picks one.
        // The message survives until it is deleted, which is exactly why a
        // consumer that crashes mid-work gets the message again.
        Primitive.Queue => new PrimitiveFacts(
            DurabilityUnit.Message,
            OrderingGuarantee.BestEffortFifo,
            PartitionModel.ServiceManaged,
            ReplayModel.UntilDeleted,
            CostDriver.OperationsOnly,
            MaxQueueMessagePayloadBytes),

        // A table entity is addressed by PartitionKey plus RowKey, and rows are
        // stored sorted by RowKey inside a partition — the only ordering there is.
        Primitive.Table => new PrimitiveFacts(
            DurabilityUnit.Entity,
            OrderingGuarantee.SortedWithinPartition,
            PartitionModel.PartitionKey,
            ReplayModel.Unlimited,
            CostDriver.StoredBytesAndOperations,
            MaxTableEntityBytes),

        // An event stream is the only primitive here where several unrelated
        // consumers can read the same items and re-read them — inside retention.
        Primitive.EventStream => new PrimitiveFacts(
            DurabilityUnit.Event,
            OrderingGuarantee.StrictWithinPartition,
            PartitionModel.PartitionKeyWithOffsets,
            ReplayModel.WithinRetentionWindow,
            CostDriver.ProvisionedThroughput,
            MaxEventBytes),

        // A document is indexed beyond its key, and that index is what request
        // units pay for on every read and write.
        Primitive.Document => new PrimitiveFacts(
            DurabilityUnit.Document,
            OrderingGuarantee.None,
            PartitionModel.PartitionKey,
            ReplayModel.Unlimited,
            CostDriver.RequestUnits,
            MaxDocumentBytes),

        // Returning a default here would let a wrong characteristic produce a
        // confidently wrong routing decision, so it fails loudly instead.
        _ => throw new ArgumentOutOfRangeException(
            nameof(primitive),
            primitive,
            "Unknown primitive; this course teaches Blob, Queue, Table, EventStream, and Document."),
    };
}
