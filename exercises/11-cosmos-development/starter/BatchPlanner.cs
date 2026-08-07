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

        // GAP 12: partition key first, limits second.
        //
        // A transactional batch is atomic because it executes inside one
        // replica set, and there is one replica set per logical partition.
        // Chunking by size and then hoping each chunk happens to be
        // single-partition produces a 400 at runtime for exactly the inputs
        // your tests did not have. Order within a partition has to survive too:
        // a batch is an ordered list, and "create then patch" is not the same
        // request as "patch then create". Reject an operation that is larger
        // than MaximumBytes, because no batch can ever carry it.
        // See lessons/11-cosmos-development/README.md#a-batch-is-one-partition-or-nothing
        throw new NotImplementedException(
            "GAP 12: implement BatchPlanner.Split. "
            + "See lessons/11-cosmos-development/README.md#a-batch-is-one-partition-or-nothing.");
    }

    /// <summary>Finds the operation that actually failed.</summary>
    /// <param name="statusCodes">The per-operation statuses, in submission order.</param>
    /// <returns>Which operation failed, why, and how many were dragged down with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="statusCodes"/> is <see langword="null"/>.</exception>
    public static BatchDiagnosis Diagnose(IReadOnlyList<int> statusCodes)
    {
        ArgumentNullException.ThrowIfNull(statusCodes);

        // GAP 13: 424 is never the answer.
        //
        // Failed Dependency means "this operation was fine; the batch was not".
        // Reporting the first non-success status treats the innocent operation
        // at position 0 as the cause and sends the reader to debug a create
        // that would have worked. The real failure is the first status that is
        // neither a success (2xx) nor a 424 — and on a real account it is
        // frequently the LAST operation, because that is where the conflict was
        // found. Report -1 and a 200 when nothing failed.
        // See lessons/11-cosmos-development/README.md#a-batch-is-one-partition-or-nothing
        throw new NotImplementedException(
            "GAP 13: implement BatchPlanner.Diagnose. "
            + "See lessons/11-cosmos-development/README.md#a-batch-is-one-partition-or-nothing.");
    }
}
