using System.Text.Json;

namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>
/// Milestone 1 — the domain and the ports. Judges whether identity is derived
/// rather than invented, and whether the contracts stay free of SDK types.
/// </summary>
[Trait("Milestone", "domain-ports")]
public sealed class DomainPortsTests
{
    [Fact]
    public void TheArtifactNameIsAPureFunctionOfTheKey()
    {
        // If this is not true, nothing downstream can be idempotent: a replayed
        // upload writes a second blob and every later stage believes it.
        var first = StationNaming.ArtifactName(Fixture.Key);
        var second = StationNaming.ArtifactName(new ArtifactKey(Fixture.Station, Fixture.Observation));

        Assert.Equal(first, second);
        Assert.Equal("stations/ridge-camp/obs-0001.json", first);
    }

    [Fact]
    public void TheWorkOrderIdIsDerivedFromTheKeyAndTheOperation()
    {
        var checksum = StationNaming.WorkOrderId(Fixture.Key, "checksum");
        var thumbnail = StationNaming.WorkOrderId(Fixture.Key, "thumbnail");

        Assert.Equal(checksum, StationNaming.WorkOrderId(Fixture.Key, "checksum"));
        Assert.NotEqual(checksum, thumbnail);
    }

    [Fact]
    public void AnArtifactNameRoundTripsBackToItsKey()
    {
        var parsed = StationNaming.TryParseArtifactName(StationNaming.ArtifactName(Fixture.Key));

        Assert.Equal(Fixture.Key, parsed);
    }

    [Fact]
    public void AnUnconventionalArtifactNameParsesToNullRatherThanGuessing()
    {
        Assert.Null(StationNaming.TryParseArtifactName("ridge-camp/obs-0001.json"));
        Assert.Null(StationNaming.TryParseArtifactName("stations/ridge-camp/obs-0001.png"));
        Assert.Null(StationNaming.TryParseArtifactName(null));
    }

    [Theory]
    [InlineData("ridge-camp")]
    [InlineData("station-01")]
    [InlineData("a1")]
    public void SafeIdentifiersAreAccepted(string value) =>
        Assert.True(StationNaming.IsValidIdentifier(value));

    [Theory]
    [InlineData("Ridge-Camp")]     // Table keys are case sensitive; blob prefixes are not.
    [InlineData("ridge camp")]     // A space survives a blob name and breaks a row key.
    [InlineData("ridge/camp")]     // Illegal in a row key, and silently a directory in a blob name.
    [InlineData("ridge#camp")]     // Illegal in a row key.
    [InlineData("ridge?camp")]     // Illegal in a row key.
    [InlineData("a")]              // Too short to be a meaningful station.
    [InlineData("")]
    [InlineData(null)]
    public void UnsafeIdentifiersAreRejected(string? value) =>
        Assert.False(StationNaming.IsValidIdentifier(value));

    [Fact]
    public void NamingAnUnsafeKeyFailsAtTheBoundaryRatherThanDownstream()
    {
        // The service would accept "ridge/camp" as a blob name and reject it as a
        // row key, which produces an artifact nothing can ever index.
        var error = Assert.Throws<ArgumentException>(
            () => StationNaming.ArtifactName(new ArtifactKey("ridge/camp", Fixture.Observation)));

        Assert.Contains("safe field-station identifier", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryRowKeyCanNeverCollideWithAnObservation()
    {
        Assert.False(StationNaming.IsValidIdentifier(StationNaming.SummaryRowKey));
        Assert.DoesNotContain('#', StationNaming.SummaryRowKey);
        Assert.DoesNotContain('/', StationNaming.SummaryRowKey);
    }

    [Fact]
    public void AWorkOrderSurvivesTheWireUnchanged()
    {
        var order = Fixture.Order();

        Assert.Equal(order, WorkOrderCodec.Decode(WorkOrderCodec.Encode(order)));
    }

    [Fact]
    public void AnEmptyObjectIsRejectedRatherThanDecodedIntoNulls()
    {
        // Deserialization happily produces a work order whose every field is
        // null. Letting that through moves the failure to the first place a
        // field is dereferenced, which is usually the ledger.
        Assert.Throws<FormatException>(() => WorkOrderCodec.Decode("{}"));
    }

    [Fact]
    public void AWorkOrderIdThatDoesNotMatchItsOwnFieldsIsRejected()
    {
        var forged = WorkOrderCodec.Encode(Fixture.Order()).Replace(
            StationNaming.WorkOrderId(Fixture.Key, Fixture.Operation),
            "whatever-the-producer-felt-like",
            StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => WorkOrderCodec.Decode(forged));
    }

    [Fact]
    public void AMalformedBodyIsAJsonFailureRatherThanACrash()
    {
        Assert.Throws<JsonException>(() => WorkOrderCodec.Decode("not json at all"));
    }

    [Fact]
    public void ThePortsExposeNoAzureTypes()
    {
        // The whole pipeline is graded in memory. That is only possible while the
        // contracts are expressed in the domain's own vocabulary; one Azure type
        // on a port drags the SDK into every fake and every test.
        var ports = new[] { typeof(IArtifactStore), typeof(IWorkBacklog), typeof(IStationStatusIndex) };

        foreach (var port in ports)
        {
            foreach (var method in port.GetMethods())
            {
                var types = method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(Unwrap);

                Assert.All(types, type => Assert.False(
                    IsAzureSdkType(type),
                    $"{port.Name}.{method.Name} exposes the Azure SDK type {type.FullName}."));
            }
        }
    }

    [Fact]
    public void EveryPortOperationTakesACancellationToken()
    {
        // An operation that cannot be cancelled is an operation that keeps a
        // shutdown waiting for a network round trip that may never answer.
        var ports = new[] { typeof(IArtifactStore), typeof(IWorkBacklog), typeof(IStationStatusIndex) };

        foreach (var port in ports)
        {
            Assert.All(port.GetMethods(), method => Assert.Contains(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(CancellationToken)));
        }
    }

    private static bool IsAzureSdkType(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly.Equals("Azure.Core", StringComparison.Ordinal)
            || assembly.StartsWith("Azure.", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return argument;
            }
        }
    }
}
