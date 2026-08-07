using System.Globalization;
using System.Text;

namespace LearningAzure.Exercises.EventHubsModel;

/// <summary>Decides which partition key a reading carries and where it lands.</summary>
/// <remarks>
/// The partition key is the only ordering control a producer has. It buys
/// co-location and send order for everything sharing the key, and it buys
/// nothing else — in particular it does not let you choose the partition.
/// </remarks>
public static class PartitionKeyPlanner
{
    /// <summary>The service's maximum partition-key length, in characters.</summary>
    public const int MaximumKeyLength = 128;

    /// <summary>Builds the partition key for one telemetry reading.</summary>
    /// <param name="reading">The reading about to be published.</param>
    /// <returns>The partition key.</returns>
    public static string PartitionKeyFor(TelemetryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        // GAP 1 — The key is the STATION, and nothing finer.
        //
        // The expedition's ordering requirement is "one station's readings in
        // the order that station sent them". Keying on the reading's instant,
        // or on a generated id, spreads one station across every partition and
        // destroys exactly the guarantee the key exists to provide. Keying on a
        // constant preserves ordering globally and pins the whole hub to one
        // partition server.
        return reading.StationId;
    }

    /// <summary>Reports whether a value may be used as a partition key at all.</summary>
    /// <param name="partitionKey">The candidate key.</param>
    /// <returns><c>true</c> when the service will accept it.</returns>
    public static bool IsUsableKey(string? partitionKey)
    {
        // GAP 2 — A key is a non-empty string of at most 128 characters.
        //
        // Unlike a table key there is no forbidden-character list, so the trap
        // is length rather than punctuation: a key built by concatenating a
        // station id with a correlation id passes every test until one station
        // has a long name.
        return !string.IsNullOrEmpty(partitionKey) && partitionKey.Length <= MaximumKeyLength;
    }

    /// <summary>Maps a partition key onto a partition index.</summary>
    /// <param name="partitionKey">The key.</param>
    /// <param name="partitionCount">The hub's fixed partition count.</param>
    /// <returns>The zero-based partition index the key belongs to.</returns>
    /// <remarks>
    /// The service performs this mapping itself; this reimplementation exists
    /// so the consequences of a key choice can be predicted before any event is
    /// sent, and so the properties the mapping must have are testable.
    /// </remarks>
    public static int PartitionFor(string partitionKey, int partitionCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        // GAP 3 — The hash must be STABLE ACROSS PROCESSES.
        //
        // string.GetHashCode() is deliberately randomized per process in .NET,
        // so a partition computed from it changes on every restart. Everything
        // still runs; the co-location guarantee simply stops being true, and
        // nothing anywhere reports it. FNV-1a over the UTF-8 bytes is a stable,
        // dependency-free alternative.
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        var hash = OffsetBasis;

        foreach (var octet in Encoding.UTF8.GetBytes(partitionKey))
        {
            hash ^= octet;
            hash *= Prime;
        }

        return (int)(hash % (uint)partitionCount);
    }

    /// <summary>Reports how a set of keys spreads over a hub's partitions.</summary>
    /// <param name="partitionKeys">The distinct keys the workload will use.</param>
    /// <param name="partitionCount">The hub's fixed partition count.</param>
    /// <returns>The resulting distribution.</returns>
    public static PartitionSkew Spread(IEnumerable<string> partitionKeys, int partitionCount)
    {
        ArgumentNullException.ThrowIfNull(partitionKeys);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        // GAP 4 — Count DISTINCT keys, because a key that appears twice lands
        // on the same partition twice; it does not spread further.
        var distinct = partitionKeys.Distinct(StringComparer.Ordinal).ToArray();
        var perPartition = new int[partitionCount];

        foreach (var key in distinct)
        {
            perPartition[PartitionFor(key, partitionCount)]++;
        }

        return new PartitionSkew(partitionCount, distinct.Length, perPartition);
    }

    /// <summary>Renders a skew report a human can act on.</summary>
    /// <param name="skew">The measured distribution.</param>
    /// <returns>A one-line summary.</returns>
    public static string Describe(PartitionSkew skew)
    {
        ArgumentNullException.ThrowIfNull(skew);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{skew.KeyCount} keys over {skew.PartitionCount} partitions: "
            + $"{skew.EmptyPartitions} idle, busiest holds {skew.BusiestPartition}, "
            + $"skew {skew.SkewFactor:F2}x");
    }
}
