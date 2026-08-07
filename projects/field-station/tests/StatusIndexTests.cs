using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>
/// Milestone 4 — the ledger. Judges the conditional claim, the ETag discipline on
/// the contended summary row, and the Table adapter's point reads.
/// </summary>
[Trait("Milestone", "status-index")]
public sealed class StatusIndexTests
{
    [Fact]
    public async Task TheFirstClaimWinsTheRow()
    {
        var world = new Pipeline();

        var claim = await world.Projector.TryClaimAsync(Fixture.Order(), TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Claimed, claim);
        var row = await world.RowAsync();
        Assert.Equal(ProcessingState.Pending, row!.State);
        Assert.Equal(StationNaming.ArtifactName(Fixture.Key), row.ArtifactName);
        Assert.Equal(Fixture.Start, row.UpdatedUtc);
    }

    [Fact]
    public async Task ASecondClaimOfAnUnfinishedRowResumesItRatherThanClaimingIt()
    {
        // A Pending row means "the effect may or may not have happened". The only
        // safe reading of that is to run it again, so this is Resumed, not
        // AlreadyProcessed.
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);

        var second = await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Resumed, second);
        Assert.Equal(1, world.Index.LostInserts);
    }

    [Fact]
    public async Task AClaimOfAConfirmedRowReportsThatTheEffectAlreadyHappened()
    {
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);
        await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);

        Assert.Equal(
            ClaimOutcome.AlreadyProcessed,
            await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AClaimOfAQuarantinedRowAsksForAHumanRatherThanAnotherAttempt()
    {
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.MarkQuarantinedAsync(order, 8, TestContext.Current.CancellationToken);

        Assert.Equal(
            ClaimOutcome.AlreadyQuarantined,
            await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfirmingProcessingCountsTheObservationOnce()
    {
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);

        var total = await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal(ProcessingState.Processed, (await world.RowAsync())!.State);
        Assert.Equal(1, (await world.SummaryAsync())!.ProcessedCount);
    }

    [Fact]
    public async Task ConfirmingTwiceDoesNotInflateTheStationTotal()
    {
        // A redelivered confirmation is not an error, but counting it is: the
        // station report is what the expedition trusts.
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);
        await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);

        var total = await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal(1, (await world.SummaryAsync())!.ProcessedCount);
    }

    [Fact]
    public async Task ConfirmingAnUnclaimedRowFailsLoudlyRatherThanInventingOne()
    {
        var world = new Pipeline();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Projector.ConfirmProcessedAsync(Fixture.Order(), 8, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AContendedSummaryIncrementRereadsAndDoesNotLoseTheCompetitorsCount()
    {
        // This is the lost-update bug in miniature. Somebody else increments
        // between this caller's read and its write; retrying with the value from
        // the stale read silently discards their observation.
        var world = new Pipeline();
        await world.Projector.IncrementStationTotalAsync(Fixture.Station, 8, TestContext.Current.CancellationToken);

        var stolen = false;
        world.Index.BeforeReplace = (station, row) =>
        {
            if (!stolen && row == StationNaming.SummaryRowKey)
            {
                stolen = true;
                world.Index.StealIncrement(station, row);
            }
        };

        var total = await world.Projector.IncrementStationTotalAsync(
            Fixture.Station, 8, TestContext.Current.CancellationToken);

        Assert.True(stolen);
        Assert.Equal(1, world.Index.StaleReplaces);
        Assert.Equal(3, total);
        Assert.Equal(3, (await world.SummaryAsync())!.ProcessedCount);
    }

    [Fact]
    public async Task PermanentContentionFailsInsteadOfLoopingForever()
    {
        // An unbounded retry loop against a hot row is an outage that looks like
        // a hang, which is the hardest kind to diagnose.
        var world = new Pipeline();
        await world.Projector.IncrementStationTotalAsync(Fixture.Station, 8, TestContext.Current.CancellationToken);
        world.Index.BeforeReplace = (station, row) => world.Index.StealRace(station, row);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Projector.IncrementStationTotalAsync(Fixture.Station, 3, TestContext.Current.CancellationToken));

        Assert.Equal(3, world.Index.StaleReplaces);
    }

    [Fact]
    public async Task TenObservationsProduceATotalOfTen()
    {
        var world = new Pipeline();

        for (var index = 1; index <= 10; index++)
        {
            var order = Fixture.Order($"obs-{index:0000}");
            await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);
            await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);
        }

        Assert.Equal(10, await world.Projector.ReadStationTotalAsync(
            Fixture.Station, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStationQueryReturnsItsObservationRowsAndItsSummary()
    {
        var world = new Pipeline();
        var order = Fixture.Order();
        await world.Projector.TryClaimAsync(order, TestContext.Current.CancellationToken);
        await world.Projector.ConfirmProcessedAsync(order, 8, TestContext.Current.CancellationToken);

        var rows = new List<StationStatus>();
        await foreach (var row in world.Index.QueryStationAsync(Fixture.Station, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        Assert.Equal([Fixture.Observation, StationNaming.SummaryRowKey], rows.Select(row => row.RowKey));
    }

    [Fact]
    public async Task TheAdapterPointReadsWithBothKeys()
    {
        // One key is a partition scan, neither is a table scan, and both return
        // the same row for a different amount of money on every run.
        var handler = new ScriptedHandler(
            _ => ScriptedClients.TableEntity(Fixture.Station, Fixture.Observation, "Pending", 0, "W/\"a\""));
        var index = new TableStationIndex(ScriptedClients.Table(handler));

        var row = await index.TryGetAsync(
            Fixture.Station, Fixture.Observation, TestContext.Current.CancellationToken);

        var path = Assert.Single(handler.Requests).Uri.AbsolutePath;
        Assert.Contains($"PartitionKey='{Fixture.Station}'", Uri.UnescapeDataString(path), StringComparison.Ordinal);
        Assert.Contains($"RowKey='{Fixture.Observation}'", Uri.UnescapeDataString(path), StringComparison.Ordinal);
        Assert.DoesNotContain("$filter", Assert.Single(handler.Requests).Uri.Query, StringComparison.Ordinal);
        Assert.Equal(ProcessingState.Pending, row!.State);
    }

    [Fact]
    public async Task AMissingRowReadsAsNull()
    {
        var handler = new ScriptedHandler(
            _ => StorageResponses.Error(System.Net.HttpStatusCode.NotFound, "ResourceNotFound", "Not found."));
        var index = new TableStationIndex(ScriptedClients.Table(handler));

        Assert.Null(await index.TryGetAsync(
            Fixture.Station, Fixture.Observation, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheAdapterReportsALostInsertRatherThanThrowing()
    {
        // 409 EntityAlreadyExists is not an error condition here: it is the
        // duplicate signal the entire pipeline is built on.
        var handler = new ScriptedHandler(
            _ => StorageResponses.Error(System.Net.HttpStatusCode.Conflict, "EntityAlreadyExists", "Exists."));
        var index = new TableStationIndex(ScriptedClients.Table(handler));

        Assert.Null(await index.TryInsertAsync(Row(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheAdapterSendsTheCallersEtagOnAReplaceRatherThanAWildcard()
    {
        // If-Match: * means "overwrite whatever is there", which is exactly the
        // lost update the ETag exists to prevent.
        var handler = new ScriptedHandler(_ => ScriptedClients.TableWritten("W/\"b\""));
        var index = new TableStationIndex(ScriptedClients.Table(handler));

        await index.TryReplaceAsync(Row(), "W/\"a\"", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("W/\"a\"", request.Header("If-Match"));
    }

    [Fact]
    public async Task TheAdapterReportsAStaleReplaceRatherThanThrowing()
    {
        var handler = new ScriptedHandler(
            _ => StorageResponses.Error(
                System.Net.HttpStatusCode.PreconditionFailed, "UpdateConditionNotSatisfied", "Stale."));
        var index = new TableStationIndex(ScriptedClients.Table(handler));

        Assert.Null(await index.TryReplaceAsync(Row(), "W/\"a\"", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStationQueryFiltersOnThePartitionKeyServerSide()
    {
        // Filtering client-side downloads every station's rows to answer a
        // question about one of them.
        var handler = new ScriptedHandler(_ => EmptyPage());
        var index = new TableStationIndex(ScriptedClients.Table(handler));

        await foreach (var _ in index.QueryStationAsync(Fixture.Station, TestContext.Current.CancellationToken))
        {
            // The page is empty; the assertion is about the request, not the rows.
        }

        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Uri.Query);
        Assert.Contains("$filter=", query, StringComparison.Ordinal);
        Assert.Contains($"PartitionKey eq '{Fixture.Station}'", query, StringComparison.Ordinal);
    }

    private static HttpResponseMessage EmptyPage() =>
        StorageResponses.OkWithBody("""{"value":[]}"""u8.ToArray(), "application/json");

    private static StationStatus Row() => new()
    {
        StationId = Fixture.Station,
        RowKey = Fixture.Observation,
        State = ProcessingState.Processed,
        ProcessedCount = 1,
        ArtifactName = StationNaming.ArtifactName(Fixture.Key),
        UpdatedUtc = Fixture.Start,
    };
}
