using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// Checks that removing data picks the cheapest mechanism that is still
/// correct, because Cosmos charges for every document you delete by hand.
/// </summary>
public sealed class CleanupPlannerTests
{
    [Fact]
    public void RequestUnitsFor_ChargesNothingForNothing()
    {
        Assert.Equal(0, CleanupPlanner.RequestUnitsFor(0));
    }

    [Fact]
    public void RequestUnitsFor_ChargesTheQueryThatFindsTheDocuments()
    {
        Assert.Equal(
            CleanupPlanner.QueryOverheadRequestUnits + CleanupPlanner.PerDocumentRequestUnits,
            CleanupPlanner.RequestUnitsFor(1));
    }

    [Fact]
    public void RequestUnitsFor_ScalesWithTheNumberOfDocuments()
    {
        Assert.Equal(
            CleanupPlanner.QueryOverheadRequestUnits + (1000 * CleanupPlanner.PerDocumentRequestUnits),
            CleanupPlanner.RequestUnitsFor(1000));
    }

    [Fact]
    public void RequestUnitsFor_RejectsANegativeCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupPlanner.RequestUnitsFor(-1));
    }

    [Fact]
    public void Plan_DeletesTheWholeContainerWhenEverythingIsGoing()
    {
        var plan = CleanupPlanner.Plan(
            totalDocuments: 1_000_000,
            documentsToRemove: 1_000_000,
            containerIsDisposable: true,
            expiryIsPredictable: false);

        Assert.Equal(CleanupStrategy.DeleteContainer, plan.Strategy);
        Assert.Equal(0, plan.RequestUnits);
    }

    [Fact]
    public void Plan_PrefersTheContainerDeleteEvenWhenTimeToLiveWouldAlsoWork()
    {
        // Free and instantaneous beats free and eventual.
        var plan = CleanupPlanner.Plan(500, 500, containerIsDisposable: true, expiryIsPredictable: true);

        Assert.Equal(CleanupStrategy.DeleteContainer, plan.Strategy);
    }

    [Fact]
    public void Plan_WillNotDeleteAContainerSomethingElseIsUsing()
    {
        var plan = CleanupPlanner.Plan(500, 500, containerIsDisposable: false, expiryIsPredictable: false);

        Assert.NotEqual(CleanupStrategy.DeleteContainer, plan.Strategy);
    }

    [Fact]
    public void Plan_WillNotDeleteAContainerToRemovePartOfIt()
    {
        var plan = CleanupPlanner.Plan(500, 499, containerIsDisposable: true, expiryIsPredictable: false);

        Assert.NotEqual(CleanupStrategy.DeleteContainer, plan.Strategy);
    }

    [Fact]
    public void Plan_LetsTheServiceExpireDocumentsWithAKnownLifetime()
    {
        var plan = CleanupPlanner.Plan(500, 300, containerIsDisposable: false, expiryIsPredictable: true);

        Assert.Equal(CleanupStrategy.TimeToLive, plan.Strategy);
        Assert.Equal(0, plan.RequestUnits);
    }

    [Fact]
    public void Plan_FallsBackToDeletingOneAtATime()
    {
        var plan = CleanupPlanner.Plan(500, 300, containerIsDisposable: false, expiryIsPredictable: false);

        Assert.Equal(CleanupStrategy.DeletePerDocument, plan.Strategy);
        Assert.Equal(CleanupPlanner.RequestUnitsFor(300), plan.RequestUnits);
    }

    [Fact]
    public void Plan_ShowsWhatTheFallbackCosts()
    {
        // A hundred thousand documents removed by hand is half a million RU,
        // which is a bill and a throttling incident at the same time.
        var plan = CleanupPlanner.Plan(200_000, 100_000, containerIsDisposable: false, expiryIsPredictable: false);

        Assert.True(plan.RequestUnits > 500_000);
    }

    [Fact]
    public void Plan_ExplainsItself()
    {
        var plan = CleanupPlanner.Plan(10, 10, containerIsDisposable: true, expiryIsPredictable: false);

        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void Plan_RejectsRemovingMoreThanExists()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CleanupPlanner.Plan(10, 11, containerIsDisposable: true, expiryIsPredictable: false));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10, -1)]
    public void Plan_RejectsNegativeCounts(int total, int toRemove)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CleanupPlanner.Plan(total, toRemove, containerIsDisposable: false, expiryIsPredictable: false));
    }
}
