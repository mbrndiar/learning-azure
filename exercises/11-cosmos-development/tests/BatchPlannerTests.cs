using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// Checks that batches respect the one boundary Cosmos will not bend — a batch
/// lives inside one logical partition — and that a failed batch is read for the
/// operation that actually failed.
/// </summary>
public sealed class BatchPlannerTests
{
    [Fact]
    public void Split_KeepsASmallSinglePartitionSetInOneBatch()
    {
        var groups = BatchPlanner.Split(Fixtures.Operations("station-05", 10));

        Assert.Single(groups);
        Assert.Equal("station-05", groups[0].PartitionKey);
        Assert.Equal(10, groups[0].Operations.Count);
    }

    [Fact]
    public void Split_NeverMixesPartitionKeys()
    {
        List<BatchOperation> mixed =
        [
            Fixtures.Operation("station-05", 0),
            Fixtures.Operation("station-06", 0),
            Fixtures.Operation("station-05", 1),
            Fixtures.Operation("station-07", 0),
        ];

        var groups = BatchPlanner.Split(mixed);

        Assert.Equal(3, groups.Count);
        Assert.All(
            groups,
            group => Assert.All(
                group.Operations,
                operation => Assert.Equal(group.PartitionKey, operation.PartitionKey)));
    }

    [Fact]
    public void Split_GroupsInterleavedOperationsBackTogether()
    {
        List<BatchOperation> mixed =
        [
            Fixtures.Operation("station-05", 0),
            Fixtures.Operation("station-06", 0),
            Fixtures.Operation("station-05", 1),
        ];

        var groups = BatchPlanner.Split(mixed);

        var station5 = groups.Single(group => group.PartitionKey == "station-05");

        Assert.Equal(2, station5.Operations.Count);
    }

    [Fact]
    public void Split_KeepsSubmissionOrderWithinAPartition()
    {
        var operations = Fixtures.Operations("station-05", 5);

        var groups = BatchPlanner.Split(operations);

        Assert.Equal(
            operations.Select(operation => operation.Id),
            groups[0].Operations.Select(operation => operation.Id));
    }

    [Fact]
    public void Split_StopsAtTheOperationLimit()
    {
        var groups = BatchPlanner.Split(Fixtures.Operations("station-05", 250, sizeBytes: 16));

        Assert.Equal(3, groups.Count);
        Assert.Equal(BatchPlanner.MaximumOperations, groups[0].Operations.Count);
        Assert.Equal(BatchPlanner.MaximumOperations, groups[1].Operations.Count);
        Assert.Equal(50, groups[2].Operations.Count);
    }

    [Fact]
    public void Split_StopsAtTheByteLimit()
    {
        // Ten operations of 512 KB: four fit in 2 MB, so three batches.
        var groups = BatchPlanner.Split(Fixtures.Operations("station-05", 10, sizeBytes: 512 * 1024));

        Assert.Equal(3, groups.Count);
        Assert.Equal(4, groups[0].Operations.Count);
        Assert.Equal(4, groups[1].Operations.Count);
        Assert.Equal(2, groups[2].Operations.Count);
    }

    [Fact]
    public void Split_NeverExceedsEitherLimit()
    {
        var operations = Fixtures.Operations("station-05", 400, sizeBytes: 64 * 1024);

        foreach (var group in BatchPlanner.Split(operations))
        {
            Assert.True(group.Operations.Count <= BatchPlanner.MaximumOperations);
            Assert.True(group.Operations.Sum(operation => operation.SizeBytes) <= BatchPlanner.MaximumBytes);
        }
    }

    [Fact]
    public void Split_LosesNothing()
    {
        List<BatchOperation> operations =
        [
            .. Fixtures.Operations("station-05", 150, sizeBytes: 4096),
            .. Fixtures.Operations("station-06", 40, sizeBytes: 4096),
        ];

        var kept = BatchPlanner.Split(operations).SelectMany(group => group.Operations).ToList();

        Assert.Equal(operations.Count, kept.Count);
        Assert.Equal(
            operations.Select(operation => operation.Id).Order(StringComparer.Ordinal),
            kept.Select(operation => operation.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Split_ReturnsNothingForNothing()
    {
        Assert.Empty(BatchPlanner.Split([]));
    }

    [Fact]
    public void Split_RejectsAnOperationThatCannotFitInAnyBatch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BatchPlanner.Split([Fixtures.Operation("station-05", 0, BatchPlanner.MaximumBytes + 1)]));
    }

    [Fact]
    public void Split_RejectsANullSequence()
    {
        Assert.Throws<ArgumentNullException>(() => BatchPlanner.Split(null!));
    }

    [Fact]
    public void Diagnose_ReportsNoCulpritWhenEveryOperationSucceeded()
    {
        var diagnosis = BatchPlanner.Diagnose([200, 201, 200]);

        Assert.Equal(-1, diagnosis.CulpritIndex);
        Assert.Equal(0, diagnosis.Collateral);
    }

    [Fact]
    public void Diagnose_SkipsPastTheFailedDependencies()
    {
        // The shape the companion printed: operation 0 reports 424, operation 1
        // is the real 409.
        var diagnosis = BatchPlanner.Diagnose([424, 409]);

        Assert.Equal(1, diagnosis.CulpritIndex);
        Assert.Equal(409, diagnosis.StatusCode);
    }

    [Fact]
    public void Diagnose_CountsTheOperationsThatWouldHaveWorked()
    {
        var diagnosis = BatchPlanner.Diagnose([424, 424, 424, 409, 424]);

        Assert.Equal(3, diagnosis.CulpritIndex);
        Assert.Equal(4, diagnosis.Collateral);
    }

    [Fact]
    public void Diagnose_FindsACulpritAtTheEnd()
    {
        var codes = Enumerable.Repeat(424, 99).Append(413).ToList();

        var diagnosis = BatchPlanner.Diagnose(codes);

        Assert.Equal(99, diagnosis.CulpritIndex);
        Assert.Equal(413, diagnosis.StatusCode);
    }

    [Fact]
    public void Diagnose_ReportsTheFirstRealFailureWhenThereAreSeveral()
    {
        var diagnosis = BatchPlanner.Diagnose([424, 409, 424, 404]);

        Assert.Equal(1, diagnosis.CulpritIndex);
        Assert.Equal(409, diagnosis.StatusCode);
    }

    [Fact]
    public void Diagnose_DoesNotTreatA201AsAFailure()
    {
        var diagnosis = BatchPlanner.Diagnose([201, 201, 429]);

        Assert.Equal(2, diagnosis.CulpritIndex);
        Assert.Equal(429, diagnosis.StatusCode);
    }

    [Fact]
    public void Diagnose_RejectsANullSequence()
    {
        Assert.Throws<ArgumentNullException>(() => BatchPlanner.Diagnose(null!));
    }
}
