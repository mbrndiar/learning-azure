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
    /// A Queue Storage message body is limited to 64 KiB. The v12 SDK does not
    /// transform the body unless the application explicitly selects Base64.
    /// </summary>
    public const long MaxQueueMessagePayloadBytes = 65_536;

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
    public static PrimitiveFacts For(Primitive primitive) =>
        // GAP 1 — Fill in the table.
        //
        // Return one PrimitiveFacts per Primitive, using the constants above for
        // MaxItemBytes. Read "Compare the primitives" in the module narrative:
        // every value below is derived there, and the tour prints them.
        //
        // An unknown value must throw ArgumentOutOfRangeException rather than
        // returning a default, because a silently wrong characteristic produces a
        // silently wrong routing decision.
        throw new NotImplementedException(
            "GAP 1: implement PrimitiveCharacteristics.For. See "
            + "lessons/01-azure-data-map/README.md#compare-the-primitives.");
}
