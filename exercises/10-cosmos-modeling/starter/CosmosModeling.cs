namespace LearningAzure.Exercises.CosmosModeling;

/// <summary>
/// A candidate partition key, described by how the data actually distributes
/// across it rather than by what the property is called.
/// </summary>
/// <param name="Path">The partition key path, for example <c>/stationId</c>.</param>
/// <param name="PartitionSizes">
/// How many documents each distinct key value holds. The dictionary is the
/// measurement; every judgement in this exercise is derived from it.
/// </param>
public sealed record PartitionKeyCandidate(
    string Path,
    IReadOnlyDictionary<string, long> PartitionSizes);

/// <summary>
/// What a candidate partition key does to the data, in the three numbers that
/// decide whether it survives contact with production.
/// </summary>
/// <param name="Cardinality">How many distinct key values exist.</param>
/// <param name="LargestPartition">Documents in the biggest logical partition.</param>
/// <param name="SkewRatio">
/// The largest partition divided by the average partition. A perfectly even key
/// scores 1.0; a key that puts everything in one partition scores the
/// cardinality.
/// </param>
public sealed record Distribution(int Cardinality, long LargestPartition, double SkewRatio);

/// <summary>An access pattern the model has to serve, and how often.</summary>
/// <param name="Name">What the application calls this query.</param>
/// <param name="FiltersOnPartitionKey">
/// Whether the query can name a single partition key value.
/// </param>
/// <param name="DocumentsReturned">How many documents the answer contains.</param>
/// <param name="ExecutionsPerSecond">How often the application runs it.</param>
public sealed record AccessPattern(
    string Name,
    bool FiltersOnPartitionKey,
    int DocumentsReturned,
    double ExecutionsPerSecond);

/// <summary>What one execution of a query is expected to cost.</summary>
/// <param name="RequestUnits">The estimated charge.</param>
/// <param name="PartitionsTouched">How many physical partitions answered.</param>
/// <param name="DocumentsExamined">How many documents had to be considered.</param>
public sealed record QueryCost(double RequestUnits, int PartitionsTouched, long DocumentsExamined);

/// <summary>Why a partition key candidate was rejected.</summary>
public enum RejectionReason
{
    /// <summary>The candidate was not rejected.</summary>
    None = 0,

    /// <summary>Too few distinct values to spread across physical partitions.</summary>
    LowCardinality,

    /// <summary>One logical partition takes a disproportionate share.</summary>
    Skew,

    /// <summary>A logical partition is projected to exceed the 20 GB ceiling.</summary>
    LogicalPartitionLimit,
}

/// <summary>A verdict on one candidate partition key.</summary>
/// <param name="Candidate">The candidate that was judged.</param>
/// <param name="Distribution">How the data spreads across it.</param>
/// <param name="Rejection">Why it fails, or <see cref="RejectionReason.None"/>.</param>
public sealed record Verdict(
    PartitionKeyCandidate Candidate,
    Distribution Distribution,
    RejectionReason Rejection)
{
    /// <summary>Gets a value indicating whether the candidate is usable.</summary>
    public bool IsUsable => Rejection == RejectionReason.None;
}

/// <summary>How a container's throughput is provisioned.</summary>
/// <param name="ProvisionedRequestUnits">
/// Manual RU/s, or the autoscale maximum when <paramref name="IsAutoscale"/> is true.
/// </param>
/// <param name="IsAutoscale">Whether the allocation autoscales.</param>
/// <param name="PhysicalPartitions">How many physical partitions the container has.</param>
public sealed record ThroughputPlan(
    int ProvisionedRequestUnits,
    bool IsAutoscale,
    int PhysicalPartitions);

/// <summary>What an indexing policy indexes.</summary>
/// <param name="IndexedPaths">How many document paths are indexed.</param>
/// <param name="CompositeIndexes">
/// The composite indexes, each listed as the ordered property names it covers.
/// </param>
public sealed record IndexingPolicy(
    int IndexedPaths,
    IReadOnlyList<IReadOnlyList<string>> CompositeIndexes);
