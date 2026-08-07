using System.Net;
using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using LearningAzure.Support.AzureFakes;
using Microsoft.Azure.Cosmos;

namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>
/// Milestone 3 — the checkpoint store and the event mapper, graded against the
/// real SDK types. The ownership rules only mean anything if the conditional
/// headers actually reach the wire.
/// </summary>
[Trait("Milestone", "telemetry-pipeline")]
public sealed class CheckpointVaultWireTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ClaimingAFreePartitionPutsIfNoneMatchOnTheWire()
    {
        // Two processors starting together both find no blob. Only a conditional
        // create lets the service decide which of them owns the partition.
        var handler = new ScriptedHandler(
            _ => StorageResponses.NotFound(),
            _ => StorageResponses.Created());

        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        var ownership = await vault.TryClaimAsync("0", "host-a", TestContext.Current.CancellationToken);

        Assert.NotNull(ownership);
        Assert.Equal("*", handler.Requests[1].Header("If-None-Match"));
        Assert.Equal("host-a", handler.Requests[1].Header($"x-ms-meta-{BlobCheckpointVault.OwnerMetadataKey}"));
        Assert.EndsWith("/checkpoints/0", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LosingTheCreateRaceYieldsNoOwnershipRatherThanAnException()
    {
        var handler = new ScriptedHandler(
            _ => StorageResponses.NotFound(),
            _ => StorageResponses.Conflict());

        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        Assert.Null(await vault.TryClaimAsync("0", "host-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALivePartitionHeldByAnotherHostIsNotTouchedAtAll()
    {
        var clock = new ManualClock(Fixture.Start);
        var handler = new ScriptedHandler(_ => ScriptedClients.Properties("host-b", Fixture.Start, "\"0x1\""));
        var vault = new BlobCheckpointVault(ScriptedClients.Container(handler), clock, Lease);

        var ownership = await vault.TryClaimAsync("0", "host-a", TestContext.Current.CancellationToken);

        Assert.Null(ownership);
        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task TakingOverAnExpiredLeaseIsConditionalOnTheVersionJustRead()
    {
        // Without the precondition, two processors that both observe the same
        // expired lease both take it, which is the race the lease should settle.
        var clock = new ManualClock(Fixture.Start + Lease + TimeSpan.FromSeconds(1));
        var handler = new ScriptedHandler(
            _ => ScriptedClients.Properties("host-b", Fixture.Start, "\"0x1\""),
            _ => StorageResponses.Ok("\"0x2\""));

        var vault = new BlobCheckpointVault(ScriptedClients.Container(handler), clock, Lease);
        var ownership = await vault.TryClaimAsync("0", "host-a", TestContext.Current.CancellationToken);

        Assert.NotNull(ownership);
        Assert.Equal("\"0x1\"", handler.Requests[1].Header("If-Match"));
        Assert.Contains("comp=metadata", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARaceLostWhileTakingOverAnExpiredLeaseYieldsNoOwnership()
    {
        var clock = new ManualClock(Fixture.Start + Lease + TimeSpan.FromSeconds(1));
        var handler = new ScriptedHandler(
            _ => ScriptedClients.Properties("host-b", Fixture.Start, "\"0x1\""),
            _ => StorageResponses.PreconditionFailed());

        var vault = new BlobCheckpointVault(ScriptedClients.Container(handler), clock, Lease);

        Assert.Null(await vault.TryClaimAsync("0", "host-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACheckpointIsWrittenUnderTheClaimsVersion()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created("\"0x9\""));
        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        var renewed = await vault.TryWriteCheckpointAsync(
            new Checkpoint("0", 12, "o12"),
            new PartitionOwnership("0", "host-a", "\"0x1\""),
            TestContext.Current.CancellationToken);

        Assert.Equal("\"0x1\"", handler.Requests[0].Header("If-Match"));
        Assert.Equal("\"0x9\"", renewed!.ETag);
        Assert.Contains("\"sequenceNumber\":12", Encoding.UTF8.GetString(handler.Requests[0].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckpointFromAFormerOwnerIsRefused()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.PreconditionFailed());
        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        Assert.Null(await vault.TryWriteCheckpointAsync(
            new Checkpoint("0", 12, "o12"),
            new PartitionOwnership("0", "host-a", "\"0xstale\""),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingCheckpointReadsAsNoPositionRatherThanPositionZero()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.NotFound());
        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        Assert.Null(await vault.TryReadCheckpointAsync("0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACorruptedCheckpointReadsAsNoPositionRatherThanZero()
    {
        // Reading a corrupted record as position zero silently replays the whole
        // partition, which is a data problem presented as a successful start.
        var handler = new ScriptedHandler(_ => ScriptedClients.Download("{ truncated", "\"0x1\""));
        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        Assert.Null(await vault.TryReadCheckpointAsync("0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredCheckpointRoundTripsBackFromTheWire()
    {
        var handler = new ScriptedHandler(
            _ => ScriptedClients.Download("""{"sequenceNumber":42,"offset":"o42"}""", "\"0x1\""));

        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        var checkpoint = await vault.TryReadCheckpointAsync("0", TestContext.Current.CancellationToken);

        Assert.Equal(42, checkpoint!.SequenceNumber);
        Assert.Equal("o42", checkpoint.Offset);
    }

    [Fact]
    public async Task AForbiddenResponseKeepsTravellingInsteadOfReadingAsNoData()
    {
        // A missing role assignment must not present as an empty stream.
        var handler = ScriptedHandler.Always(_ => StorageResponses.Error(
            HttpStatusCode.Forbidden,
            "AuthorizationPermissionMismatch",
            "This request is not authorized."));

        var vault = new BlobCheckpointVault(
            ScriptedClients.Container(handler),
            new ManualClock(Fixture.Start),
            Lease);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() =>
            vault.TryReadCheckpointAsync("0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnEventCarriesTheServicesOwnCoordinatesBackIntoTheDomain()
    {
        // The sequence number and offset are how a partition addresses itself. A
        // counter maintained by the consumer diverges permanently after the first
        // restart or rebalance.
        var reading = Fixture.Reading();
        var data = EventHubsModelFactory.EventData(
            new BinaryData(Encoding.UTF8.GetBytes(JournalCodec.EncodeReading(reading))),
            partitionKey: Fixture.Station,
            sequenceNumber: 77,
            offsetString: "o77");

        var mapped = TelemetryEventMapper.ToStreamEvent(
            new PartitionEvent(EventHubsModelFactory.PartitionContext("3"), data));

        Assert.Equal("3", mapped!.PartitionId);
        Assert.Equal(77, mapped.SequenceNumber);
        Assert.Equal("o77", mapped.Offset);
        Assert.Equal(Fixture.Station, mapped.PartitionKey);
        Assert.Equal(reading, mapped.Reading);
    }

    [Fact]
    public void AReadThatTimedOutIsNotMistakenForAnEvent()
    {
        var empty = new PartitionEvent(EventHubsModelFactory.PartitionContext("0"), null);

        Assert.Null(TelemetryEventMapper.ToStreamEvent(empty));
    }

    [Fact]
    public void AnEventCarriesTheRoutingKeyAndTheStationAsProperties()
    {
        var data = TelemetryEventMapper.ToEventData(Fixture.Reading());

        Assert.Equal(Fixture.Station, data.Properties[TelemetryEventMapper.PartitionKeyProperty]);
        Assert.Equal(Fixture.Station, data.Properties[TelemetryEventMapper.StationProperty]);
        Assert.Equal("application/json", data.ContentType);
    }

    [Fact]
    public void AMalformedEventBodyIsRejectedAtTheBoundary()
    {
        var data = EventHubsModelFactory.EventData(
            new BinaryData(Encoding.UTF8.GetBytes("""{"stationId":"ridge camp"}""")),
            sequenceNumber: 1,
            offsetString: "o1");

        Assert.ThrowsAny<Exception>(() => TelemetryEventMapper.ToStreamEvent(
            new PartitionEvent(EventHubsModelFactory.PartitionContext("0"), data)));
    }
}

/// <summary>
/// Milestone 4 — how a Cosmos failure is read. The projector's whole retry
/// policy rests on 429 being classified as rate limiting rather than as an
/// outage.
/// </summary>
[Trait("Milestone", "cosmos-projection")]
public sealed class CosmosOutcomeTests
{
    [Fact]
    public void ARateLimitedResponseBecomesADomainThrottleCarryingItsCharge()
    {
        var error = new CosmosException(
            "Request rate is large.",
            HttpStatusCode.TooManyRequests,
            subStatusCode: 3200,
            activityId: "activity-1",
            requestCharge: 4.25);

        var throttle = CosmosOutcomes.AsThrottle(error);

        Assert.NotNull(throttle);
        Assert.Equal(4.25, throttle.RequestCharge);
        Assert.True(throttle.RetryAfter > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void EveryOtherStatusIsLeftForTheCallerToClassify(HttpStatusCode status)
    {
        // A catch-all that reads any failure as a throttle retries a missing role
        // assignment forever and never reports it.
        var error = new CosmosException("Not a throttle.", status, 0, "activity-1", 1.0);

        Assert.Null(CosmosOutcomes.AsThrottle(error));
    }

    [Fact]
    public void AThrottleWithNoServiceDelayStillWaitsBeforeRetrying()
    {
        // RetryAfter is absent on some responses. A zero wait turns the retry loop
        // into a tight spin against a service already asking for room.
        var error = new CosmosException("Request rate is large.", HttpStatusCode.TooManyRequests, 3200, "a", 1.0);

        Assert.True(CosmosOutcomes.AsThrottle(error)!.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TheStoredDocumentRoundTripsThroughItsBoundaryType()
    {
        var entry = Fixture.Entry(sequenceNumber: 3);
        var document = JournalDocument.From(entry);

        Assert.Equal(entry.Id, document.Id);
        Assert.Equal(entry.StationId, document.StationId);
        Assert.Equal(entry with { ETag = string.Empty }, document.ToEntry());
    }

    [Fact]
    public void ThePartitionKeyPathMatchesTheDocumentsStationField() =>
        Assert.Equal("/stationId", CosmosJournalProjection.PartitionKeyPath);
}
