namespace LearningAzure.Exercises.BlobStorage.Tests;

/// <summary>Verifies the naming scheme that turns a flat namespace into directories.</summary>
public sealed class ArtifactPathTests
{
    private static readonly DateTimeOffset Observed =
        new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANameCarriesStationAndTimestamp()
    {
        var name = ArtifactPath.For("station-bravo", Observed, "calving.jpg");

        Assert.Equal("observations/station-bravo/2026/07/06/120000-calving.jpg", name);
    }

    [Fact]
    public void ANameIsBuiltFromUtcNotLocalTime()
    {
        var sameInstantElsewhere = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal(
            ArtifactPath.For("station-bravo", Observed, "calving.jpg"),
            ArtifactPath.For("station-bravo", sameInstantElsewhere, "calving.jpg"));
    }

    [Fact]
    public void NamesSortChronologicallyWithinAStation()
    {
        var earlier = ArtifactPath.For("station-bravo", Observed, "a.jpg");
        var later = ArtifactPath.For("station-bravo", Observed.AddHours(3), "a.jpg");

        // Blob listings are ordered lexicographically, so zero-padded UTC
        // components are what make a listing chronological for free.
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void ASlashInTheFileNameIsRejected()
    {
        // A slash invents an extra virtual directory level and silently breaks
        // every prefix this class produces.
        Assert.Throws<ArgumentException>(
            () => ArtifactPath.For("station-bravo", Observed, "raw/calving.jpg"));
    }

    [Fact]
    public void ABackslashInTheFileNameIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => ArtifactPath.For("station-bravo", Observed, "raw\\calving.jpg"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankStationIsRejected(string stationId)
    {
        Assert.Throws<ArgumentException>(() => ArtifactPath.For(stationId, Observed, "calving.jpg"));
    }

    [Fact]
    public void TheDayPrefixEndsWithASlash()
    {
        var prefix = ArtifactPath.DayPrefix("station-bravo", Observed);

        Assert.EndsWith("/", prefix, StringComparison.Ordinal);
        Assert.Equal("observations/station-bravo/2026/07/06/", prefix);
    }

    [Fact]
    public void ZeroPaddingKeepsDayOneFromMatchingDayTen()
    {
        var dayOne = ArtifactPath.DayPrefix("station-bravo", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var dayTen = ArtifactPath.For("station-bravo", new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), "a.jpg");

        // A prefix scan is a string comparison and nothing more. Unpadded, the
        // prefix for day 1 would also match days 10 through 19.
        Assert.EndsWith("/01/", dayOne, StringComparison.Ordinal);
        Assert.False(dayTen.StartsWith(dayOne, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDayPrefixMatchesEveryArtifactFromThatDay()
    {
        var prefix = ArtifactPath.DayPrefix("station-bravo", Observed);
        var name = ArtifactPath.For("station-bravo", Observed.AddHours(5), "calving.jpg");

        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStationPrefixEndsWithASlash()
    {
        Assert.Equal("observations/station-bravo/", ArtifactPath.StationPrefix("station-bravo"));
    }

    [Fact]
    public void TheStationPrefixDoesNotMatchALongerStationName()
    {
        var shortStation = ArtifactPath.StationPrefix("station-b");
        var longStation = ArtifactPath.For("station-bravo", Observed, "a.jpg");

        Assert.False(longStation.StartsWith(shortStation, StringComparison.Ordinal));
    }

    [Fact]
    public void TheStationPrefixMatchesEveryDayOfThatStation()
    {
        var prefix = ArtifactPath.StationPrefix("station-bravo");

        Assert.StartsWith(prefix, ArtifactPath.DayPrefix("station-bravo", Observed), StringComparison.Ordinal);
    }

    [Fact]
    public void AStationCanBeReadBackFromAName()
    {
        var name = ArtifactPath.For("station-bravo", Observed, "calving.jpg");

        Assert.Equal("station-bravo", ArtifactPath.StationOf(name));
    }

    [Theory]
    [InlineData("station-bravo/2026/07/06/120000-a.jpg")]
    [InlineData("observations/station-bravo/2026/07/120000-a.jpg")]
    [InlineData("thumbnails/station-bravo/2026/07/06/120000-a.jpg")]
    [InlineData("observations/station-bravo/2026/07/06/nested/120000-a.jpg")]
    public void AForeignNameIsRejectedRatherThanGuessed(string blobName)
    {
        // Returning segment 1 of a foreign name attributes an artifact to the
        // wrong station, which is worse than failing.
        Assert.Throws<FormatException>(() => ArtifactPath.StationOf(blobName));
    }

    [Fact]
    public void EveryNameThisClassBuildsCanBeReadBack()
    {
        string[] stations = ["station-bravo", "station-alfa", "s1"];

        foreach (var station in stations)
        {
            var name = ArtifactPath.For(station, Observed, "a.jpg");
            Assert.Equal(station, ArtifactPath.StationOf(name));
        }
    }
}
