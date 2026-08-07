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

    /// <summary>Builds the blob name for one artifact.</summary>
    /// <param name="stationId">The station that produced the artifact.</param>
    /// <param name="observedAt">When the observation was taken.</param>
    /// <param name="fileName">The artifact's file name, such as <c>calving.jpg</c>.</param>
    /// <returns><c>observations/{station}/{yyyy}/{MM}/{dd}/{HHmmss}-{fileName}</c>.</returns>
    /// <exception cref="ArgumentException">Any component is blank, or the file name contains a slash.</exception>
    public static string For(string stationId, DateTimeOffset observedAt, string fileName) =>
        // GAP 1 — Build the name.
        //
        //   observations/{stationId}/{yyyy}/{MM}/{dd}/{HHmmss}-{fileName}
        //
        // Use the UTC components of observedAt and format them with
        // CultureInfo.InvariantCulture. A name built with the ambient culture
        // sorts differently on a machine with a non-Gregorian calendar, and blob
        // listings are ordered lexicographically — so the ordering the whole
        // scheme depends on would silently change per machine.
        //
        // Reject a blank component, and reject a fileName containing '/' or '\':
        // a slash inside the file name invents a virtual directory level and
        // breaks every prefix this class produces.
        throw new NotImplementedException(
            "GAP 1: implement ArtifactPath.For. See "
            + "lessons/04-blob-storage/README.md#the-namespace-is-flat.");

    /// <summary>Returns the prefix that lists everything a station recorded on a day.</summary>
    /// <param name="stationId">The station to scan.</param>
    /// <param name="day">The day to scan, in UTC.</param>
    /// <returns>A prefix ending in <c>/</c>.</returns>
    public static string DayPrefix(string stationId, DateTimeOffset day) =>
        // GAP 2 — Return observations/{stationId}/{yyyy}/{MM}/{dd}/ — zero-padded
        // and with the trailing slash. A prefix scan is a string comparison and
        // nothing more: unpadded, the prefix for day 1 also matches days 10-19,
        // and without the slash a station id that is a prefix of another one
        // matches both.
        throw new NotImplementedException(
            "GAP 2: implement ArtifactPath.DayPrefix. See "
            + "lessons/04-blob-storage/README.md#the-namespace-is-flat.");

    /// <summary>Returns the prefix that lists everything a station ever recorded.</summary>
    /// <param name="stationId">The station to scan.</param>
    /// <returns>A prefix ending in <c>/</c>.</returns>
    public static string StationPrefix(string stationId) =>
        // GAP 3 — Return observations/{stationId}/ — with the trailing slash, for
        // the same reason: without it, 'station-b' also matches 'station-bravo'.
        throw new NotImplementedException(
            "GAP 3: implement ArtifactPath.StationPrefix. See "
            + "lessons/04-blob-storage/README.md#the-namespace-is-flat.");

    /// <summary>Extracts the station id from a blob name this class produced.</summary>
    /// <param name="blobName">A full blob name.</param>
    /// <returns>The station id.</returns>
    /// <exception cref="FormatException">The name does not follow the scheme.</exception>
    public static string StationOf(string blobName) =>
        // GAP 4 — Split on '/' and return the second segment, after checking the
        // first is Root and that there are exactly six segments. A name that does
        // not match the scheme is a FormatException, not a best guess: silently
        // returning segment 1 of a foreign name attributes an artifact to the
        // wrong station.
        throw new NotImplementedException(
            "GAP 4: implement ArtifactPath.StationOf. See "
            + "lessons/04-blob-storage/README.md#the-namespace-is-flat.");
}
