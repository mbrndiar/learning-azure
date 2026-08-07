namespace LearningAzure.Exercises.TableStorage;

/// <summary>Validates a transactional batch before the service rejects it.</summary>
/// <remarks>
/// A table transaction is real: every operation in it succeeds or none does. The
/// price is that it may not leave one partition, which turns partition-key
/// design into a transaction-boundary decision rather than a performance one.
/// </remarks>
public static class BatchValidator
{
    /// <summary>The service's limit on operations in one transactional batch.</summary>
    public const int MaxOperations = 100;

    /// <summary>Reports why <paramref name="writes"/> cannot be submitted, if it cannot.</summary>
    /// <param name="writes">The batch, in order.</param>
    /// <returns>The first rejection reason, or <see cref="BatchRejection.None"/>.</returns>
    public static BatchRejection Validate(IReadOnlyList<BatchWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);

        // GAP 10 — Check in this order: empty, cross-partition, over the
        // operation limit, duplicate row key.
        //
        // The cross-partition check is the one that changes designs. There is no
        // way to make it work — no setting, no SDK option, no retry — so a
        // requirement to write two entities atomically is a requirement that
        // they share a partition key.
        if (writes.Count == 0)
        {
            return BatchRejection.Empty;
        }

        var partition = writes[0].PartitionKey;

        foreach (var write in writes)
        {
            if (!string.Equals(write.PartitionKey, partition, StringComparison.Ordinal))
            {
                return BatchRejection.CrossPartition;
            }
        }

        if (writes.Count > MaxOperations)
        {
            return BatchRejection.TooManyOperations;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var write in writes)
        {
            if (!seen.Add(write.RowKey))
            {
                return BatchRejection.DuplicateRowKey;
            }
        }

        return BatchRejection.None;
    }

    /// <summary>Splits a mixed batch into one submittable batch per partition.</summary>
    /// <param name="writes">The writes, in any order.</param>
    /// <returns>Batches, each within one partition and within the operation limit.</returns>
    /// <remarks>
    /// This is the honest fallback, and it is important to see what it costs:
    /// the atomicity is gone. Each returned batch succeeds or fails on its own,
    /// so the caller must be able to tolerate a partial outcome.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<BatchWrite>> SplitByPartition(IReadOnlyList<BatchWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);

        var batches = new List<IReadOnlyList<BatchWrite>>();

        foreach (var group in writes.GroupBy(write => write.PartitionKey, StringComparer.Ordinal))
        {
            batches.AddRange(group.Chunk(MaxOperations).Select(chunk => (IReadOnlyList<BatchWrite>)chunk));
        }

        return batches;
    }
}
