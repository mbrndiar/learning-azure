using System.Text;
using LearningAzure.Exercises.CosmosModeling;

namespace LearningAzure.Exercises.CosmosModeling.Tests;

/// <summary>
/// Checks the two ways of manufacturing a partition key when the document does
/// not contain one: combining properties, and adding a bucket.
/// </summary>
public sealed class SyntheticKeyBuilderTests
{
    [Fact]
    public void PartsAreJoinedInTheOrderGiven() =>
        Assert.Equal("arctic|station-05", SyntheticKeyBuilder.Compose("arctic", "station-05"));

    [Fact]
    public void OrderIsSignificant() =>
        Assert.NotEqual(
            SyntheticKeyBuilder.Compose("a", "b"),
            SyntheticKeyBuilder.Compose("b", "a"));

    [Fact]
    public void ASinglePartIsAValidCompositeKey() =>
        Assert.Equal("arctic", SyntheticKeyBuilder.Compose("arctic"));

    [Fact]
    public void AnAbsentPartIsRefusedRatherThanSkipped()
    {
        // Skipping it would place this document in the 'arctic' partition while
        // every complete document goes to 'arctic|station-05'.
        Assert.Throws<ArgumentException>(() => SyntheticKeyBuilder.Compose("arctic", ""));
    }

    [Fact]
    public void ANullPartIsRefused() =>
        Assert.Throws<ArgumentException>(() => SyntheticKeyBuilder.Compose("arctic", null!));

    [Fact]
    public void APartContainingTheSeparatorIsRefused()
    {
        // 'a|b' + 'c' and 'a' + 'b|c' would both compose to 'a|b|c'.
        Assert.Throws<ArgumentException>(() => SyntheticKeyBuilder.Compose("a|b", "c"));
    }

    [Fact]
    public void ComposingNothingIsRefused() =>
        Assert.Throws<ArgumentException>(() => SyntheticKeyBuilder.Compose());

    [Fact]
    public void ComposingRefusesANullArray() =>
        Assert.Throws<ArgumentNullException>(() => SyntheticKeyBuilder.Compose(null!));

    [Fact]
    public void TheKeySizeLimitIsTwoKibibytes() =>
        Assert.Equal(2_048, SyntheticKeyBuilder.MaximumKeyBytes);

    [Fact]
    public void AKeyAtTheLimitIsAccepted()
    {
        var atLimit = new string('x', SyntheticKeyBuilder.MaximumKeyBytes);

        Assert.Equal(
            SyntheticKeyBuilder.MaximumKeyBytes,
            Encoding.UTF8.GetByteCount(SyntheticKeyBuilder.Compose(atLimit)));
    }

    [Fact]
    public void AKeyOverTheLimitIsRefused()
    {
        var tooLong = new string('x', SyntheticKeyBuilder.MaximumKeyBytes + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticKeyBuilder.Compose(tooLong));
    }

    [Fact]
    public void TheLimitIsMeasuredInBytesNotCharacters()
    {
        // Each of these characters is three bytes in UTF-8.
        var multiByte = new string('\u2603', (SyntheticKeyBuilder.MaximumKeyBytes / 3) + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticKeyBuilder.Compose(multiByte));
    }

    [Fact]
    public void TheSameDocumentAlwaysLandsInTheSameBucket()
    {
        var builder = new SyntheticKeyBuilder(buckets: 16);

        Assert.Equal(
            builder.Spread("2026-08-07", "reading-4711"),
            builder.Spread("2026-08-07", "reading-4711"));
    }

    [Fact]
    public void TheBucketDependsOnTheDocumentSoTheKeyIsRecomputable()
    {
        var builder = new SyntheticKeyBuilder(buckets: 16);

        var one = builder.Spread("2026-08-07", "reading-0001");
        var another = builder.Spread("2026-08-07", "reading-0002");

        // Not a strict requirement of correctness, but if every document landed
        // in one bucket the spread would be doing nothing.
        Assert.NotEqual(one, another);
    }

    [Fact]
    public void TheHotKeyIsStillReadableInTheResult()
    {
        var builder = new SyntheticKeyBuilder(buckets: 4);

        Assert.StartsWith("2026-08-07-", builder.Spread("2026-08-07", "reading-0001"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDocumentLandsInsideTheBucketRange()
    {
        var builder = new SyntheticKeyBuilder(buckets: 8);
        var allowed = builder.FanOutKeys("2026-08-07").ToHashSet(StringComparer.Ordinal);

        for (var index = 0; index < 500; index++)
        {
            Assert.Contains(builder.Spread("2026-08-07", $"reading-{index:0000}"), allowed);
        }
    }

    [Fact]
    public void EveryBucketIsUsedByAReasonableNumberOfDocuments()
    {
        var builder = new SyntheticKeyBuilder(buckets: 8);

        var used = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 500; index++)
        {
            used.Add(builder.Spread("2026-08-07", $"reading-{index:0000}"));
        }

        Assert.Equal(8, used.Count);
    }

    [Fact]
    public void TheFanOutListHasOneKeyPerBucket()
    {
        var builder = new SyntheticKeyBuilder(buckets: 5);

        Assert.Equal(5, builder.FanOutKeys("2026-08-07").Count);
    }

    [Fact]
    public void TheFanOutListIsTheCostOfSpreading()
    {
        // This is the trade: one write partition became five, and so did every
        // read that wants the whole day.
        var builder = new SyntheticKeyBuilder(buckets: 5);

        Assert.Equal(
            ["2026-08-07-000", "2026-08-07-001", "2026-08-07-002", "2026-08-07-003", "2026-08-07-004"],
            builder.FanOutKeys("2026-08-07"));
    }

    [Fact]
    public void SpreadingRefusesAnEmptyKey()
    {
        var builder = new SyntheticKeyBuilder(buckets: 4);

        Assert.Throws<ArgumentException>(() => builder.Spread("", "reading-0001"));
    }

    [Fact]
    public void SpreadingRefusesAnEmptyDocumentId()
    {
        var builder = new SyntheticKeyBuilder(buckets: 4);

        Assert.Throws<ArgumentException>(() => builder.Spread("2026-08-07", ""));
    }

    [Fact]
    public void ZeroBucketsIsNotASpread() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyntheticKeyBuilder(0));

    [Fact]
    public void TheBuilderRemembersItsBucketCount() =>
        Assert.Equal(12, new SyntheticKeyBuilder(12).Buckets);
}
