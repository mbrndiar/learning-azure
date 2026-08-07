using System.Text.Json;

namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>
/// Milestone 1 — the domain and the ports. Judges whether identity is derived
/// rather than invented, whether a payload is validated before it is trusted, and
/// whether a batch is legal to send.
/// </summary>
[Trait("Milestone", "domain-ports")]
public sealed class DomainPortsTests
{
    private static readonly string[] ExpectedOrder = ["obs-0001", "obs-0002", "obs-0003"];

    [Fact]
    public void ThePartitionKeyIsTheStationSoOneStationStaysOrdered()
    {
        // Order is guaranteed within a partition and nowhere else. Keying on the
        // observation would spread one station across every partition, and the
        // consumer could then never tell a replay from an overtake.
        var first = ExpeditionNaming.PartitionKey(Fixture.Key);
        var second = ExpeditionNaming.PartitionKey(new ObservationKey(Fixture.Station, "obs-0009"));

        Assert.Equal(Fixture.Station, first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, ExpeditionNaming.PartitionKey(new ObservationKey(Fixture.OtherStation, "obs-0001")));
    }

    [Fact]
    public void TheArtifactNameIsAPureFunctionOfTheKey()
    {
        // If this is not true, nothing downstream can be idempotent: a replayed
        // reading writes a second blob and every later stage believes it.
        var first = ExpeditionNaming.ArtifactName(Fixture.Key);
        var second = ExpeditionNaming.ArtifactName(new ObservationKey(Fixture.Station, Fixture.Observation));

        Assert.Equal(first, second);
        Assert.Equal("journal/ridge-camp/obs-0001.json", first);
        Assert.StartsWith(ExpeditionNaming.StationPrefix(Fixture.Station), first, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJournalItemIdIsUniqueWithinTheStationNotAcrossTheContainer()
    {
        // A Cosmos document is addressed by (partition key, id). Folding the
        // station into the id would work, and would also make every document its
        // own partition's problem to find.
        var ridge = ExpeditionNaming.JournalItemId(Fixture.Key);
        var delta = ExpeditionNaming.JournalItemId(new ObservationKey(Fixture.OtherStation, Fixture.Observation));

        Assert.Equal("entry-obs-0001", ridge);
        Assert.Equal(ridge, delta);
        Assert.DoesNotContain(Fixture.Station, ridge, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArtifactNameRoundTripsBackToItsKey() =>
        Assert.Equal(Fixture.Key, ExpeditionNaming.TryParseArtifactName(ExpeditionNaming.ArtifactName(Fixture.Key)));

    [Fact]
    public void AnUnconventionalArtifactNameParsesToNullRatherThanGuessing()
    {
        Assert.Null(ExpeditionNaming.TryParseArtifactName("ridge-camp/obs-0001.json"));
        Assert.Null(ExpeditionNaming.TryParseArtifactName("journal/ridge-camp/obs-0001.png"));
        Assert.Null(ExpeditionNaming.TryParseArtifactName(null));
    }

    [Theory]
    [InlineData("ridge-camp")]
    [InlineData("station-01")]
    [InlineData("a1")]
    public void SafeIdentifiersAreAccepted(string value) =>
        Assert.True(ExpeditionNaming.IsValidIdentifier(value));

    [Theory]
    [InlineData("Ridge-Camp")]     // Table keys are case sensitive; blob prefixes are not.
    [InlineData("ridge camp")]     // A space survives a blob name and breaks a row key.
    [InlineData("ridge/camp")]     // Illegal in a row key, and silently a directory in a blob name.
    [InlineData("-ridge")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsafeIdentifiersAreRejected(string? value) =>
        Assert.False(ExpeditionNaming.IsValidIdentifier(value));

    [Fact]
    public void ACheckpointNameNamesOnlyAPartition()
    {
        Assert.Equal("checkpoints/3", ExpeditionNaming.CheckpointName("3"));
        Assert.Throws<ArgumentException>(() => ExpeditionNaming.CheckpointName("../secrets"));
    }

    [Fact]
    public void AReadingRoundTripsThroughTheCodec()
    {
        var reading = Fixture.Reading();

        Assert.Equal(reading, JournalCodec.DecodeReading(JournalCodec.EncodeReading(reading)));
    }

    [Theory]
    [InlineData("""{"stationId":"ridge-camp","observationId":"","celsius":-14.5,"observedUtc":"2026-07-06T12:00:00Z"}""")]
    [InlineData("""{"stationId":"ridge camp","observationId":"obs-0001","celsius":-14.5,"observedUtc":"2026-07-06T12:00:00Z"}""")]
    [InlineData("""{"stationId":"ridge-camp","observationId":"obs-0001","celsius":null,"observedUtc":"2026-07-06T12:00:00Z"}""")]
    [InlineData("""{"stationId":"ridge-camp","observationId":"obs-0001"}""")]
    public void APartiallyValidReadingIsStillRejected(string body)
    {
        // A decoder that fills in a default for the field it could not read hands
        // a plausible, wrong reading to every stage after it. The failure has to
        // happen here, where the message can still be quarantined.
        Assert.Throws<FormatException>(() => JournalCodec.DecodeReading(body));
    }

    [Fact]
    public void AReadingWithANonFiniteTemperatureIsRejected()
    {
        // NaN and infinity survive a JSON round trip through some encoders and
        // then poison every average computed downstream.
        Assert.Throws<FormatException>(() => JournalCodec.DecodeReading(
            """{"stationId":"ridge-camp","observationId":"obs-0001","celsius":"NaN","observedUtc":"2026-07-06T12:00:00Z"}"""));
    }

    [Fact]
    public void MalformedJsonIsRejectedRatherThanSilentlyEmpty()
    {
        Assert.ThrowsAny<Exception>(() => JournalCodec.DecodeReading("not json"));
        Assert.ThrowsAny<Exception>(() => JournalCodec.DecodeWorkOrder("{"));
    }

    [Fact]
    public void AWorkOrderWhoseIdDoesNotMatchItsKeysIsRejected()
    {
        // The id is derived, so an id that disagrees with the station and
        // observation beside it is either a forgery or a producer bug. Either way
        // it must not be executed.
        var forged = JsonSerializer.Serialize(new
        {
            workOrderId = "ridge-camp.obs-9999.summarize",
            stationId = Fixture.Station,
            observationId = Fixture.Observation,
            artifactName = ExpeditionNaming.ArtifactName(Fixture.Key),
            operation = WorkOperations.Summarize,
        });

        Assert.Throws<FormatException>(() => JournalCodec.DecodeWorkOrder(forged));
    }

    [Fact]
    public void AWorkOrderRoundTripsThroughTheCodec()
    {
        var order = Fixture.Order();

        Assert.Equal(order, JournalCodec.DecodeWorkOrder(JournalCodec.EncodeWorkOrder(order)));
    }

    [Fact]
    public void ARenderedArtifactCarriesTheReadingAndTheOrder()
    {
        var rendered = JournalCodec.RenderArtifact(Fixture.Order(), Fixture.Reading(), Fixture.Start);
        using var document = JsonDocument.Parse(rendered);

        Assert.Equal(Fixture.Station, document.RootElement.GetProperty("stationId").GetString());
        Assert.Equal(Fixture.Observation, document.RootElement.GetProperty("observationId").GetString());
        Assert.Equal(-14.5, document.RootElement.GetProperty("celsius").GetDouble());
    }

    [Fact]
    public void BatchesCarryExactlyOnePartitionKey()
    {
        // A batch is stamped with one key. Mixing stations in a batch is either
        // rejected or sent unkeyed, and unkeyed means per-station order is gone.
        var ingress = new TelemetryIngress(new InMemoryFeed(), maxEventsPerBatch: 8);

        var batches = ingress.Plan(
        [
            Fixture.Reading("obs-0001"),
            Fixture.Reading("obs-0002", Fixture.OtherStation),
            Fixture.Reading("obs-0003"),
        ]);

        Assert.All(batches, batch => Assert.All(
            batch.Readings,
            reading => Assert.Equal(batch.PartitionKey, ExpeditionNaming.PartitionKey(reading.Key))));
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void ABatchIsSplitAtTheCeilingAndKeepsItsOrder()
    {
        var ingress = new TelemetryIngress(new InMemoryFeed(), maxEventsPerBatch: 2);

        var batches = ingress.Plan(
        [
            Fixture.Reading("obs-0001", minutes: 0),
            Fixture.Reading("obs-0002", minutes: 1),
            Fixture.Reading("obs-0003", minutes: 2),
        ]);

        Assert.Equal(3, batches.Sum(batch => batch.Readings.Count));
        Assert.All(batches, batch => Assert.InRange(batch.Readings.Count, 1, 2));
        Assert.Equal(
            ExpectedOrder,
            batches.SelectMany(batch => batch.Readings).Select(reading => reading.ObservationId));
    }

    [Fact]
    public void AnEmptyPublishSendsNothingAtAll()
    {
        var feed = new InMemoryFeed();
        var ingress = new TelemetryIngress(feed);

        Assert.Empty(ingress.Plan([]));
    }

    [Fact]
    public async Task PublishReportsWhatEachPartitionKeyReceived()
    {
        var feed = new InMemoryFeed();
        var ingress = new TelemetryIngress(feed, maxEventsPerBatch: 2);

        var receipt = await ingress.PublishAsync(
            [
                Fixture.Reading("obs-0001"),
                Fixture.Reading("obs-0002"),
                Fixture.Reading("obs-0003"),
                Fixture.Reading("obs-0004", Fixture.OtherStation),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(4, receipt.ReadingCount);
        Assert.Equal(3, receipt.BatchCount);
        Assert.Equal(3, receipt.ByPartitionKey[Fixture.Station]);
        Assert.Equal(1, receipt.ByPartitionKey[Fixture.OtherStation]);
    }

    [Fact]
    public void TheDomainNeverNamesAnAzureType()
    {
        // The ports are what let five services be graded in memory. A single
        // SDK type on a port drags the whole pipeline back onto the network.
        var domainTypes = new[]
        {
            typeof(ITelemetryFeed),
            typeof(ICheckpointStore),
            typeof(IArtifactVault),
            typeof(IWorkBacklog),
            typeof(IStationRegistry),
            typeof(IJournalProjection),
            typeof(TelemetryReading),
            typeof(StreamEvent),
            typeof(JournalEntry),
            typeof(StationState),
        };

        foreach (var type in domainTypes)
        {
            var referenced = type.GetMethods()
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType))
                .Concat(type.GetProperties().Select(property => property.PropertyType))
                .SelectMany(Unwrap)
                .Select(candidate => candidate.Namespace ?? string.Empty)
                .ToList();

            Assert.DoesNotContain(referenced, name =>
                name.StartsWith("Azure", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Azure", StringComparison.Ordinal));
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }
}
