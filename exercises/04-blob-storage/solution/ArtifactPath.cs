using System.Globalization;

namespace LearningAzure.Exercises.BlobStorage;

/// <summary>Builds and reads the blob names the expedition stores artifacts under.</summary>
/// <remarks>
/// Blob Storage has a flat namespace. A "directory" is a naming convention plus a
/// prefix scan, which is why the name has to be designed rather than concatenated.
/// </remarks>
public static class ArtifactPath
{
    /// <summary>Root of every observation artifact.</summary>
    public const string Root = "observations";

    private const int SegmentCount = 6;

    /// <summary>Builds the blob name for one artifact.</summary>
    /// <param name="stationId">The station that produced the artifact.</param>
    /// <param name="observedAt">When the observation was taken.</param>
    /// <param name="fileName">The artifact's file name, such as <c>calving.jpg</c>.</param>
    /// <returns><c>observations/{station}/{yyyy}/{MM}/{dd}/{HHmmss}-{fileName}</c>.</returns>
    /// <exception cref="ArgumentException">Any component is blank, or the file name contains a slash.</exception>
    public static string For(string stationId, DateTimeOffset observedAt, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{fileName}' contains a path separator, which would invent an extra virtual "
                + "directory level and break every prefix this scheme produces.",
                nameof(fileName));
        }

        var utc = observedAt.ToUniversalTime();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Root}/{stationId}/{utc:yyyy}/{utc:MM}/{utc:dd}/{utc:HHmmss}-{fileName}");
    }

    /// <summary>Returns the prefix that lists everything a station recorded on a day.</summary>
    /// <param name="stationId">The station to scan.</param>
    /// <param name="day">The day to scan, in UTC.</param>
    /// <returns>A prefix ending in <c>/</c>.</returns>
    public static string DayPrefix(string stationId, DateTimeOffset day)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        var utc = day.ToUniversalTime();

        // A prefix scan is a string comparison and nothing more. Zero-padding is
        // what keeps day 1 from also matching days 10-19, and the trailing slash
        // is what keeps the component boundary explicit.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Root}/{stationId}/{utc:yyyy}/{utc:MM}/{utc:dd}/");
    }

    /// <summary>Returns the prefix that lists everything a station ever recorded.</summary>
    /// <param name="stationId">The station to scan.</param>
    /// <returns>A prefix ending in <c>/</c>.</returns>
    public static string StationPrefix(string stationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        // Same reason: without the slash, 'station-b' also matches 'station-bravo'.
        return $"{Root}/{stationId}/";
    }

    /// <summary>Extracts the station id from a blob name this class produced.</summary>
    /// <param name="blobName">A full blob name.</param>
    /// <returns>The station id.</returns>
    /// <exception cref="FormatException">The name does not follow the scheme.</exception>
    public static string StationOf(string blobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var segments = blobName.Split('/');
        if (segments.Length != SegmentCount || !string.Equals(segments[0], Root, StringComparison.Ordinal))
        {
            // Returning segment 1 of a foreign name would attribute an artifact
            // to the wrong station, which is worse than failing.
            throw new FormatException(
                $"'{blobName}' does not follow the artifact naming scheme "
                + $"'{Root}/{{station}}/{{yyyy}}/{{MM}}/{{dd}}/{{HHmmss}}-{{file}}'.");
        }

        return segments[1];
    }
}
