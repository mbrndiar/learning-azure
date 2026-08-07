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

        // GAP 1: report the cardinality, the largest logical partition, and the
        // skew ratio.
        //
        // Cardinality is how many distinct key values exist. The largest
        // partition is the biggest document count. The skew ratio is the
        // interesting one: measure the largest partition against the AVERAGE
        // partition, not against the total. A share-of-total measure falls as
        // cardinality rises, so a key with ten thousand values and one enormous
        // partition scores well on it and still takes the system down. Against
        // the average, a perfect key scores exactly 1.0 at any cardinality.
        // See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution
        throw new NotImplementedException(
            "GAP 1: implement PartitionKeyAdvisor.Measure. "
            + "See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution.");
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

        // GAP 2: project the largest partition forward and compare it with the
        // 20 GB ceiling.
        //
        // Multiply the daily document count by the document size and by the
        // retention window, and answer whether the result exceeds
        // LogicalPartitionLimitBytes. Note that the ceiling applies to ONE
        // logical partition and not to the container: a container holds as much
        // as it needs, but a single partition key value that reaches 20 GB
        // starts refusing writes with 403 sub-status 1014, and the only repair
        // is a new partition key and a migration. Exactly at the limit is not
        // over it.
        // See lessons/10-cosmos-modeling/README.md#a-logical-partition-is-a-ceiling
        throw new NotImplementedException(
            "GAP 2: implement PartitionKeyAdvisor.WillOutgrowLogicalPartition. "
            + "See lessons/10-cosmos-modeling/README.md#a-logical-partition-is-a-ceiling.");
    }

    /// <summary>Judges one candidate against this advisor's thresholds.</summary>
    /// <param name="candidate">The candidate to judge.</param>
    /// <returns>The verdict, including why it was rejected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    /// <exception cref="ArgumentException">The candidate holds no partitions.</exception>
    public Verdict Judge(PartitionKeyCandidate candidate)
    {
        var distribution = Measure(candidate);

        // GAP 3: reject on cardinality first, then on skew.
        //
        // The order is not cosmetic: it is the order in which the failures
        // become unfixable. A key below the cardinality floor cannot be rescued
        // by more throughput, because Cosmos cannot split one key value across
        // physical partitions. A skewed key can at least be spread with a
        // synthetic suffix. When a candidate fails both, report the cardinality.
        // See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution
        throw new NotImplementedException(
            "GAP 3: implement PartitionKeyAdvisor.Judge. "
            + "See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution.");
    }

    /// <summary>Picks the best usable candidate, or none.</summary>
    /// <param name="candidates">The candidates to consider.</param>
    /// <returns>The verdict on the best candidate, or null when all fail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is null.</exception>
    public Verdict? Choose(IEnumerable<PartitionKeyCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Judge every candidate, discard the unusable ones, and return the
        // flattest of what is left. Break ties on cardinality: more distinct
        // values give Cosmos more room to split as the container grows. Return
        // null when nothing survives — an honest "none of these" is a result.
        throw new NotImplementedException(
            "Implement PartitionKeyAdvisor.Choose once GAP 3 works. "
            + "See lessons/10-cosmos-modeling/README.md#cardinality-is-not-distribution.");
    }
}
