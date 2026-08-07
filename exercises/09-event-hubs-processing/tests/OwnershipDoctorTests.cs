namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>
/// The diagnosis has to be ordered, because a rebalancing cluster and an
/// over-provisioned one look alike in a single snapshot and the remedies are
/// opposite.
/// </summary>
public sealed class OwnershipDoctorTests
{
    private static OwnershipSnapshot Snapshot(
        int partitionCount,
        int processorCount,
        Dictionary<string, IReadOnlyList<string>> owned,
        int changes = 0) =>
        new(partitionCount, processorCount, owned, changes);

    [Fact]
    public void ASnapshotIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => OwnershipDoctor.Diagnose(null!));
    }

    [Fact]
    public void AHubWithNoPartitionsIsNotAThing()
    {
        var snapshot = Snapshot(0, 1, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        Assert.Throws<ArgumentOutOfRangeException>(() => OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void EveryPartitionOwnedAndNoProcessorSpareIsBalanced()
    {
        var snapshot = Snapshot(4, 2, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["a"] = ["0", "1"],
            ["b"] = ["2", "3"],
        });

        Assert.Equal(OwnershipVerdict.Balanced, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void OneProcessorOwningEverythingIsBalanced()
    {
        var snapshot = Snapshot(4, 1, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["a"] = ["0", "1", "2", "3"],
        });

        Assert.Equal(OwnershipVerdict.Balanced, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void AnUnownedPartitionOutranksEverything()
    {
        var snapshot = Snapshot(4, 8, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["a"] = ["0", "1"],
            ["b"] = ["2"],
        });

        Assert.Equal(OwnershipVerdict.UnownedPartitions, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void NoProcessorsAtAllIsAnUnownedPartitionProblem()
    {
        var snapshot = Snapshot(4, 0, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        Assert.Equal(OwnershipVerdict.UnownedPartitions, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void ThrashingIsCheckedBeforeTheSnapshotIsBelieved()
    {
        var snapshot = Snapshot(
            4,
            8,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["a"] = ["0"] },
            changes: 40);

        Assert.Equal(OwnershipVerdict.Thrashing, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void ThrashingOutranksIdleProcessors()
    {
        var snapshot = Snapshot(
            2,
            9,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["a"] = ["0", "1"] },
            changes: OwnershipDoctor.ThrashingThreshold);

        Assert.Equal(OwnershipVerdict.Thrashing, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void SomeRebalancingIsNotThrashing()
    {
        var snapshot = Snapshot(
            2,
            2,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["a"] = ["0"],
                ["b"] = ["1"],
            },
            changes: OwnershipDoctor.ThrashingThreshold - 1);

        Assert.Equal(OwnershipVerdict.Balanced, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void MoreProcessorsThanPartitionsIsWastedMoney()
    {
        var snapshot = Snapshot(2, 5, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["a"] = ["0"],
            ["b"] = ["1"],
        });

        Assert.Equal(OwnershipVerdict.IdleProcessors, OwnershipDoctor.Diagnose(snapshot));
    }

    [Fact]
    public void TheSamePartitionOwnedTwiceIsStillOnePartition()
    {
        var snapshot = Snapshot(2, 2, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["a"] = ["0"],
            ["b"] = ["0"],
        });

        Assert.Equal(OwnershipVerdict.UnownedPartitions, OwnershipDoctor.Diagnose(snapshot));
    }
}
