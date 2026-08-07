namespace LearningAzure.Exercises.QueueStorage.Tests;

public sealed class VisibilityPlannerTests
{
    [Fact]
    public void TheChosenTimeoutLeavesHeadroomAboveTheExpectedDuration()
    {
        var chosen = VisibilityPlanner.Choose(TimeSpan.FromSeconds(10));

        Assert.True(
            chosen > TimeSpan.FromSeconds(10),
            $"A timeout of {chosen} gives a 10-second handler no headroom at all.");
    }

    [Fact]
    public void TheChosenTimeoutIsTheExpectedDurationTimesTheSafetyFactor()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), VisibilityPlanner.Choose(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TheSafetyFactorIsMoreThanOne()
    {
        Assert.True(VisibilityPlanner.SafetyFactor > 1.0);
    }

    [Fact]
    public void TheChosenTimeoutIsCappedAtTheServiceMaximum()
    {
        var chosen = VisibilityPlanner.Choose(TimeSpan.FromDays(5));

        Assert.Equal(VisibilityPlanner.MaximumVisibility, chosen);
    }

    [Fact]
    public void TheServiceMaximumIsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), VisibilityPlanner.MaximumVisibility);
    }

    [Fact]
    public void ADurationJustUnderTheCapIsNotCapped()
    {
        var expected = TimeSpan.FromDays(7) / VisibilityPlanner.SafetyFactor;

        Assert.Equal(VisibilityPlanner.MaximumVisibility, VisibilityPlanner.Choose(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveExpectedDurationIsRejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VisibilityPlanner.Choose(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void AHandlerFasterThanItsVisibilityWindowIsNotRedelivered()
    {
        Assert.False(VisibilityPlanner.WillBeRedelivered(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AHandlerSlowerThanItsVisibilityWindowIsRedelivered()
    {
        Assert.True(VisibilityPlanner.WillBeRedelivered(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void AHandlerThatExactlyMeetsItsDeadlineIsStillRedelivered()
    {
        // The message becomes visible at the deadline; the delete happens after
        // the handler returns, which is never earlier than the deadline itself.
        Assert.True(VisibilityPlanner.WillBeRedelivered(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ThePlannersOwnChoiceSurvivesAHandlerAtThreeTimesTheMedian()
    {
        var expected = TimeSpan.FromSeconds(4);
        var chosen = VisibilityPlanner.Choose(expected);

        Assert.False(VisibilityPlanner.WillBeRedelivered(chosen, expected * 2.9));
    }

    [Fact]
    public void ThePlannersOwnChoiceStillLosesToAPathologicalHandler()
    {
        var expected = TimeSpan.FromSeconds(4);
        var chosen = VisibilityPlanner.Choose(expected);

        Assert.True(VisibilityPlanner.WillBeRedelivered(chosen, expected * 10));
    }

    [Fact]
    public void ANonPositiveVisibilityWindowIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VisibilityPlanner.WillBeRedelivered(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ANegativeHandlerDurationIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VisibilityPlanner.WillBeRedelivered(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-1)));
    }
}
