using Azure;
using Azure.Data.Tables;

namespace LearningAzure.Exercises.TableStorage;

/// <summary>One station observation, as it is stored in the table.</summary>
/// <remarks>
/// <see cref="ITableEntity"/> fixes the four system properties every entity
/// has. Everything else on this type is a column that exists only on the rows
/// that set it: the table has no schema, so two rows in the same table may
/// carry different properties entirely.
/// </remarks>
public sealed class ObservationEntity : ITableEntity
{
    /// <summary>The partition this observation lives in. Chosen, never incidental.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>The row's identity within its partition. Sorted ascending, as a string.</summary>
    public string RowKey { get; set; } = string.Empty;

    /// <summary>Server-assigned last-write time.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>The entity version, used for optimistic concurrency.</summary>
    public ETag ETag { get; set; }

    /// <summary>The station that produced the observation.</summary>
    public string StationId { get; set; } = string.Empty;

    /// <summary>When the observation was taken, in UTC.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>Temperature in degrees Celsius.</summary>
    public double TemperatureC { get; set; }

    /// <summary>Processing state: pending, ingested, or rejected.</summary>
    public string Status { get; set; } = "pending";
}

/// <summary>A lookup a caller wants to perform, stated before a key layout exists.</summary>
/// <param name="StationId">The station, when the caller knows it.</param>
/// <param name="ObservedAt">The exact instant, when the caller knows it.</param>
/// <param name="Since">The start of a time range, when the caller wants a range.</param>
public sealed record LookupIntent(string? StationId, DateTimeOffset? ObservedAt, DateTimeOffset? Since);

/// <summary>What the service has to do to answer a query.</summary>
public enum QueryShape
{
    /// <summary>Both keys are known. One entity, one lookup, cost independent of table size.</summary>
    PointRead,

    /// <summary>The partition is known and rows are filtered. Cost grows with the partition.</summary>
    PartitionScan,

    /// <summary>Neither key is usable. Cost grows with the whole table.</summary>
    TableScan,
}

/// <summary>What a query actually cost, counted rather than estimated.</summary>
/// <param name="Shape">The shape the query degenerated to.</param>
/// <param name="EntitiesScanned">Entities the service had to look at.</param>
/// <param name="EntitiesReturned">Entities the caller received.</param>
public sealed record QueryCost(QueryShape Shape, int EntitiesScanned, int EntitiesReturned)
{
    /// <summary>Entities read and thrown away, per entity actually wanted.</summary>
    /// <remarks>A ratio above 1 is work the caller paid for and did not use.</remarks>
    public double Waste => EntitiesReturned == 0
        ? EntitiesScanned
        : (double)EntitiesScanned / EntitiesReturned;
}

/// <summary>The outcome of an optimistic-concurrency update.</summary>
public enum UpdateOutcome
{
    /// <summary>The update landed against the version the caller read.</summary>
    Applied,

    /// <summary>Somebody else wrote first; the caller's version is stale.</summary>
    Stale,

    /// <summary>The entity does not exist.</summary>
    Missing,
}

/// <summary>Why a transactional batch cannot be submitted as written.</summary>
public enum BatchRejection
{
    /// <summary>The batch is valid.</summary>
    None,

    /// <summary>The batch spans more than one partition. The service has no cross-partition transaction.</summary>
    CrossPartition,

    /// <summary>The batch exceeds the 100-operation limit.</summary>
    TooManyOperations,

    /// <summary>The batch touches the same row key twice.</summary>
    DuplicateRowKey,

    /// <summary>The batch is empty.</summary>
    Empty,
}

/// <summary>A single write inside a transactional batch.</summary>
/// <param name="PartitionKey">The partition the write targets.</param>
/// <param name="RowKey">The row the write targets.</param>
public sealed record BatchWrite(string PartitionKey, string RowKey);
