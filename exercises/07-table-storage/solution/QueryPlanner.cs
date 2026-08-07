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
    public static QueryShape Classify(LookupIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        // GAP 5 — Both keys known is a point read; only the partition key is a
        // partition scan; anything else is a table scan, no matter how selective
        // the filter looks. A filter on a non-key property does not reduce what
        // is scanned; it only reduces what is returned.
        var hasPartition = !string.IsNullOrWhiteSpace(intent.StationId);

        if (!hasPartition)
        {
            return QueryShape.TableScan;
        }

        return intent.ObservedAt.HasValue ? QueryShape.PointRead : QueryShape.PartitionScan;
    }

    /// <summary>Builds the OData filter for <paramref name="intent"/>.</summary>
    /// <param name="intent">What the caller knows.</param>
    /// <returns>An OData filter string.</returns>
    public static string BuildFilter(LookupIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var clauses = new List<string>();

        // GAP 6 — Key predicates first, and always by key name.
        //
        // "StationId eq 'x'" and "PartitionKey eq 'x'" return the same rows and
        // cost completely different amounts: only the second one narrows the
        // scan. Filtering the duplicated StationId column is the most common way
        // to write an accidental table scan.
        if (!string.IsNullOrWhiteSpace(intent.StationId) && intent.ObservedAt.HasValue)
        {
            var partition = ObservationKeys.PartitionKeyFor(intent.StationId, intent.ObservedAt.Value);
            clauses.Add($"PartitionKey eq '{partition}'");
            clauses.Add($"RowKey eq '{ObservationKeys.RowKeyFor(intent.ObservedAt.Value)}'");
        }
        else if (!string.IsNullOrWhiteSpace(intent.StationId) && intent.Since.HasValue)
        {
            var partition = ObservationKeys.PartitionKeyFor(intent.StationId, intent.Since.Value);
            clauses.Add($"PartitionKey eq '{partition}'");
            clauses.Add($"RowKey ge '{ObservationKeys.RowKeyFor(intent.Since.Value)}'");
        }
        else if (!string.IsNullOrWhiteSpace(intent.StationId))
        {
            clauses.Add($"StationId eq '{intent.StationId}'");
        }
        else if (intent.Since.HasValue)
        {
            clauses.Add($"ObservedAt ge datetime'{intent.Since.Value.UtcDateTime:O}'");
        }

        return clauses.Count == 0 ? string.Empty : string.Join(" and ", clauses);
    }

    /// <summary>Counts what a query cost against a known data set.</summary>
    /// <param name="shape">The shape the query executed as.</param>
    /// <param name="tableSize">Entities in the whole table.</param>
    /// <param name="partitionSize">Entities in the targeted partition.</param>
    /// <param name="matched">Entities the caller wanted.</param>
    /// <returns>The measured cost.</returns>
    public static QueryCost Measure(QueryShape shape, int tableSize, int partitionSize, int matched)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tableSize);
        ArgumentOutOfRangeException.ThrowIfNegative(partitionSize);
        ArgumentOutOfRangeException.ThrowIfNegative(matched);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partitionSize, tableSize);

        // GAP 7 — What is SCANNED depends on the shape; what is RETURNED does
        // not. A point read scans exactly one entity regardless of how large the
        // table has become — that is the property the whole key design exists to
        // buy.
        var scanned = shape switch
        {
            QueryShape.PointRead => matched == 0 ? 1 : matched,
            QueryShape.PartitionScan => partitionSize,
            QueryShape.TableScan => tableSize,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        return new QueryCost(shape, scanned, matched);
    }
}
