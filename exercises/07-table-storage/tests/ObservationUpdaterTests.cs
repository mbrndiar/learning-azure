using Azure;

namespace LearningAzure.Exercises.TableStorage.Tests;

public sealed class ObservationUpdaterTests
{
    private const string Partition = "station-bravo|2026-07-06";
    private const string Row = "2026-07-06T12:00:00.0000000Z";

    private static ObservationEntity Seeded() => new()
    {
        PartitionKey = Partition,
        RowKey = Row,
        StationId = "station-bravo",
        ObservedAt = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
        TemperatureC = -3.0,
        Status = "pending",
    };

    private static (RacingTable Table, ObservationUpdater Updater) Fresh()
    {
        var table = new RacingTable();
        table.Seed(Seeded());
        return (table, new ObservationUpdater(table));
    }

    [Fact]
    public async Task AnUncontendedUpdateIsApplied()
    {
        var (_, updater) = Fresh();

        var outcome = await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Applied, outcome);
    }

    [Fact]
    public async Task AnAppliedUpdateChangesTheStoredEntity()
    {
        var (table, updater) = Fresh();

        await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal("ingested", table.Peek(Partition, Row)!.Status);
    }

    [Fact]
    public async Task AnAppliedUpdateAdvancesTheStoredEtag()
    {
        var (table, updater) = Fresh();
        var before = table.Peek(Partition, Row)!.ETag;

        await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(before, table.Peek(Partition, Row)!.ETag);
    }

    [Fact]
    public async Task TheWriteBetsOnTheEtagThatTheReadReturned()
    {
        var (table, updater) = Fresh();
        var read = table.Peek(Partition, Row)!.ETag;

        await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal(read.ToString(), Assert.Single(table.EtagsBetOn));
    }

    [Fact]
    public async Task TheWriteNeverBetsOnEtagAll()
    {
        var (table, updater) = Fresh();

        await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(ETag.All.ToString(), table.EtagsBetOn, StringComparer.Ordinal);
    }

    [Fact]
    public async Task AContendedUpdateIsReportedAsStale()
    {
        var (table, updater) = Fresh();
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -9.0);

        var outcome = await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Stale, outcome);
    }

    [Fact]
    public async Task ALostUpdateIsNotSilentlyOverwritten()
    {
        var (table, updater) = Fresh();
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -9.0);

        await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal(-9.0, table.Peek(Partition, Row)!.TemperatureC);
    }

    [Fact]
    public async Task AMissingEntityIsReportedAsMissingNotStale()
    {
        var updater = new ObservationUpdater(new RacingTable());

        var outcome = await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Missing, outcome);
    }

    [Fact]
    public async Task AMissingEntityIsNotWrittenTo()
    {
        var table = new RacingTable();
        var updater = new ObservationUpdater(table);

        await updater.TryUpdateAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, table.Writes);
    }

    [Fact]
    public async Task ARetriedUpdateEventuallyLands()
    {
        var (table, updater) = Fresh();
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -9.0);

        var outcome = await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Applied, outcome);
    }

    [Fact]
    public async Task ARetriedUpdatePreservesTheCompetitorsChange()
    {
        var (table, updater) = Fresh();
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -9.0);

        await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            5,
            TestContext.Current.CancellationToken);

        var stored = table.Peek(Partition, Row)!;
        Assert.Equal("ingested", stored.Status);
        Assert.Equal(-9.0, stored.TemperatureC);
    }

    [Fact]
    public async Task EveryRetryReReadsBeforeWriting()
    {
        var (table, updater) = Fresh();
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -9.0);
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -11.0);

        await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(table.Writes, table.Reads);
    }

    [Fact]
    public async Task NoTwoAttemptsBetOnTheSameEtag()
    {
        // A retry that re-sends the stale ETag fails identically forever. The
        // only way the sequence can be all-distinct is a re-read per attempt.
        var (table, updater) = Fresh();
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -9.0);
        table.CompetingWrites.Enqueue(entity => entity.TemperatureC = -11.0);

        await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(table.EtagsBetOn.Count, table.EtagsBetOn.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ARelentlessCompetitorExhaustsTheAttemptBudget()
    {
        var (table, updater) = Fresh();

        for (var n = 0; n < 10; n++)
        {
            table.CompetingWrites.Enqueue(entity => entity.TemperatureC -= 1.0);
        }

        var outcome = await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Stale, outcome);
    }

    [Fact]
    public async Task TheAttemptBudgetIsRespectedExactly()
    {
        var (table, updater) = Fresh();

        for (var n = 0; n < 10; n++)
        {
            table.CompetingWrites.Enqueue(entity => entity.TemperatureC -= 1.0);
        }

        await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, table.Writes);
    }

    [Fact]
    public async Task AMissingEntityIsNotRetried()
    {
        var table = new RacingTable();
        var updater = new ObservationUpdater(table);

        var outcome = await updater.UpdateWithRetryAsync(
            Partition,
            Row,
            entity => entity.Status = "ingested",
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Missing, outcome);
        Assert.Equal(1, table.Reads);
    }

    [Fact]
    public async Task AnAttemptBudgetBelowOneIsRejected()
    {
        var (_, updater) = Fresh();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => updater.UpdateWithRetryAsync(
                Partition,
                Row,
                entity => entity.Status = "ingested",
                0,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnUpdaterRequiresATable()
    {
        Assert.Throws<ArgumentNullException>(() => new ObservationUpdater(null!));
    }

    [Fact]
    public async Task ANullChangeIsRejected()
    {
        var (_, updater) = Fresh();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => updater.TryUpdateAsync(Partition, Row, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyPartitionKeyIsRejected()
    {
        var (_, updater) = Fresh();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => updater.TryUpdateAsync("  ", Row, entity => entity.Status = "x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyRowKeyIsRejected()
    {
        var (_, updater) = Fresh();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => updater.TryUpdateAsync(Partition, "  ", entity => entity.Status = "x", TestContext.Current.CancellationToken));
    }
}
