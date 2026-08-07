namespace LearningAzure.Exercises.TableStorage.Tests;

public sealed class BatchValidatorTests
{
    private static BatchWrite Write(string partition, string row) => new(partition, row);

    private static IReadOnlyList<BatchWrite> OnePartition(int count) =>
        [.. Enumerable.Range(1, count).Select(n => Write("station-bravo|2026-07-06", $"row-{n:0000}"))];

    [Fact]
    public void ASinglePartitionBatchIsValid()
    {
        Assert.Equal(BatchRejection.None, BatchValidator.Validate(OnePartition(3)));
    }

    [Fact]
    public void ABatchOfExactlyOneHundredIsValid()
    {
        Assert.Equal(BatchRejection.None, BatchValidator.Validate(OnePartition(100)));
    }

    [Fact]
    public void ABatchOfOneHundredAndOneIsRejected()
    {
        Assert.Equal(BatchRejection.TooManyOperations, BatchValidator.Validate(OnePartition(101)));
    }

    [Fact]
    public void ThePublishedOperationLimitIsOneHundred()
    {
        Assert.Equal(100, BatchValidator.MaxOperations);
    }

    [Fact]
    public void AnEmptyBatchIsRejected()
    {
        Assert.Equal(BatchRejection.Empty, BatchValidator.Validate([]));
    }

    [Fact]
    public void ACrossPartitionBatchIsRejected()
    {
        IReadOnlyList<BatchWrite> writes =
        [
            Write("station-bravo|2026-07-06", "row-1"),
            Write("station-delta|2026-07-06", "row-1"),
        ];

        Assert.Equal(BatchRejection.CrossPartition, BatchValidator.Validate(writes));
    }

    [Fact]
    public void TwoDaysOfTheSameStationAreStillCrossPartition()
    {
        // The day is part of the partition key, so "one station" is not one
        // partition. This is the cost of the key design, stated honestly.
        IReadOnlyList<BatchWrite> writes =
        [
            Write("station-bravo|2026-07-06", "row-1"),
            Write("station-bravo|2026-07-07", "row-1"),
        ];

        Assert.Equal(BatchRejection.CrossPartition, BatchValidator.Validate(writes));
    }

    [Fact]
    public void CrossPartitionIsReportedBeforeTheOperationLimit()
    {
        // Splitting is the fix for one and impossible for the other, so the
        // caller needs the harder problem named first.
        var writes = new List<BatchWrite>(OnePartition(101))
        {
            Write("station-delta|2026-07-06", "row-1"),
        };

        Assert.Equal(BatchRejection.CrossPartition, BatchValidator.Validate(writes));
    }

    [Fact]
    public void ADuplicateRowKeyIsRejected()
    {
        IReadOnlyList<BatchWrite> writes =
        [
            Write("station-bravo|2026-07-06", "row-1"),
            Write("station-bravo|2026-07-06", "row-1"),
        ];

        Assert.Equal(BatchRejection.DuplicateRowKey, BatchValidator.Validate(writes));
    }

    [Fact]
    public void RowKeysAreComparedCaseSensitivelyLikeTheService()
    {
        IReadOnlyList<BatchWrite> writes =
        [
            Write("station-bravo|2026-07-06", "Row-1"),
            Write("station-bravo|2026-07-06", "row-1"),
        ];

        Assert.Equal(BatchRejection.None, BatchValidator.Validate(writes));
    }

    [Fact]
    public void ValidateRejectsANullBatch()
    {
        Assert.Throws<ArgumentNullException>(() => BatchValidator.Validate(null!));
    }

    [Fact]
    public void SplittingAMixedBatchProducesOneBatchPerPartition()
    {
        IReadOnlyList<BatchWrite> writes =
        [
            Write("station-bravo|2026-07-06", "row-1"),
            Write("station-delta|2026-07-06", "row-1"),
            Write("station-bravo|2026-07-06", "row-2"),
        ];

        Assert.Equal(2, BatchValidator.SplitByPartition(writes).Count);
    }

    [Fact]
    public void EverySplitBatchIsItselfValid()
    {
        IReadOnlyList<BatchWrite> writes =
        [
            .. OnePartition(150),
            .. Enumerable.Range(1, 40).Select(n => Write("station-delta|2026-07-06", $"row-{n:0000}")),
        ];

        foreach (var batch in BatchValidator.SplitByPartition(writes))
        {
            Assert.Equal(BatchRejection.None, BatchValidator.Validate(batch));
        }
    }

    [Fact]
    public void SplittingChunksAnOversizedPartitionAtTheOperationLimit()
    {
        var batches = BatchValidator.SplitByPartition(OnePartition(250));

        Assert.Equal(3, batches.Count);
        Assert.Equal([100, 100, 50], batches.Select(batch => batch.Count));
    }

    [Fact]
    public void SplittingLosesNoWrites()
    {
        IReadOnlyList<BatchWrite> writes =
        [
            .. OnePartition(150),
            .. Enumerable.Range(1, 40).Select(n => Write("station-delta|2026-07-06", $"row-{n:0000}")),
        ];

        var recovered = BatchValidator.SplitByPartition(writes).SelectMany(batch => batch).ToArray();

        Assert.Equal(writes.Count, recovered.Length);
        Assert.Equal<IEnumerable<BatchWrite>>([.. writes.OrderBy(w => w.PartitionKey + w.RowKey, StringComparer.Ordinal)],
            [.. recovered.OrderBy(w => w.PartitionKey + w.RowKey, StringComparer.Ordinal)]);
    }

    [Fact]
    public void SplittingAnEmptyBatchProducesNothing()
    {
        Assert.Empty(BatchValidator.SplitByPartition([]));
    }

    [Fact]
    public void SplitByPartitionRejectsANullBatch()
    {
        Assert.Throws<ArgumentNullException>(() => BatchValidator.SplitByPartition(null!));
    }

    [Fact]
    public void SplittingSurrendersAtomicityWhichIsWhyItIsNotAutomatic()
    {
        // Two writes that must land together cannot be split; the fact that
        // splitting produces two batches IS the loss of the guarantee.
        IReadOnlyList<BatchWrite> writes =
        [
            Write("station-bravo|2026-07-06", "row-1"),
            Write("station-delta|2026-07-06", "row-1"),
        ];

        Assert.Equal(BatchRejection.CrossPartition, BatchValidator.Validate(writes));
        Assert.Equal(2, BatchValidator.SplitByPartition(writes).Count);
    }
}
