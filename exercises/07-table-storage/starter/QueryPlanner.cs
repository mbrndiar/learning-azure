namespace LearningAzure.Exercises.TableStorage;

/// <summary>Classifies a lookup by what the service will actually have to do.</summary>
/// <remarks>
/// Every query against a table is one of three things, and which one it is
/// depends entirely on which keys the caller supplied. The SDK will happily run
/// all three and the syntax is identical, which is why the cost difference is
/// invisible until the table is large.
/// </remarks>
public static class QueryPlanner
{
    /// <summary>Classifies <paramref name="intent"/> into a query shape.</summary>
    /// <param name="intent">What the caller knows.</param>
    /// <returns>The shape the service will execute.</returns>
    public static QueryShape Classify(LookupIntent intent) =>
        // GAP 5 — Both keys known is a PointRead; only the partition key is a
        // PartitionScan; anything else is a TableScan, no matter how selective
        // the filter looks. A filter on a non-key property does not reduce what
        // is scanned; it only reduces what is returned.
        throw new NotImplementedException(
            "GAP 5: implement QueryPlanner.Classify. See "
            + "lessons/07-table-storage/README.md#three-query-shapes-one-syntax.");

    /// <summary>Builds the OData filter for <paramref name="intent"/>.</summary>
    /// <param name="intent">What the caller knows.</param>
    /// <returns>An OData filter string.</returns>
    public static string BuildFilter(LookupIntent intent) =>
        // GAP 6 — Key predicates first, and always by key name.
        //
        // "StationId eq 'x'" and "PartitionKey eq 'x'" return the same rows and
        // cost completely different amounts: only the second one narrows the
        // scan. Filtering the duplicated StationId column is the most common way
        // to write an accidental table scan.
        //
        // The evaluator checks four cases: station + instant (PartitionKey eq
        // and RowKey eq), station + since (PartitionKey eq and RowKey ge),
        // station alone (no key predicate is available), since alone, and
        // neither (an empty filter).
        throw new NotImplementedException(
            "GAP 6: implement QueryPlanner.BuildFilter. See "
            + "lessons/07-table-storage/README.md#three-query-shapes-one-syntax.");

    /// <summary>Counts what a query cost against a known data set.</summary>
    /// <param name="shape">The shape the query executed as.</param>
    /// <param name="tableSize">Entities in the whole table.</param>
    /// <param name="partitionSize">Entities in the targeted partition.</param>
    /// <param name="matched">Entities the caller wanted.</param>
    /// <returns>The measured cost.</returns>
    public static QueryCost Measure(QueryShape shape, int tableSize, int partitionSize, int matched) =>
        // GAP 7 — What is SCANNED depends on the shape; what is RETURNED does
        // not. A point read scans exactly one entity regardless of how large the
        // table has become — that is the property the whole key design exists to
        // buy. A partition scan scans the partition; a table scan scans the
        // table.
        throw new NotImplementedException(
            "GAP 7: implement QueryPlanner.Measure. See "
            + "lessons/07-table-storage/README.md#three-query-shapes-one-syntax.");
}
