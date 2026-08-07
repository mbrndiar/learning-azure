using System.Text;

namespace LearningAzure.Exercises.BlobLifecycle.Tests;

/// <summary>Asserts the read-modify-write loop under a competing writer.</summary>
public sealed class ArtifactUpdaterTests
{
    private static byte[] Append(byte[] current) => [.. current, .. "+"u8];

    private static RacingStore Store(int stealCount) =>
        new(Encoding.UTF8.GetBytes("base"), stealCount);

    [Fact]
    public async Task AnUncontendedUpdateCostsOneAttempt()
    {
        var store = Store(stealCount: 0);

        var attempts = await ArtifactUpdater.UpdateAsync(
            store, "note.txt", Append, ArtifactUpdater.DefaultMaxAttempts, TestContext.Current.CancellationToken);

        Assert.Equal(1, attempts);
        Assert.Equal("base+", Encoding.UTF8.GetString(store.Content));
    }

    [Fact]
    public async Task AContendedUpdateRetriesAndStillLands()
    {
        var store = Store(stealCount: 2);

        var attempts = await ArtifactUpdater.UpdateAsync(
            store, "note.txt", Append, ArtifactUpdater.DefaultMaxAttempts, TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task EveryRetryReReadsBeforeWriting()
    {
        // This is the whole test. An implementation that hoists the read out of
        // the loop bets the same stale ETag every time; one that re-reads bets a
        // fresh one, and the sequence of bets is observable.
        var store = Store(stealCount: 2);

        await ArtifactUpdater.UpdateAsync(
            store, "note.txt", Append, ArtifactUpdater.DefaultMaxAttempts, TestContext.Current.CancellationToken);

        Assert.Equal(3, store.Reads.Count);
        Assert.Equal(3, store.Writes.Count);
        Assert.Equal(store.Reads, store.Writes);
        Assert.Equal(store.Writes.Distinct(StringComparer.Ordinal).Count(), store.Writes.Count);
    }

    [Fact]
    public async Task TheChangeIsAppliedToTheFreshlyReadBytesNotTheStaleOnes()
    {
        // Applying the change to the copy read before the loop silently discards
        // the competing writer's work even though the write itself was conditional.
        var store = Store(stealCount: 1);

        await ArtifactUpdater.UpdateAsync(
            store, "note.txt", Append, ArtifactUpdater.DefaultMaxAttempts, TestContext.Current.CancellationToken);

        Assert.Equal("base+", Encoding.UTF8.GetString(store.Content));
        Assert.Equal(2, store.Reads.Count);
    }

    [Fact]
    public async Task SustainedContentionFailsLoudlyRatherThanLooping()
    {
        var store = Store(stealCount: 100);

        var error = await Assert.ThrowsAsync<ConcurrencyExhaustedException>(
            () => ArtifactUpdater.UpdateAsync(
                store, "note.txt", Append, 4, TestContext.Current.CancellationToken));

        Assert.Equal(4, error.Attempts);
        Assert.Equal("note.txt", error.ArtifactName);
    }

    [Fact]
    public async Task TheAttemptBudgetIsExactlyRespected()
    {
        var store = Store(stealCount: 100);

        await Assert.ThrowsAsync<ConcurrencyExhaustedException>(
            () => ArtifactUpdater.UpdateAsync(
                store, "note.txt", Append, 3, TestContext.Current.CancellationToken));

        Assert.Equal(3, store.Writes.Count);
    }

    [Fact]
    public async Task ExhaustionLeavesTheArtifactUntouched()
    {
        // Giving up must not be a partial write. The failed update wrote nothing,
        // which is why an operator can safely re-run it.
        var store = Store(stealCount: 100);

        await Assert.ThrowsAsync<ConcurrencyExhaustedException>(
            () => ArtifactUpdater.UpdateAsync(
                store, "note.txt", Append, 2, TestContext.Current.CancellationToken));

        Assert.Equal("base", Encoding.UTF8.GetString(store.Content));
    }

    [Fact]
    public async Task TheExhaustionMessageNamesTheArtifactAndTheBudget()
    {
        var store = Store(stealCount: 100);

        var error = await Assert.ThrowsAsync<ConcurrencyExhaustedException>(
            () => ArtifactUpdater.UpdateAsync(
                store, "note.txt", Append, 2, TestContext.Current.CancellationToken));

        Assert.Contains("note.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingArtifactIsNotRetried()
    {
        // Retrying a read that returns nothing is a loop with no exit. The
        // caller wanted an update and there is nothing to update.
        var store = Store(stealCount: 0);
        store.Absent = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArtifactUpdater.UpdateAsync(
                store, "note.txt", Append, ArtifactUpdater.DefaultMaxAttempts, TestContext.Current.CancellationToken));

        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task ACancelledTokenStopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = Store(stealCount: 100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ArtifactUpdater.UpdateAsync(store, "note.txt", Append, 5, cts.Token));

        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task ANullStoreIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ArtifactUpdater.UpdateAsync(null!, "note.txt", Append, 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANullChangeIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ArtifactUpdater.UpdateAsync(Store(0), "note.txt", null!, 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABlankNameIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArtifactUpdater.UpdateAsync(Store(0), " ", Append, 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AZeroAttemptBudgetIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ArtifactUpdater.UpdateAsync(Store(0), "note.txt", Append, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheDefaultAttemptBudgetIsSmall()
    {
        // A large budget under contention is a long hang. Five is enough for
        // real contention and short enough to surface a design problem.
        Assert.InRange(ArtifactUpdater.DefaultMaxAttempts, 2, 10);
    }
}
