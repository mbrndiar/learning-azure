using LearningAzure.Exercises.CosmosModeling;

namespace LearningAzure.Exercises.CosmosModeling.Tests;

/// <summary>
/// Checks the asymmetry between what an index costs to keep and what it saves.
/// </summary>
public sealed class IndexingPlannerTests
{
    [Fact]
    public void AnUnindexedWriteIsTheBaseline() =>
        Assert.Equal(5.0, IndexingPlanner.WriteCost(Fixtures.Policy(0)), 6);

    [Fact]
    public void EveryIndexedPathAddsToEveryWrite() =>
        Assert.Equal(
            IndexingPlanner.BaseWriteRequestUnits + (20 * IndexingPlanner.PerIndexedPathRequestUnits),
            IndexingPlanner.WriteCost(Fixtures.Policy(20)),
            6);

    [Fact]
    public void ACompositeIndexCostsMoreThanASinglePath()
    {
        var single = IndexingPlanner.WriteCost(Fixtures.Policy(10));
        var composite = IndexingPlanner.WriteCost(Fixtures.PolicyWithComposite(10, "day", "celsius"));

        Assert.True(composite > single + IndexingPlanner.PerIndexedPathRequestUnits);
    }

    [Fact]
    public void TheDefaultPolicyIndexesEverythingAndChargesForIt()
    {
        // A document with forty properties, all indexed by default, pays for
        // forty indexes on every write whether or not anything queries them.
        var wide = IndexingPlanner.WriteCost(Fixtures.Policy(40));
        var narrow = IndexingPlanner.WriteCost(Fixtures.Policy(3));

        Assert.True(wide > narrow * 1.5);
    }

    [Fact]
    public void WriteCostRefusesANullPolicy() =>
        Assert.Throws<ArgumentNullException>(() => IndexingPlanner.WriteCost(null!));

    [Fact]
    public void ExcludingPathsGivesBudgetBack()
    {
        var saved = IndexingPlanner.SavingsPerSecond(
            Fixtures.Policy(40),
            Fixtures.Policy(4),
            writesPerSecond: 1_000);

        // 36 paths x 0.15 RU x 1,000 writes/s.
        Assert.Equal(5_400.0, saved, 6);
    }

    [Fact]
    public void AddingAnIndexIsReportedAsANegativeSaving()
    {
        var saved = IndexingPlanner.SavingsPerSecond(
            Fixtures.Policy(10),
            Fixtures.PolicyWithComposite(10, "day", "celsius"),
            writesPerSecond: 100);

        Assert.True(saved < 0);
        Assert.Equal(-40.0, saved, 6);
    }

    [Fact]
    public void AChangeThatChangesNothingSavesNothing() =>
        Assert.Equal(
            0.0,
            IndexingPlanner.SavingsPerSecond(Fixtures.Policy(12), Fixtures.Policy(12), 500),
            6);

    [Fact]
    public void SavingScalesWithWriteRate()
    {
        var slow = IndexingPlanner.SavingsPerSecond(Fixtures.Policy(40), Fixtures.Policy(4), 10);
        var fast = IndexingPlanner.SavingsPerSecond(Fixtures.Policy(40), Fixtures.Policy(4), 1_000);

        Assert.Equal(slow * 100, fast, 6);
    }

    [Fact]
    public void AContainerThatIsNeverWrittenToSavesNothingByReindexing() =>
        Assert.Equal(
            0.0,
            IndexingPlanner.SavingsPerSecond(Fixtures.Policy(40), Fixtures.Policy(1), 0),
            6);

    [Fact]
    public void SavingsRefuseANegativeWriteRate() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IndexingPlanner.SavingsPerSecond(Fixtures.Policy(4), Fixtures.Policy(2), -1));

    [Fact]
    public void SavingsRefuseANullPolicy()
    {
        Assert.Throws<ArgumentNullException>(
            () => IndexingPlanner.SavingsPerSecond(null!, Fixtures.Policy(2), 10));

        Assert.Throws<ArgumentNullException>(
            () => IndexingPlanner.SavingsPerSecond(Fixtures.Policy(2), null!, 10));
    }

    [Fact]
    public void ASinglePropertyOrderByNeedsNoCompositeIndex() =>
        Assert.False(IndexingPlanner.RequiresMissingCompositeIndex(Fixtures.Policy(10), ["day"]));

    [Fact]
    public void AQueryWithNoOrderByNeedsNoCompositeIndex() =>
        Assert.False(IndexingPlanner.RequiresMissingCompositeIndex(Fixtures.Policy(10), []));

    [Fact]
    public void ATwoPropertyOrderByWithoutACompositeIndexIsRefused() =>
        Assert.True(
            IndexingPlanner.RequiresMissingCompositeIndex(
                Fixtures.Policy(10),
                ["day", "celsius"]));

    [Fact]
    public void AMatchingCompositeIndexSatisfiesTheQuery() =>
        Assert.False(
            IndexingPlanner.RequiresMissingCompositeIndex(
                Fixtures.PolicyWithComposite(10, "day", "celsius"),
                ["day", "celsius"]));

    [Fact]
    public void TheOrderOfACompositeIndexIsPartOfTheMatch()
    {
        // (day, celsius) does not serve ORDER BY celsius, day.
        Assert.True(
            IndexingPlanner.RequiresMissingCompositeIndex(
                Fixtures.PolicyWithComposite(10, "day", "celsius"),
                ["celsius", "day"]));
    }

    [Fact]
    public void APrefixOfACompositeIndexIsNotAMatchForALongerQuery() =>
        Assert.True(
            IndexingPlanner.RequiresMissingCompositeIndex(
                Fixtures.PolicyWithComposite(10, "day", "celsius"),
                ["day", "celsius", "stationId"]));

    [Fact]
    public void OneOfSeveralCompositeIndexesIsEnough()
    {
        var policy = new IndexingPolicy(
            10,
            new IReadOnlyList<string>[]
            {
                ["stationId", "day"],
                ["day", "celsius"],
            });

        Assert.False(IndexingPlanner.RequiresMissingCompositeIndex(policy, ["day", "celsius"]));
    }

    [Fact]
    public void CompositeIndexMatchingIsCaseSensitiveBecauseJsonIs() =>
        Assert.True(
            IndexingPlanner.RequiresMissingCompositeIndex(
                Fixtures.PolicyWithComposite(10, "day", "celsius"),
                ["Day", "Celsius"]));

    [Fact]
    public void CompositeIndexMatchingRefusesNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => IndexingPlanner.RequiresMissingCompositeIndex(null!, ["a", "b"]));

        Assert.Throws<ArgumentNullException>(
            () => IndexingPlanner.RequiresMissingCompositeIndex(Fixtures.Policy(1), null!));
    }
}
