namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>
/// Turns a pile of writes into batches the service will actually accept, and
/// turns a failed batch back into an explanation.
/// </summary>
public sealed class BatchPlanner
{
    /// <summary>The most operations one transactional batch may carry.</summary>
    public const int MaximumOperations = 100;

    /// <summary>The most bytes one transactional batch may carry.</summary>
    public const int MaximumBytes = 2 * 1024 * 1024;

    /// <summary>The status an operation reports when its batch failed elsewhere.</summary>
    public const int FailedDependency = 424;

    /// <summary>Splits operations into legal batches.</summary>
    /// <param name="operations">The operations, in the order they were requested.</param>
    /// <returns>Batches, each within one logical partition and within both limits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A single operation exceeds the byte limit.</exception>
    public static IReadOnlyList<BatchGroup> Split(IReadOnlyList<BatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var groups = new List<BatchGroup>();

        // GAP 12: partition key first, limits second.
        //
        // A transactional batch is atomic because it executes inside one
        // replica set, and there is one replica set per logical partition.
        // Chunking by size and then hoping each chunk happens to be
        // single-partition produces a 400 at runtime for exactly the inputs
        // your tests did not have. Order within a partition has to survive too:
        // a batch is an ordered list, and "create then patch" is not the same
        // request as "patch then create".
        // See lessons/11-cosmos-development/README.md#a-batch-is-one-partition-or-nothing
        foreach (var partition in operations.GroupBy(operation => operation.PartitionKey, StringComparer.Ordinal))
        {
            var current = new List<BatchOperation>();
            var bytes = 0;

            foreach (var operation in partition)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(
                    operation.SizeBytes,
                    MaximumBytes,
                    nameof(operations));

                if (current.Count == MaximumOperations || bytes + operation.SizeBytes > MaximumBytes)
                {
                    groups.Add(new BatchGroup(partition.Key, current));
                    current = [];
                    bytes = 0;
                }

                current.Add(operation);
                bytes += operation.SizeBytes;
            }

            if (current.Count > 0)
            {
                groups.Add(new BatchGroup(partition.Key, current));
            }
        }

        return groups;
    }

    /// <summary>Finds the operation that actually failed.</summary>
    /// <param name="statusCodes">The per-operation statuses, in submission order.</param>
    /// <returns>Which operation failed, why, and how many were dragged down with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="statusCodes"/> is <see langword="null"/>.</exception>
    public static BatchDiagnosis Diagnose(IReadOnlyList<int> statusCodes)
    {
        ArgumentNullException.ThrowIfNull(statusCodes);

        var collateral = 0;
        var culpritIndex = -1;
        var culpritStatus = ConcurrencyGuard.Ok;

        // GAP 13: 424 is never the answer.
        //
        // Failed Dependency means "this operation was fine; the batch was not".
        // Reporting the first non-success status treats the innocent operation
        // at position 0 as the cause and sends the reader to debug a create
        // that would have worked. The real failure is the first status that is
        // neither a success nor a 424 — and on a real account it is frequently
        // the LAST operation, because that is where the conflict was found.
        // See lessons/11-cosmos-development/README.md#a-batch-is-one-partition-or-nothing
        for (var index = 0; index < statusCodes.Count; index++)
        {
            var status = statusCodes[index];

            if (status == FailedDependency)
            {
                collateral++;
                continue;
            }

            if (status is >= 200 and <= 299)
            {
                continue;
            }

            if (culpritIndex < 0)
            {
                culpritIndex = index;
                culpritStatus = status;
            }
        }

        return new BatchDiagnosis(culpritIndex, culpritStatus, collateral);
    }
}
