using System.Globalization;

namespace LearningAzure.Exercises.TableStorage.Tests;

public sealed class ObservationKeysTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThePartitionKeyNamesTheStation()
    {
        Assert.Contains("station-bravo", ObservationKeys.PartitionKeyFor("station-bravo", Noon), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePartitionKeyNamesTheDay()
    {
        Assert.Contains("2026-07-06", ObservationKeys.PartitionKeyFor("station-bravo", Noon), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoObservationsOnTheSameDayShareAPartition()
    {
        var morning = ObservationKeys.PartitionKeyFor("station-bravo", Noon.AddHours(-6));
        var evening = ObservationKeys.PartitionKeyFor("station-bravo", Noon.AddHours(6));

        Assert.Equal(morning, evening);
    }

    [Fact]
    public void ObservationsOnDifferentDaysDoNotShareAPartition()
    {
        var today = ObservationKeys.PartitionKeyFor("station-bravo", Noon);
        var tomorrow = ObservationKeys.PartitionKeyFor("station-bravo", Noon.AddDays(1));

        Assert.NotEqual(today, tomorrow);
    }

    [Fact]
    public void DifferentStationsDoNotShareAPartition()
    {
        Assert.NotEqual(
            ObservationKeys.PartitionKeyFor("station-bravo", Noon),
            ObservationKeys.PartitionKeyFor("station-delta", Noon));
    }

    [Fact]
    public void ThePartitionKeyIsComputedInUtcNotLocalTime()
    {
        // The same instant, expressed in two offsets, must land in one partition.
        var utc = ObservationKeys.PartitionKeyFor("station-bravo", Noon);
        var shifted = ObservationKeys.PartitionKeyFor("station-bravo", Noon.ToOffset(TimeSpan.FromHours(9)));

        Assert.Equal(utc, shifted);
    }

    [Fact]
    public void ThePartitionKeyIsItselfAUsableKey()
    {
        Assert.True(ObservationKeys.IsUsableKey(ObservationKeys.PartitionKeyFor("station-bravo", Noon)));
    }

    [Fact]
    public void APartitionKeyNeedsAStation()
    {
        Assert.ThrowsAny<ArgumentException>(() => ObservationKeys.PartitionKeyFor("  ", Noon));
    }

    [Fact]
    public void RowKeysSortChronologicallyAsStrings()
    {
        DateTimeOffset[] instants =
        [
            Noon.AddHours(-3),
            Noon.AddMinutes(5),
            Noon.AddHours(9),
            Noon.AddHours(11),
        ];

        var keys = instants.Select(ObservationKeys.RowKeyFor).ToArray();

        Assert.Equal<IEnumerable<string>>(keys, [.. keys.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void TheNineOClockTrapDoesNotBite()
    {
        // Unpadded, "9:05" sorts after "10:05". Zero padding is what prevents it.
        var nine = ObservationKeys.RowKeyFor(new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero));
        var ten = ObservationKeys.RowKeyFor(new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero));

        Assert.True(
            string.CompareOrdinal(nine, ten) < 0,
            $"'{nine}' must sort before '{ten}' or every range query is wrong.");
    }

    [Fact]
    public void EveryRowKeyIsTheSameWidth()
    {
        var widths = new[] { Noon, Noon.AddHours(9), Noon.AddDays(400) }
            .Select(instant => ObservationKeys.RowKeyFor(instant).Length)
            .Distinct()
            .ToArray();

        Assert.Single(widths);
    }

    [Fact]
    public void TheRowKeyIsComputedInUtcNotLocalTime()
    {
        Assert.Equal(
            ObservationKeys.RowKeyFor(Noon),
            ObservationKeys.RowKeyFor(Noon.ToOffset(TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void TheRowKeyIsAUsableKey()
    {
        Assert.True(ObservationKeys.IsUsableKey(ObservationKeys.RowKeyFor(Noon)));
    }

    [Fact]
    public void DescendingRowKeysSortNewestFirst()
    {
        DateTimeOffset[] instants = [Noon, Noon.AddHours(1), Noon.AddHours(2)];

        var keys = instants.Select(ObservationKeys.DescendingRowKeyFor).ToArray();

        Assert.Equal<IEnumerable<string>>([keys[2], keys[1], keys[0]], [.. keys.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void EveryDescendingRowKeyIsNineteenCharactersWide()
    {
        foreach (var instant in (DateTimeOffset[])[Noon, Noon.AddYears(-20), Noon.AddYears(20)])
        {
            Assert.Equal(19, ObservationKeys.DescendingRowKeyFor(instant).Length);
        }
    }

    [Fact]
    public void DescendingRowKeysAreDigitsOnlySoTheyPadCorrectly()
    {
        var key = ObservationKeys.DescendingRowKeyFor(Noon);

        Assert.True(key.All(char.IsAsciiDigit), $"'{key}' must be digits only to sort as a fixed-width number.");
    }

    [Fact]
    public void DescendingAndAscendingKeysDisagreeAboutOrder()
    {
        var ascendingEarlier = ObservationKeys.RowKeyFor(Noon);
        var ascendingLater = ObservationKeys.RowKeyFor(Noon.AddHours(1));
        var descendingEarlier = ObservationKeys.DescendingRowKeyFor(Noon);
        var descendingLater = ObservationKeys.DescendingRowKeyFor(Noon.AddHours(1));

        Assert.True(string.CompareOrdinal(ascendingEarlier, ascendingLater) < 0);
        Assert.True(string.CompareOrdinal(descendingEarlier, descendingLater) > 0);
    }

    [Theory]
    [InlineData("station-bravo")]
    [InlineData("2026-07-06T00:00:00.0000000Z")]
    [InlineData("a")]
    public void OrdinaryValuesAreUsableKeys(string key)
    {
        Assert.True(ObservationKeys.IsUsableKey(key));
    }

    [Theory]
    [InlineData("observations/station-bravo")]
    [InlineData("station\\bravo")]
    [InlineData("station#bravo")]
    [InlineData("station?bravo")]
    public void ForbiddenCharactersMakeAValueUnusable(string key)
    {
        Assert.False(ObservationKeys.IsUsableKey(key));
    }

    [Fact]
    public void AControlCharacterMakesAValueUnusable()
    {
        Assert.False(ObservationKeys.IsUsableKey("station\u0001bravo"));
    }

    [Fact]
    public void AnEmptyValueIsNotAKey()
    {
        Assert.False(ObservationKeys.IsUsableKey(string.Empty));
    }

    [Fact]
    public void ANullValueIsNotAKey()
    {
        Assert.False(ObservationKeys.IsUsableKey(null));
    }

    [Fact]
    public void AValueOverTheLengthLimitIsNotAKey()
    {
        Assert.False(ObservationKeys.IsUsableKey(new string('a', 1025)));
    }

    [Fact]
    public void ABlobNameIsNotAUsableKeyWhichIsTheWholePoint()
    {
        // Module 4's blob names are full of slashes. Pasting one into a key is
        // the usual way this rule is discovered.
        Assert.False(ObservationKeys.IsUsableKey("observations/station-bravo/2026/07/06/frame-0001.jpg"));
    }

    [Fact]
    public void TheDayComponentIsZeroPaddedLikeTheBlobPrefixWas()
    {
        var firstOfMonth = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Contains(
            firstOfMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ObservationKeys.PartitionKeyFor("station-bravo", firstOfMonth),
            StringComparison.Ordinal);
    }
}
