namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>
/// Milestone 4 — the Cosmos projection. Judges whether a replay is absorbed,
/// whether a lost race is re-decided rather than re-sent, whether a throttle is
/// waited out and charged for, and whether a page really is read to its end.
/// </summary>
[Trait("Milestone", "cosmos-projection")]
public sealed class CosmosProjectionTests
{
    [Fact]
    public async Task AnEntryIsWrittenOnceAndItsReplayIsAbsorbed()
    {
        var journal = new Journal();
        var entry = Fixture.Entry(sequenceNumber: 4);

        var first = await journal.Projector.ProjectAsync(entry, null, TestContext.Current.CancellationToken);
        var replay = await journal.Projector.ProjectAsync(entry, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Written);
        Assert.Equal(0, replay.Written);
        Assert.Equal(1, replay.Superseded);
        Assert.Equal(1, journal.Projection.Writes);
    }

    [Fact]
    public async Task ALaterPositionOvertakesAnEarlierOne()
    {
        var journal = new Journal();
        await journal.Projector.ProjectAsync(
            Fixture.Entry(sequenceNumber: 1, celsius: -1),
            null,
            TestContext.Current.CancellationToken);

        var report = await journal.Projector.ProjectAsync(
            Fixture.Entry(sequenceNumber: 2, celsius: -2),
            null,
            TestContext.Current.CancellationToken);

        var stored = Assert.Single(journal.Projection.Entries);

        Assert.Equal(1, report.Written);
        Assert.Equal(2, stored.SequenceNumber);
        Assert.Equal(-2, stored.Celsius);
    }

    [Fact]
    public async Task AnOutOfOrderEventNeverRewindsTheStoredEntry()
    {
        // Late delivery is normal. A projection that takes the last write it saw
        // rather than the furthest position it saw goes backwards under load.
        var journal = new Journal();
        await journal.Projector.ProjectAsync(
            Fixture.Entry(sequenceNumber: 9, celsius: -9),
            null,
            TestContext.Current.CancellationToken);

        var late = await journal.Projector.ProjectAsync(
            Fixture.Entry(sequenceNumber: 3, celsius: -3),
            null,
            TestContext.Current.CancellationToken);

        var stored = Assert.Single(journal.Projection.Entries);

        Assert.Equal(1, late.Superseded);
        Assert.Equal(9, stored.SequenceNumber);
        Assert.Equal(-9, stored.Celsius);
    }

    [Fact]
    public async Task ALostRaceIsReReadAndReDecidedNotResentUnderAFreshETag()
    {
        // The competitor lands a LATER position while this caller holds a stale
        // ETag. Re-sending the original body under a new version is the lost
        // update the ETag existed to prevent.
        var journal = new Journal();
        var seeded = journal.Projection.Seed(Fixture.Entry(sequenceNumber: 1));
        journal.Projection.StealRace(seeded.StationId, seeded.Id, sequenceNumber: 7);

        var report = await journal.Projector.ProjectAsync(
            Fixture.Entry(sequenceNumber: 2),
            null,
            TestContext.Current.CancellationToken);

        var stored = Assert.Single(journal.Projection.Entries);

        Assert.Equal(1, report.Superseded);
        Assert.Equal(0, report.Written);
        Assert.Equal(7, stored.SequenceNumber);
    }

    [Fact]
    public async Task AThrottleIsWaitedOutAndTheRefusedAttemptIsStillCharged()
    {
        // Every attempt is billed, including the ones that lose. A projector that
        // counts only successes reports a cost the invoice will disagree with.
        var journal = new Journal();
        journal.Projection.ThrottleCharge = 2.5;
        journal.Projection.ChargePerOperation = 5.0;
        journal.Projection.ThrottleNext(2);

        var waits = new List<TimeSpan>();
        var report = await journal.Projector.ProjectAsync(
            Fixture.Entry(),
            (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Written);
        Assert.Equal(2, report.ThrottleRetries);
        Assert.Equal(2, waits.Count);
        Assert.All(waits, wait => Assert.True(wait > TimeSpan.Zero));

        // Two refused attempts at 2.5 each, plus the write that finally landed.
        Assert.Equal(10.0, report.RequestCharge);
    }

    [Fact]
    public async Task AThrottleBudgetIsBoundedRatherThanInfinite()
    {
        // Retrying forever against a rate-limited container is a workload that
        // never finishes and never stops spending.
        var journal = new Journal();
        var projector = new JournalProjector(journal.Projection, maxThrottleRetries: 1);
        journal.Projection.ThrottleNext(5);

        await Assert.ThrowsAsync<ThrottledException>(() =>
            projector.ProjectAsync(Fixture.Entry(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APermanentlyContendedDocumentFailsInsteadOfLoopingForever()
    {
        // Every read reports an earlier position and every write loses the race,
        // so the loop can never converge. Spinning on it is an outage that
        // presents as a hang.
        var contended = new JournalProjector(new ContendedProjection(), maxConcurrencyRetries: 2);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            contended.ProjectAsync(Fixture.Entry(sequenceNumber: 5), null, TestContext.Current.CancellationToken));

        Assert.Contains("contended", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStationIsReadToTheEndOfItsContinuationToken()
    {
        var journal = new Journal();
        for (var index = 0; index < 5; index++)
        {
            journal.Projection.Seed(Fixture.Entry($"obs-{index:0000}", index));
        }

        var (entries, charge, pages) = await journal.Projector.ReadStationAsync(
            Fixture.Station,
            pageSize: 2,
            delay: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, entries.Count);
        Assert.Equal(3, pages);
        Assert.True(charge > 0);
    }

    [Fact]
    public async Task AShortPageIsNotMistakenForTheEndOfTheResults()
    {
        // Cosmos may cut a page short at a size or time budget and still have more
        // to give. A reader that stops on a short page truncates its answer
        // silently, and only under load.
        var journal = new Journal();
        journal.Projection.ShortPages = true;
        for (var index = 0; index < 6; index++)
        {
            journal.Projection.Seed(Fixture.Entry($"obs-{index:0000}", index));
        }

        var (entries, _, _) = await journal.Projector.ReadStationAsync(
            Fixture.Station,
            pageSize: 4,
            delay: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(6, entries.Count);
    }

    [Fact]
    public async Task AQueryStaysInsideItsOwnPartition()
    {
        var journal = new Journal();
        journal.Projection.Seed(Fixture.Entry("obs-0001", 0));
        journal.Projection.Seed(Fixture.Entry("obs-0002", 1, Fixture.OtherStation));

        var (entries, _, _) = await journal.Projector.ReadStationAsync(
            Fixture.Station,
            pageSize: 10,
            delay: null,
            TestContext.Current.CancellationToken);

        Assert.All(entries, entry => Assert.Equal(Fixture.Station, entry.StationId));
        Assert.Single(entries);
    }

    [Fact]
    public async Task ReadingAnEmptyStationCostsOnePageAndReturnsNothing()
    {
        var journal = new Journal();

        var (entries, _, pages) = await journal.Projector.ReadStationAsync(
            Fixture.Station,
            pageSize: 10,
            delay: null,
            TestContext.Current.CancellationToken);

        Assert.Empty(entries);
        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task ReprojectingAWholeRunWritesNothingAndCostsAReadEach()
    {
        // Re-running the projection after a crash is the normal recovery path.
        // It must converge on the same journal rather than rewrite it.
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading("obs-0001"), Fixture.Reading("obs-0002"));
        await journal.ProcessAsync();

        var first = await journal.ProjectHandledAsync();
        var again = await journal.ProjectHandledAsync();

        Assert.Equal(2, first.Written);
        Assert.Equal(0, again.Written);
        Assert.Equal(2, again.Superseded);
        Assert.Equal(2, journal.Projection.Entries.Count);
    }

    /// <summary>A projection whose documents always move on before a write lands.</summary>
    private sealed class ContendedProjection : IJournalProjection
    {
        public Task<ProjectionResult> WriteAsync(
            JournalEntry entry,
            string? ifMatch,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectionResult(ProjectionOutcome.Stale, null, 1.0));

        public Task<JournalEntry?> TryReadAsync(string stationId, string id, CancellationToken cancellationToken) =>
            Task.FromResult<JournalEntry?>(Fixture.Entry(sequenceNumber: 0));

        public Task<JournalPage> QueryStationAsync(
            string stationId,
            int pageSize,
            string? continuationToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(new JournalPage([], null, 1.0));

        public Task<bool> DeleteAsync(string stationId, string id, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
