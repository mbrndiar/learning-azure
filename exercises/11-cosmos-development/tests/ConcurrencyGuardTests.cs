using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// Checks that a concurrent change is noticed rather than overwritten, and that
/// noticing it does not turn into an unbounded loop.
/// </summary>
public sealed class ConcurrencyGuardTests
{
    [Theory]
    [InlineData(412)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(408)]
    public void ShouldRetry_AcceptsStatusesALaterAttemptCouldChange(int statusCode)
    {
        Assert.True(ConcurrencyGuard.ShouldRetry(statusCode));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public void ShouldRetry_RefusesStatusesThatWillNotChange(int statusCode)
    {
        Assert.False(ConcurrencyGuard.ShouldRetry(statusCode));
    }

    [Fact]
    public void Apply_CommitsOnTheFirstAttemptWhenNobodyIsCompeting()
    {
        var store = new RacingStore(interferences: 0);

        var result = ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 5);

        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(ConcurrencyGuard.Ok, result.StatusCode);
    }

    [Fact]
    public void Apply_SucceedsOnceTheCompetingWriterStops()
    {
        var store = new RacingStore(interferences: 2);

        var result = ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 5);

        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public void Apply_ReReadsBeforeEveryAttempt()
    {
        // Three attempts, three reads. Reading once and retrying the same
        // proposed document sends the same stale ETag forever.
        var store = new RacingStore(interferences: 2);

        ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 5);

        Assert.Equal(3, store.Reads);
        Assert.Equal(3, store.Writes);
    }

    [Fact]
    public void Apply_KeepsTheCompetingWritersChanges()
    {
        // Two competitors each added a correction; the caller's intent adds a
        // third. A blind write would have left the counter at 1.
        var store = new RacingStore(interferences: 2);

        ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 5);

        Assert.Equal(3, store.Current.Corrections);
    }

    [Fact]
    public void Apply_AppliesTheIntentToTheFreshDocumentNotTheStaleOne()
    {
        var store = new RacingStore(interferences: 1);

        ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 5);

        Assert.Equal(2, store.Current.Corrections);
    }

    [Fact]
    public void Apply_GivesUpWhenTheBudgetRunsOut()
    {
        var store = new RacingStore(interferences: 100);

        var result = ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 3);

        Assert.Equal(WriteOutcome.Exhausted, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(ConcurrencyGuard.PreconditionFailed, result.StatusCode);
    }

    [Fact]
    public void Apply_DoesNotWriteMoreOftenThanItsBudget()
    {
        var store = new RacingStore(interferences: 100);

        ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 3);

        Assert.Equal(3, store.Writes);
    }

    [Fact]
    public void Apply_StopsImmediatelyOnAStatusRetryingCannotFix()
    {
        var store = new RacingStore(interferences: 0, refuseWith: 404);

        var result = ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 5);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void Apply_ReportsNoETagWhenNothingWasWritten()
    {
        var store = new RacingStore(interferences: 100);

        Assert.Null(ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 2).ETag);
    }

    [Fact]
    public void Apply_RetriesAThrottledWrite()
    {
        var store = new RacingStore(interferences: 0, refuseWith: ConcurrencyGuard.TooManyRequests);

        var result = ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 4);

        Assert.Equal(WriteOutcome.Exhausted, result.Outcome);
        Assert.Equal(4, store.Writes);
    }

    [Fact]
    public void Apply_RejectsANullStore()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConcurrencyGuard.Apply(null!, "reading-1", Fixtures.AddCorrection, 3));
    }

    [Fact]
    public void Apply_RejectsANullChange()
    {
        var store = new RacingStore(interferences: 0);

        Assert.Throws<ArgumentNullException>(
            () => ConcurrencyGuard.Apply(store, "reading-1", null!, 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_RejectsABlankId(string id)
    {
        var store = new RacingStore(interferences: 0);

        Assert.Throws<ArgumentException>(
            () => ConcurrencyGuard.Apply(store, id, Fixtures.AddCorrection, 3));
    }

    [Fact]
    public void Apply_RejectsAnUnboundedBudget()
    {
        var store = new RacingStore(interferences: 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ConcurrencyGuard.Apply(store, "reading-1", Fixtures.AddCorrection, 0));
    }
}
