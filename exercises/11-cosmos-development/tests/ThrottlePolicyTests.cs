using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// Checks the retry schedule the emulator can never produce: the local Cosmos
/// container has no rate limiter, so 429 has to be reasoned about rather than
/// observed.
/// </summary>
public sealed class ThrottlePolicyTests
{
    [Fact]
    public void Backoff_StartsAtTheBaseDelay()
    {
        Assert.Equal(ThrottlePolicy.BaseDelay, ThrottlePolicy.Backoff(1));
    }

    [Fact]
    public void Backoff_Doubles()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), ThrottlePolicy.Backoff(2));
        Assert.Equal(TimeSpan.FromMilliseconds(400), ThrottlePolicy.Backoff(3));
        Assert.Equal(TimeSpan.FromMilliseconds(800), ThrottlePolicy.Backoff(4));
    }

    [Fact]
    public void Backoff_GrowsFasterThanLinearly()
    {
        var third = ThrottlePolicy.Backoff(3) - ThrottlePolicy.Backoff(2);
        var second = ThrottlePolicy.Backoff(2) - ThrottlePolicy.Backoff(1);

        Assert.True(third > second, "Backoff has to shed load faster than linearly.");
    }

    [Fact]
    public void Backoff_StopsAtTheCeiling()
    {
        Assert.Equal(ThrottlePolicy.MaximumDelay, ThrottlePolicy.Backoff(20));
        Assert.Equal(ThrottlePolicy.MaximumDelay, ThrottlePolicy.Backoff(60));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Backoff_RejectsANonPositiveAttempt(int attempt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottlePolicy.Backoff(attempt));
    }

    [Fact]
    public void WaitFor_UsesTheClientsCurveWhenTheServiceSaysNothing()
    {
        var step = ThrottlePolicy.WaitFor(Fixtures.Throttled(), attempt: 3);

        Assert.False(step.FromServer);
        Assert.Equal(ThrottlePolicy.Backoff(3), step.Delay);
        Assert.Equal(3, step.Attempt);
    }

    [Fact]
    public void WaitFor_ObeysTheServiceWhenItSaysSomething()
    {
        var step = ThrottlePolicy.WaitFor(Fixtures.ThrottledFor(37), attempt: 1);

        Assert.True(step.FromServer);
        Assert.Equal(TimeSpan.FromMilliseconds(37), step.Delay);
    }

    [Fact]
    public void WaitFor_ObeysTheServiceEvenWhenItAsksForLessThanTheCurve()
    {
        // The service knows when the partition's budget refills. Waiting longer
        // than it asked for is throughput thrown away.
        var step = ThrottlePolicy.WaitFor(Fixtures.ThrottledFor(5), attempt: 6);

        Assert.Equal(TimeSpan.FromMilliseconds(5), step.Delay);
    }

    [Fact]
    public void WaitFor_ObeysTheServiceEvenWhenItAsksForMoreThanTheCeiling()
    {
        // Capping the server's own number produces a retry that arrives before
        // the throttle has lifted, and is throttled again.
        var step = ThrottlePolicy.WaitFor(Fixtures.ThrottledFor(30_000), attempt: 1);

        Assert.Equal(TimeSpan.FromSeconds(30), step.Delay);
        Assert.True(step.Delay > ThrottlePolicy.MaximumDelay);
    }

    [Fact]
    public void WaitFor_RejectsANullResponse()
    {
        Assert.Throws<ArgumentNullException>(() => ThrottlePolicy.WaitFor(null!, 1));
    }

    [Fact]
    public void Plan_WaitsNotAtAllWhenTheFirstAttemptSucceeds()
    {
        var plan = ThrottlePolicy.Plan([Fixtures.Ok()], maximumAttempts: 5, budget: TimeSpan.FromSeconds(10));

        Assert.Empty(plan.Steps);
        Assert.False(plan.Exhausted);
        Assert.Equal(TimeSpan.Zero, plan.TotalDelay);
    }

    [Fact]
    public void Plan_WaitsOnceBetweenTwoAttempts()
    {
        var plan = ThrottlePolicy.Plan(
            [Fixtures.Throttled(), Fixtures.Ok()],
            maximumAttempts: 5,
            budget: TimeSpan.FromSeconds(10));

        Assert.Single(plan.Steps);
        Assert.False(plan.Exhausted);
        Assert.Equal(ThrottlePolicy.BaseDelay, plan.TotalDelay);
    }

    [Fact]
    public void Plan_FollowsTheServicesAdviceForEveryStep()
    {
        var plan = ThrottlePolicy.Plan(
            [Fixtures.ThrottledFor(50), Fixtures.ThrottledFor(75), Fixtures.Ok()],
            maximumAttempts: 5,
            budget: TimeSpan.FromSeconds(10));

        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps, step => Assert.True(step.FromServer));
        Assert.Equal(TimeSpan.FromMilliseconds(125), plan.TotalDelay);
    }

    [Fact]
    public void Plan_StopsWithoutWaitingOnAStatusRetryingCannotFix()
    {
        var plan = ThrottlePolicy.Plan(
            [Fixtures.Throttled(), new ServiceResponse(409, null), Fixtures.Ok()],
            maximumAttempts: 5,
            budget: TimeSpan.FromSeconds(10));

        Assert.Single(plan.Steps);
        Assert.False(plan.Exhausted);
    }

    [Fact]
    public void Plan_StopsAfterTheLastAllowedAttempt()
    {
        // Four attempts allowed means three waits, not four.
        var responses = Enumerable.Repeat(Fixtures.Throttled(), 10).ToList();

        var plan = ThrottlePolicy.Plan(responses, maximumAttempts: 4, budget: TimeSpan.FromMinutes(1));

        Assert.Equal(3, plan.Steps.Count);
        Assert.True(plan.Exhausted);
    }

    [Fact]
    public void Plan_StopsBeforeTheWaitThatWouldBreachTheDeadline()
    {
        // 100 + 200 + 400 = 700 ms fits; the next wait of 800 ms does not.
        var responses = Enumerable.Repeat(Fixtures.Throttled(), 10).ToList();

        var plan = ThrottlePolicy.Plan(responses, maximumAttempts: 20, budget: TimeSpan.FromMilliseconds(1000));

        Assert.Equal(3, plan.Steps.Count);
        Assert.True(plan.Exhausted);
        Assert.Equal(TimeSpan.FromMilliseconds(700), plan.TotalDelay);
    }

    [Fact]
    public void Plan_NeverWaitsLongerThanTheBudget()
    {
        var responses = Enumerable.Repeat(Fixtures.ThrottledFor(400), 50).ToList();
        var budget = TimeSpan.FromMilliseconds(1000);

        var plan = ThrottlePolicy.Plan(responses, maximumAttempts: 50, budget: budget);

        Assert.True(plan.TotalDelay <= budget);
        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void Plan_RefusesEvenTheFirstWaitWhenTheDeadlineIsAlreadyTooClose()
    {
        var plan = ThrottlePolicy.Plan(
            [Fixtures.ThrottledFor(5_000)],
            maximumAttempts: 5,
            budget: TimeSpan.FromMilliseconds(100));

        Assert.Empty(plan.Steps);
        Assert.True(plan.Exhausted);
    }

    [Fact]
    public void Plan_NumbersItsStepsFromOne()
    {
        var responses = Enumerable.Repeat(Fixtures.Throttled(), 4).ToList();

        var plan = ThrottlePolicy.Plan(responses, maximumAttempts: 4, budget: TimeSpan.FromMinutes(1));

        Assert.Equal([1, 2, 3], plan.Steps.Select(step => step.Attempt));
    }

    [Fact]
    public void Plan_RejectsANullResponseSequence()
    {
        Assert.Throws<ArgumentNullException>(
            () => ThrottlePolicy.Plan(null!, 3, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Plan_RejectsANonPositiveBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThrottlePolicy.Plan([Fixtures.Throttled()], 3, TimeSpan.Zero));
    }

    [Fact]
    public void Plan_RejectsANonPositiveAttemptLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThrottlePolicy.Plan([Fixtures.Throttled()], 0, TimeSpan.FromSeconds(1)));
    }
}
