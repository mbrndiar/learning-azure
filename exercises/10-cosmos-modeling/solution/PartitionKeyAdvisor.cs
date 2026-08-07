namespace LearningAzure.Exercises.CosmosModeling;

/// <summary>
/// Judges candidate partition keys against the data they would have to
/// distribute. A partition key is not chosen by reading the schema; it is
/// chosen by measuring what happens to the documents.
/// </summary>
public sealed class PartitionKeyAdvisor
{
    /// <summary>The hard ceiling on one logical partition, in bytes: 20 GB.</summary>
    public const long LogicalPartitionLimitBytes = 20L * 1024 * 1024 * 1024;

    private readonly int _minimumCardinality;
    private readonly double _maximumSkew;

    /// <summary>Initialises a new instance of the <see cref="PartitionKeyAdvisor"/> class.</summary>
    /// <param name="minimumCardinality">How many distinct key values are required.</param>
    /// <param name="maximumSkew">The largest acceptable skew ratio.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    public PartitionKeyAdvisor(int minimumCardinality, double maximumSkew)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCardinality);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSkew, 1.0);

        _minimumCardinality = minimumCardinality;
        _maximumSkew = maximumSkew;
    }

    /// <summary>Gets the cardinality floor this advisor enforces.</summary>
    public int MinimumCardinality => _minimumCardinality;

    /// <summary>Gets the skew ceiling this advisor enforces.</summary>
    public double MaximumSkew => _maximumSkew;

    /// <summary>Measures how a candidate spreads the documents it is given.</summary>
    /// <param name="candidate">The candidate to measure.</param>
    /// <returns>Its cardinality, largest partition and skew ratio.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    /// <exception cref="ArgumentException">The candidate holds no partitions.</exception>
    public static Distribution Measure(PartitionKeyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.PartitionSizes.Count == 0)
        {
            throw new ArgumentException(
                "A candidate with no logical partitions cannot be measured.",
                nameof(candidate));
        }

        // GAP 1: skew is the largest partition against the AVERAGE, not the total.
        //
        // Measuring the largest partition as a share of the total sounds
        // equivalent and is not: that number falls as cardinality rises, so a
        // key with ten thousand values and one enormous partition scores well
        // on it. Dividing by the average asks the only question that matters —
        // "how many times its fair share does the worst partition hold?" — and
        // that number is 1.0 for a perfect key regardless of cardinality.
        // See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution
        var cardinality = candidate.PartitionSizes.Count;
        var total = candidate.PartitionSizes.Values.Sum();
        var largest = candidate.PartitionSizes.Values.Max();
        var average = (double)total / cardinality;

        var skew = average == 0 ? 1.0 : largest / average;

        return new Distribution(cardinality, largest, skew);
    }

    /// <summary>
    /// Projects whether the largest logical partition will outgrow the 20 GB
    /// ceiling within the retention window.
    /// </summary>
    /// <param name="documentsPerDayInLargestPartition">Daily growth of the worst partition.</param>
    /// <param name="averageDocumentBytes">The average serialised document size.</param>
    /// <param name="retentionDays">How long documents are kept.</param>
    /// <returns>True when the partition is projected to exceed the limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An input is negative or zero.</exception>
    public static bool WillOutgrowLogicalPartition(
        long documentsPerDayInLargestPartition,
        int averageDocumentBytes,
        int retentionDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentsPerDayInLargestPartition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionDays);

        // GAP 2: the ceiling applies to ONE logical partition, not the container.
        //
        // A container holds as much as it needs; a single partition key value
        // holds 20 GB and then writes to it fail with 403 sub-status 1014, with
        // no way to fix it except changing the key and moving the data. The
        // projection is deliberately simple because the decision is: does this
        // key have an end date, yes or no.
        // See lessons/10-cosmos-modeling/README.md#a-logical-partition-is-a-ceiling
        var projectedBytes = documentsPerDayInLargestPartition
            * averageDocumentBytes
            * (long)retentionDays;

        return projectedBytes > LogicalPartitionLimitBytes;
    }

    /// <summary>Judges one candidate against this advisor's thresholds.</summary>
    /// <param name="candidate">The candidate to judge.</param>
    /// <returns>The verdict, including why it was rejected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    /// <exception cref="ArgumentException">The candidate holds no partitions.</exception>
    public Verdict Judge(PartitionKeyCandidate candidate)
    {
        var distribution = Measure(candidate);

        // GAP 3: cardinality is checked before skew, and both before size.
        //
        // The order is not cosmetic: it is the order in which the failures
        // become unfixable. A low-cardinality key cannot be rescued by more
        // throughput because Cosmos cannot split a single key value across
        // physical partitions. A skewed key can at least be spread with a
        // synthetic suffix. Reporting the first, most fundamental failure is
        // what makes the verdict actionable instead of a list of symptoms.
        var rejection = distribution.Cardinality < _minimumCardinality
            ? RejectionReason.LowCardinality
            : distribution.SkewRatio > _maximumSkew
                ? RejectionReason.Skew
                : RejectionReason.None;

        return new Verdict(candidate, distribution, rejection);
    }

    /// <summary>Picks the best usable candidate, or none.</summary>
    /// <param name="candidates">The candidates to consider.</param>
    /// <returns>The verdict on the best candidate, or null when all fail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is null.</exception>
    public Verdict? Choose(IEnumerable<PartitionKeyCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        Verdict? best = null;

        foreach (var candidate in candidates)
        {
            var verdict = Judge(candidate);

            if (!verdict.IsUsable)
            {
                continue;
            }

            // Among usable candidates the flattest one wins, and ties are broken
            // by cardinality: more distinct values give Cosmos more room to
            // split as the container grows.
            if (best is null
                || verdict.Distribution.SkewRatio < best.Distribution.SkewRatio
                || (verdict.Distribution.SkewRatio == best.Distribution.SkewRatio
                    && verdict.Distribution.Cardinality > best.Distribution.Cardinality))
            {
                best = verdict;
            }
        }

        return best;
    }
}
