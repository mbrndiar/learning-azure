namespace LearningAzure.Exercises.CosmosModeling;

/// <summary>
/// Turns an access pattern and a data model into an expected request charge, so
/// two models can be compared before either of them is built.
/// </summary>
/// <remarks>
/// The constants below are a deliberately crude model of a real account. They
/// are calibrated to the one number Cosmos defines exactly — a 1 KB point read
/// costs 1 RU — and everything else is expressed against it. The point of the
/// model is the ratio between two designs, not the absolute figure.
/// </remarks>
public sealed class QueryCostModel
{
    /// <summary>What one 1 KB document read by id and partition key costs.</summary>
    public const double PointReadRequestUnits = 1.0;

    /// <summary>The fixed cost of asking one physical partition anything.</summary>
    public const double PerPartitionOverheadRequestUnits = 2.5;

    /// <summary>What each document the engine has to look at costs.</summary>
    public const double PerDocumentExaminedRequestUnits = 0.1;

    private readonly int _physicalPartitions;

    /// <summary>Initialises a new instance of the <see cref="QueryCostModel"/> class.</summary>
    /// <param name="physicalPartitions">How many physical partitions the container has.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="physicalPartitions"/> is not positive.</exception>
    public QueryCostModel(int physicalPartitions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalPartitions);

        _physicalPartitions = physicalPartitions;
    }

    /// <summary>Gets how many physical partitions this model assumes.</summary>
    public int PhysicalPartitions => _physicalPartitions;

    /// <summary>How much of the work was wasted.</summary>
    /// <param name="documentsReturned">Documents in the answer.</param>
    /// <param name="documentsExamined">Documents the engine had to consider.</param>
    /// <returns>Documents examined per document returned.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An input is negative.</exception>
    public static double ReadAmplification(int documentsReturned, long documentsExamined)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentsReturned);
        ArgumentOutOfRangeException.ThrowIfNegative(documentsExamined);

        // GAP 6: divide examined by returned — and decide what a query that
        // returned nothing is worth.
        //
        // Returning 1.0 for the empty case, or dividing by zero and letting
        // infinity escape, both hide the most expensive query an application
        // can run: the scan that finds nothing. Report the examined count
        // itself, so the number stays monotonic in the thing that costs money.
        // See lessons/10-cosmos-modeling/README.md#read-amplification-is-the-number-to-watch
        throw new NotImplementedException(
            "GAP 6: implement QueryCostModel.ReadAmplification. "
            + "See lessons/10-cosmos-modeling/README.md#read-amplification-is-the-number-to-watch.");
    }

    /// <summary>Estimates what one execution of an access pattern costs.</summary>
    /// <param name="pattern">The access pattern.</param>
    /// <param name="documentsExamined">Documents the engine has to consider per partition asked.</param>
    /// <returns>The estimated cost.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="documentsExamined"/> is negative.</exception>
    public QueryCost Estimate(AccessPattern pattern, long documentsExamined)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentOutOfRangeException.ThrowIfNegative(documentsExamined);

        // GAP 7: a query that names a partition key touches one partition;
        // one that does not touches all of them.
        //
        // The charge is PerPartitionOverheadRequestUnits for each partition
        // touched, plus PerDocumentExaminedRequestUnits for each document
        // examined. Multiply the OVERHEAD by the partition count and not the
        // documents: the documents exist once, wherever they live. Multiplying
        // both is the mistake that makes a cost model predict absurdities and
        // get abandoned.
        // See lessons/10-cosmos-modeling/README.md#fan-out-scales-with-partitions-not-results
        throw new NotImplementedException(
            "GAP 7: implement QueryCostModel.Estimate. "
            + "See lessons/10-cosmos-modeling/README.md#fan-out-scales-with-partitions-not-results.");
    }

    /// <summary>
    /// Adds up what a whole workload costs per second, so the answer can be
    /// compared with a provisioned throughput figure.
    /// </summary>
    /// <param name="patterns">The access patterns, paired with documents examined.</param>
    /// <returns>The steady-state request units per second.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="patterns"/> is null.</exception>
    public double RequestUnitsPerSecond(
        IEnumerable<(AccessPattern Pattern, long DocumentsExamined)> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        // GAP 8: sum each pattern's charge multiplied by how often it runs.
        //
        // The expensive query is not automatically the one to fix. A 500 RU
        // report that runs once an hour costs less per second than a 3 RU read
        // that runs two hundred times a second, and provisioned throughput is a
        // rate. Charge times frequency is what turns a list of queries into a
        // number that can be compared with an RU/s allocation.
        // See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent
        throw new NotImplementedException(
            "GAP 8: implement QueryCostModel.RequestUnitsPerSecond. "
            + "See lessons/10-cosmos-modeling/README.md#throughput-is-divided-before-it-is-spent.");
    }
}
