using LearningAzure.Exercises.DataMap;

namespace LearningAzure.Exercises.DataMap.Tests;

/// <summary>
/// Judges the characteristics table that every routing rule reads.
/// </summary>
/// <remarks>
/// A selection rule can only be as good as the facts it compares, so these
/// assertions are exact. They also assert the invariants that distinguish the
/// primitives from one another, which is what a copy-pasted table fails.
/// </remarks>
public sealed class PrimitiveCharacteristicsTests
{
    /// <summary>Every primitive the course teaches.</summary>
    private static readonly Primitive[] AllPrimitives =
        [Primitive.Blob, Primitive.Queue, Primitive.Table, Primitive.EventStream, Primitive.Document];

    [Fact]
    public void Blob_is_an_unordered_object_that_reading_never_consumes()
    {
        var facts = PrimitiveCharacteristics.For(Primitive.Blob);

        Assert.Equal(DurabilityUnit.OpaqueObject, facts.Unit);
        Assert.Equal(OrderingGuarantee.None, facts.Ordering);
        Assert.Equal(PartitionModel.NamePrefixOnly, facts.Partitioning);
        Assert.Equal(ReplayModel.Unlimited, facts.Replay);
        Assert.Equal(CostDriver.StoredBytesAndOperations, facts.Cost);
        Assert.Equal(PrimitiveCharacteristics.MaxBlobBytes, facts.MaxItemBytes);
    }

    [Fact]
    public void Queue_hands_out_service_managed_messages_that_survive_until_deleted()
    {
        var facts = PrimitiveCharacteristics.For(Primitive.Queue);

        Assert.Equal(DurabilityUnit.Message, facts.Unit);
        Assert.Equal(OrderingGuarantee.BestEffortFifo, facts.Ordering);
        Assert.Equal(PartitionModel.ServiceManaged, facts.Partitioning);
        Assert.Equal(ReplayModel.UntilDeleted, facts.Replay);
        Assert.Equal(CostDriver.OperationsOnly, facts.Cost);
        Assert.Equal(PrimitiveCharacteristics.MaxQueueMessagePayloadBytes, facts.MaxItemBytes);
    }

    [Fact]
    public void Table_sorts_entities_by_row_key_inside_a_partition()
    {
        var facts = PrimitiveCharacteristics.For(Primitive.Table);

        Assert.Equal(DurabilityUnit.Entity, facts.Unit);
        Assert.Equal(OrderingGuarantee.SortedWithinPartition, facts.Ordering);
        Assert.Equal(PartitionModel.PartitionKey, facts.Partitioning);
        Assert.Equal(ReplayModel.Unlimited, facts.Replay);
        Assert.Equal(CostDriver.StoredBytesAndOperations, facts.Cost);
        Assert.Equal(PrimitiveCharacteristics.MaxTableEntityBytes, facts.MaxItemBytes);
    }

    [Fact]
    public void EventStream_orders_strictly_inside_a_partition_and_bills_for_capacity()
    {
        var facts = PrimitiveCharacteristics.For(Primitive.EventStream);

        Assert.Equal(DurabilityUnit.Event, facts.Unit);
        Assert.Equal(OrderingGuarantee.StrictWithinPartition, facts.Ordering);
        Assert.Equal(PartitionModel.PartitionKeyWithOffsets, facts.Partitioning);
        Assert.Equal(ReplayModel.WithinRetentionWindow, facts.Replay);
        Assert.Equal(CostDriver.ProvisionedThroughput, facts.Cost);
        Assert.Equal(PrimitiveCharacteristics.MaxEventBytes, facts.MaxItemBytes);
    }

    [Fact]
    public void Document_is_indexed_beyond_its_key_and_bills_request_units()
    {
        var facts = PrimitiveCharacteristics.For(Primitive.Document);

        Assert.Equal(DurabilityUnit.Document, facts.Unit);
        Assert.Equal(OrderingGuarantee.None, facts.Ordering);
        Assert.Equal(PartitionModel.PartitionKey, facts.Partitioning);
        Assert.Equal(ReplayModel.Unlimited, facts.Replay);
        Assert.Equal(CostDriver.RequestUnits, facts.Cost);
        Assert.Equal(PrimitiveCharacteristics.MaxDocumentBytes, facts.MaxItemBytes);
    }

    /// <summary>
    /// Consumption semantics are the sharpest difference between the primitives:
    /// exactly one consumes on read, and exactly one expires. A table that blurs
    /// them cannot support a defensible routing rule.
    /// </summary>
    [Fact]
    public void Replay_semantics_are_unique_where_they_have_to_be()
    {
        var replay = AllPrimitives
            .ToDictionary(primitive => primitive, primitive => PrimitiveCharacteristics.For(primitive).Replay);

        Assert.Equal(
            [Primitive.Queue],
            replay.Where(entry => entry.Value == ReplayModel.UntilDeleted).Select(entry => entry.Key));
        Assert.Equal(
            [Primitive.EventStream],
            replay.Where(entry => entry.Value == ReplayModel.WithinRetentionWindow).Select(entry => entry.Key));
        Assert.Equal(
            [Primitive.Blob, Primitive.Table, Primitive.Document],
            replay.Where(entry => entry.Value == ReplayModel.Unlimited).Select(entry => entry.Key).Order());
    }

    /// <summary>
    /// Cost drivers must not be uniform either: reserving capacity, paying per
    /// request unit, and paying per operation are what make one choice cheap and
    /// the adjacent one expensive for the same workload.
    /// </summary>
    [Fact]
    public void Cost_drivers_distinguish_the_primitives()
    {
        var costs = AllPrimitives
            .Select(primitive => PrimitiveCharacteristics.For(primitive).Cost)
            .ToList();

        Assert.Equal(4, costs.Distinct().Count());
        Assert.Single(costs, CostDriver.ProvisionedThroughput);
        Assert.Single(costs, CostDriver.RequestUnits);
        Assert.Single(costs, CostDriver.OperationsOnly);
    }

    /// <summary>
    /// The service limit describes the message body. Applications that opt into
    /// Base64 must apply their smaller raw-payload policy separately.
    /// </summary>
    [Fact]
    public void Queue_ceiling_is_the_service_message_limit()
    {
        var ceiling = PrimitiveCharacteristics.For(Primitive.Queue).MaxItemBytes;

        Assert.Equal(65_536, ceiling);
    }

    [Fact]
    public void An_unknown_primitive_fails_loudly_instead_of_returning_a_default()
    {
        var unknown = (Primitive)999;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveCharacteristics.For(unknown));
        Assert.Equal("primitive", error.ParamName);
    }
}
