using LearningAzure.Exercises.CosmosModeling;

namespace LearningAzure.Exercises.CosmosModeling.Tests;

/// <summary>
/// Checks the measurement a partition key decision rests on. If these numbers
/// are wrong, every later judgement is confidently wrong.
/// </summary>
public sealed class PartitionKeyAdvisorTests
{
    [Fact]
    public void AFlatKeyHasSkewOfExactlyOne()
    {
        var measured = PartitionKeyAdvisor.Measure(Fixtures.ByStation());

        Assert.Equal(8, measured.Cardinality);
        Assert.Equal(25, measured.LargestPartition);
        Assert.Equal(1.0, measured.SkewRatio, 6);
    }

    [Fact]
    public void SkewIsMeasuredAgainstTheAverageNotTheTotal()
    {
        // Four partitions, one holding half the documents. Against the total
        // that is 50%, which sounds survivable. Against the average it is 2x,
        // which is what it actually is.
        var measured = PartitionKeyAdvisor.Measure(Fixtures.Of("/k", 100, 34, 33, 33));

        Assert.Equal(2.0, measured.SkewRatio, 6);
    }

    [Fact]
    public void SkewDoesNotImproveJustBecauseCardinalityRose()
    {
        var few = PartitionKeyAdvisor.Measure(Fixtures.Of("/k", 100, 10, 10, 10));
        var many = PartitionKeyAdvisor.Measure(
            Fixtures.Of("/k", 100, 10, 10, 10, 10, 10, 10, 10, 10, 10));

        // A share-of-total measure would have fallen from 77% to 53% here while
        // the hot partition did not move at all.
        Assert.True(many.SkewRatio > few.SkewRatio);
    }

    [Fact]
    public void TheLargestPartitionIsReportedInDocuments()
    {
        var measured = PartitionKeyAdvisor.Measure(Fixtures.ByTenant());

        Assert.Equal(9_000, measured.LargestPartition);
        Assert.Equal(100, measured.Cardinality);
    }

    [Fact]
    public void ACandidateWithNoPartitionsCannotBeMeasured()
    {
        var empty = new PartitionKeyCandidate(
            "/nothing",
            new Dictionary<string, long>(StringComparer.Ordinal));

        Assert.Throws<ArgumentException>(() => PartitionKeyAdvisor.Measure(empty));
    }

    [Fact]
    public void MeasurementRefusesANullCandidate() =>
        Assert.Throws<ArgumentNullException>(() => PartitionKeyAdvisor.Measure(null!));

    [Fact]
    public void ALowCardinalityKeyIsRejectedBeforeAnythingElseIsConsidered()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 4, maximumSkew: 2.0);

        var verdict = advisor.Judge(Fixtures.ByDay());

        Assert.Equal(RejectionReason.LowCardinality, verdict.Rejection);
        Assert.False(verdict.IsUsable);
    }

    [Fact]
    public void ASkewedKeyWithPlentyOfValuesIsStillRejected()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 8, maximumSkew: 2.0);

        var verdict = advisor.Judge(Fixtures.ByTenant());

        Assert.Equal(RejectionReason.Skew, verdict.Rejection);
    }

    [Fact]
    public void AFlatKeyWithEnoughValuesIsAccepted()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 8, maximumSkew: 2.0);

        var verdict = advisor.Judge(Fixtures.ByStation());

        Assert.Equal(RejectionReason.None, verdict.Rejection);
        Assert.True(verdict.IsUsable);
    }

    [Fact]
    public void TheVerdictCarriesTheCandidateItJudged()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 2, maximumSkew: 2.0);
        var candidate = Fixtures.ByStation();

        Assert.Same(candidate, advisor.Judge(candidate).Candidate);
    }

    [Fact]
    public void CardinalityIsCheckedBeforeSkew()
    {
        // This candidate fails both bounds. Cardinality is the unfixable one, so
        // it is the one worth reporting.
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 10, maximumSkew: 1.1);

        var verdict = advisor.Judge(Fixtures.Of("/k", 100, 1));

        Assert.Equal(RejectionReason.LowCardinality, verdict.Rejection);
    }

    [Fact]
    public void ChoosingPicksTheFlattestUsableCandidate()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 4, maximumSkew: 3.0);

        var chosen = advisor.Choose(
        [
            Fixtures.Of("/lumpy", 40, 20, 20, 20),
            Fixtures.ByStation(),
        ]);

        Assert.NotNull(chosen);
        Assert.Equal("/stationId", chosen.Candidate.Path);
    }

    [Fact]
    public void ChoosingBreaksTiesOnCardinality()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 2, maximumSkew: 2.0);

        var chosen = advisor.Choose(
        [
            Fixtures.Of("/four", 10, 10, 10, 10),
            Fixtures.Of("/eight", 10, 10, 10, 10, 10, 10, 10, 10),
        ]);

        Assert.NotNull(chosen);
        Assert.Equal("/eight", chosen.Candidate.Path);
    }

    [Fact]
    public void ChoosingReturnsNothingWhenEveryCandidateFails()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 1_000, maximumSkew: 1.5);

        Assert.Null(advisor.Choose([Fixtures.ByStation(), Fixtures.ByDay(), Fixtures.ByTenant()]));
    }

    [Fact]
    public void ChoosingRefusesANullSequence()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 2, maximumSkew: 2.0);

        Assert.Throws<ArgumentNullException>(() => advisor.Choose(null!));
    }

    [Fact]
    public void ASkewCeilingOfOneOrLessIsMeaningless() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PartitionKeyAdvisor(4, 1.0));

    [Fact]
    public void ACardinalityFloorOfZeroIsMeaningless() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PartitionKeyAdvisor(0, 2.0));

    [Fact]
    public void TheLogicalPartitionLimitIsTwentyGigabytes() =>
        Assert.Equal(21_474_836_480, PartitionKeyAdvisor.LogicalPartitionLimitBytes);

    [Fact]
    public void APartitionThatStaysSmallDoesNotOutgrowTheLimit()
    {
        // 1,000 documents a day at 1 KB, kept for a year: about 365 MB.
        Assert.False(PartitionKeyAdvisor.WillOutgrowLogicalPartition(1_000, 1_024, 365));
    }

    [Fact]
    public void APartitionThatGrowsWithTheWholeSystemDoesOutgrowIt()
    {
        // A key with cardinality 1 collects everything: 10 million documents a
        // day at 1 KB reaches 20 GB in three days.
        Assert.True(PartitionKeyAdvisor.WillOutgrowLogicalPartition(10_000_000, 1_024, 3));
    }

    [Fact]
    public void TheLimitIsAboutOnePartitionNotTheContainer()
    {
        // Exactly at the ceiling is not over it.
        const long documentsPerDay = 20L * 1024 * 1024;

        Assert.False(
            PartitionKeyAdvisor.WillOutgrowLogicalPartition(documentsPerDay, 1_024, 1));

        Assert.True(
            PartitionKeyAdvisor.WillOutgrowLogicalPartition(documentsPerDay + 1, 1_024, 1));
    }

    [Fact]
    public void RetentionIsPartOfTheProjection()
    {
        Assert.False(PartitionKeyAdvisor.WillOutgrowLogicalPartition(1_000_000, 1_024, 20));
        Assert.True(PartitionKeyAdvisor.WillOutgrowLogicalPartition(1_000_000, 1_024, 21));
    }

    [Fact]
    public void AZeroLengthRetentionIsNotAProjection() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PartitionKeyAdvisor.WillOutgrowLogicalPartition(1_000, 1_024, 0));

    [Fact]
    public void AZeroSizedDocumentIsNotAProjection() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PartitionKeyAdvisor.WillOutgrowLogicalPartition(1_000, 0, 30));

    [Fact]
    public void TheAdvisorRemembersItsThresholds()
    {
        var advisor = new PartitionKeyAdvisor(minimumCardinality: 12, maximumSkew: 3.5);

        Assert.Equal(12, advisor.MinimumCardinality);
        Assert.Equal(3.5, advisor.MaximumSkew);
    }
}
