namespace LearningAzure.Exercises.TableStorage;

/// <summary>Builds the partition and row keys the expedition's lookups need.</summary>
/// <remarks>
/// Key design is the only performance decision in Table storage. There are no
/// secondary indexes to add later: if a lookup cannot be expressed with these
/// two strings, the service scans.
/// </remarks>
public static class ObservationKeys
{
    /// <summary>Builds the partition key for a station's observations on one day.</summary>
    /// <param name="stationId">The station.</param>
    /// <param name="observedAt">Any instant on the day in question.</param>
    /// <returns>The partition key.</returns>
    public static string PartitionKeyFor(string stationId, DateTimeOffset observedAt) =>
        // GAP 1 — Station AND day, joined by '|', with the day as UTC
        // 'yyyy-MM-dd'.
        //
        // Partitioning by station alone gives one partition per station that
        // grows forever; a station reporting every minute reaches a million rows
        // in two years, and every "yesterday's readings" query scans all of it.
        // Adding the day bounds each partition at one day of traffic.
        throw new NotImplementedException(
            "GAP 1: implement ObservationKeys.PartitionKeyFor. See "
            + "lessons/07-table-storage/README.md#the-partition-key-is-the-only-decision-that-matters.");

    /// <summary>Builds the row key for one observation.</summary>
    /// <param name="observedAt">When the observation was taken.</param>
    /// <returns>The row key.</returns>
    public static string RowKeyFor(DateTimeOffset observedAt) =>
        // GAP 2 — Rows sort ascending as STRINGS, always.
        //
        // A row key of "9:05" sorts after "10:05", so an unpadded time makes
        // range queries silently wrong. Use a fixed-width, zero-padded UTC
        // format: "yyyy-MM-ddTHH:mm:ss.fffffffZ".
        throw new NotImplementedException(
            "GAP 2: implement ObservationKeys.RowKeyFor. See "
            + "lessons/07-table-storage/README.md#row-keys-sort-as-strings.");

    /// <summary>Builds a row key that sorts newest first.</summary>
    /// <param name="observedAt">When the observation was taken.</param>
    /// <returns>The inverted row key.</returns>
    /// <remarks>
    /// Ascending order is not a preference you can change: it is the storage
    /// order. To read the newest rows cheaply you invert the key instead.
    /// </remarks>
    public static string DescendingRowKeyFor(DateTimeOffset observedAt) =>
        // GAP 3 — Subtract the instant's UTC tick count from
        // DateTime.MaxValue.Ticks so a later instant produces a smaller key, and
        // format it "D19" so every key is the same width. There is no descending
        // index to ask for; this is the whole technique.
        throw new NotImplementedException(
            "GAP 3: implement ObservationKeys.DescendingRowKeyFor. See "
            + "lessons/07-table-storage/README.md#row-keys-sort-as-strings.");

    /// <summary>Reports whether a value may be used as a key at all.</summary>
    /// <param name="key">The candidate key.</param>
    /// <returns><c>true</c> when the service will accept it.</returns>
    public static bool IsUsableKey(string? key) =>
        // GAP 4 — The forbidden characters are '/', '\\', '#', '?' and control
        // characters; the empty string is not a key; the limit is 1024
        // characters. A station id or a blob name pasted straight into a key is
        // the usual way this is discovered — in production, on the one station
        // whose id contains a slash.
        throw new NotImplementedException(
            "GAP 4: implement ObservationKeys.IsUsableKey. See "
            + "lessons/07-table-storage/README.md#the-partition-key-is-the-only-decision-that-matters.");
}
