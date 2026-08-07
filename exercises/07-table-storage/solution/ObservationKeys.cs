using System.Globalization;

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
    public static string PartitionKeyFor(string stationId, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        // GAP 1 — Station AND day, not station alone.
        //
        // Partitioning by station alone gives one partition per station that
        // grows forever; a station reporting every minute reaches a million rows
        // in two years, and every "yesterday's readings" query scans all of it.
        // Adding the day bounds each partition at one day of traffic.
        var day = observedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{stationId}|{day}";
    }

    /// <summary>Builds the row key for one observation.</summary>
    /// <param name="observedAt">When the observation was taken.</param>
    /// <returns>The row key.</returns>
    public static string RowKeyFor(DateTimeOffset observedAt)
    {
        // GAP 2 — Rows sort ascending as STRINGS, always.
        //
        // A row key of "9:05" sorts after "10:05", so an unpadded time makes
        // range queries silently wrong. A fixed-width UTC round-trip format
        // sorts chronologically because it is fixed width and zero padded.
        return observedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>Builds a row key that sorts newest first.</summary>
    /// <param name="observedAt">When the observation was taken.</param>
    /// <returns>The inverted row key.</returns>
    /// <remarks>
    /// Ascending order is not a preference you can change: it is the storage
    /// order. To read the newest rows cheaply you invert the key instead.
    /// </remarks>
    public static string DescendingRowKeyFor(DateTimeOffset observedAt)
    {
        // GAP 3 — Subtract the tick count from the maximum so that a later
        // instant produces a smaller key. There is no descending index to ask
        // for; this is the whole technique.
        var inverted = DateTime.MaxValue.Ticks - observedAt.ToUniversalTime().UtcTicks;
        return inverted.ToString("D19", CultureInfo.InvariantCulture);
    }

    /// <summary>Reports whether a value may be used as a key at all.</summary>
    /// <param name="key">The candidate key.</param>
    /// <returns><c>true</c> when the service will accept it.</returns>
    public static bool IsUsableKey(string? key)
    {
        // GAP 4 — The forbidden characters are '/', '\\', '#', '?', control
        // characters, and the empty string. A station id or a blob name pasted
        // straight into a key is the usual way this is discovered — in
        // production, on the one station whose id contains a slash.
        if (string.IsNullOrEmpty(key) || key.Length > 1024)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (character is '/' or '\\' or '#' or '?' || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
