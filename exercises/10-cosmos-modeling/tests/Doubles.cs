using LearningAzure.Exercises.CosmosModeling;

namespace LearningAzure.Exercises.CosmosModeling.Tests;

/// <summary>
/// Fixed data for the evaluator. Nothing here talks to Cosmos: a partition key
/// decision is arithmetic over a distribution, and arithmetic can be checked
/// offline and in milliseconds.
/// </summary>
internal static class Fixtures
{
    /// <summary>Eight stations with twenty-five readings each: a flat key.</summary>
    public static PartitionKeyCandidate ByStation()
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);

        for (var station = 1; station <= 8; station++)
        {
            sizes[$"station-{station:00}"] = 25;
        }

        return new PartitionKeyCandidate("/stationId", sizes);
    }

    /// <summary>One day holding every reading: cardinality of one.</summary>
    public static PartitionKeyCandidate ByDay() =>
        new(
            "/day",
            new Dictionary<string, long>(StringComparer.Ordinal) { ["2026-08-07"] = 200 });

    /// <summary>Many values, one of which holds most of the data: high skew.</summary>
    public static PartitionKeyCandidate ByTenant()
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["tenant-whale"] = 9_000,
        };

        for (var tenant = 1; tenant <= 99; tenant++)
        {
            sizes[$"tenant-{tenant:000}"] = 10;
        }

        return new PartitionKeyCandidate("/tenantId", sizes);
    }

    /// <summary>A candidate built from a dictionary literal.</summary>
    public static PartitionKeyCandidate Of(string path, params long[] sizes)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);

        for (var index = 0; index < sizes.Length; index++)
        {
            map[$"key-{index:000}"] = sizes[index];
        }

        return new PartitionKeyCandidate(path, map);
    }

    /// <summary>An indexing policy with no composite indexes.</summary>
    public static IndexingPolicy Policy(int indexedPaths) =>
        new(indexedPaths, Array.Empty<IReadOnlyList<string>>());

    /// <summary>An indexing policy carrying one composite index.</summary>
    public static IndexingPolicy PolicyWithComposite(int indexedPaths, params string[] properties) =>
        new(indexedPaths, new IReadOnlyList<string>[] { properties });
}
