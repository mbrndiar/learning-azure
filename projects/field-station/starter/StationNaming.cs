using System.Globalization;
using System.Text.RegularExpressions;

namespace LearningAzure.Projects.FieldStation;

/// <summary>
/// Derives every name the pipeline uses from one artifact key, and rejects the
/// keys that cannot be named safely.
/// </summary>
/// <remarks>
/// <para>
/// Naming is where idempotency is won or lost. If the blob name, the work-order
/// id, and the status row key are all pure functions of the same key, a replayed
/// upload collides with itself in three places and every collision is detectable.
/// If any one of them carries a timestamp or a GUID, the replay silently becomes
/// a second observation and nothing downstream can tell.
/// </para>
/// <para>
/// The character rules are not the same in the three services, so the strictest
/// one wins: a Table key may not contain <c>/</c>, <c>\</c>, <c>#</c>, or
/// <c>?</c>, which is stricter than a blob name, which is stricter than a queue
/// message body. One validated shape satisfies all three.
/// </para>
/// </remarks>
public static partial class StationNaming
{
    /// <summary>The row key of the per-station summary row.</summary>
    /// <remarks>
    /// <c>~</c> is legal in a Table row key, is rejected by
    /// <see cref="IsValidIdentifier"/> for observation ids, and sorts after every
    /// alphanumeric row, so the summary lands at the end of the partition and can
    /// never collide with an observation.
    /// </remarks>
    public const string SummaryRowKey = "~summary";

    /// <summary>The virtual-directory prefix every artifact of one station shares.</summary>
    /// <param name="stationId">The station.</param>
    /// <returns>The blob-name prefix, ending in a slash.</returns>
    /// <exception cref="ArgumentException">The station id is not a safe identifier.</exception>
    public static string StationPrefix(string stationId)
    {
        RequireIdentifier(stationId, nameof(stationId));
        return $"stations/{stationId}/";
    }

    /// <summary>Derives the blob name for one artifact key.</summary>
    /// <param name="key">The artifact identity.</param>
    /// <returns>A deterministic, hierarchical blob name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either identifier is unsafe.</exception>
    public static string ArtifactName(ArtifactKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireIdentifier(key.StationId, nameof(key.StationId));
        RequireIdentifier(key.ObservationId, nameof(key.ObservationId));

        // GAP 1 — The name must be a pure function of the key.
        //
        // Nothing time-based, nothing random. Appending DateTimeOffset.UtcNow
        // here passes every single-run test and breaks every duplicate test in
        // this project, because the second delivery of the same observation then
        // writes a second blob instead of colliding with the first.
        //
        // Build a hierarchical name under StationPrefix so one station's
        // artifacts can be listed, and deleted, by prefix.
        throw new NotImplementedException(
            "GAP 1: derive the artifact name from the key. See "
            + "projects/field-station/README.md#milestone-1-the-domain-and-the-ports.");
    }

    /// <summary>Derives the work-order id for one artifact key and operation.</summary>
    /// <param name="key">The artifact identity.</param>
    /// <param name="operation">The operation to perform.</param>
    /// <returns>A deterministic work-order id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">An identifier or the operation is unsafe.</exception>
    public static string WorkOrderId(ArtifactKey key, string operation)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireIdentifier(key.StationId, nameof(key.StationId));
        RequireIdentifier(key.ObservationId, nameof(key.ObservationId));
        RequireIdentifier(operation, nameof(operation));

        // GAP 2 — The work-order id is the producer-chosen identity the consumer
        // deduplicates on, so it must survive a re-send.
        //
        // The queue assigns a fresh message id on every enqueue. Deduplicating on
        // that catches redelivery of one queue entry and nothing else: a retried
        // dispatch gets a new message id and slips straight through.
        //
        // Two different operations on the same observation are different work,
        // so the operation belongs in the id.
        throw new NotImplementedException(
            "GAP 2: derive the work-order id from the key and the operation. See "
            + "projects/field-station/README.md#milestone-1-the-domain-and-the-ports.");
    }

    /// <summary>Derives the status row key for one observation.</summary>
    /// <param name="observationId">The observation.</param>
    /// <returns>The row key inside the station's partition.</returns>
    /// <exception cref="ArgumentException">The observation id is unsafe.</exception>
    public static string StatusRowKey(string observationId)
    {
        RequireIdentifier(observationId, nameof(observationId));
        return observationId;
    }

    /// <summary>True when <paramref name="value"/> is safe in a blob name, a row key, and a message body.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><c>true</c> when the value is lowercase alphanumeric with internal dashes.</returns>
    public static bool IsValidIdentifier(string? value) =>
        value is not null
        && value.Length is >= 2 and <= 63
        && IdentifierPattern().IsMatch(value);

    /// <summary>Reads the station and observation back out of a derived artifact name.</summary>
    /// <param name="artifactName">A name produced by <see cref="ArtifactName"/>.</param>
    /// <returns>The key it was derived from, or <c>null</c> when the name does not fit the convention.</returns>
    public static ArtifactKey? TryParseArtifactName(string? artifactName)
    {
        if (artifactName is null)
        {
            return null;
        }

        var match = ArtifactNamePattern().Match(artifactName);
        return match.Success
            ? new ArtifactKey(match.Groups["station"].Value, match.Groups["observation"].Value)
            : null;
    }

    /// <summary>Formats a UTC instant the way every status row records time.</summary>
    /// <param name="instant">The instant to format.</param>
    /// <returns>A round-trippable, culture-independent representation.</returns>
    public static string FormatInstant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (!IsValidIdentifier(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a safe field-station identifier. Use 2-63 lowercase "
                + "letters, digits, and internal dashes, so the same value is legal as a "
                + "blob name segment, a Table row key, and a work-order id.",
                parameterName);
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^stations/(?<station>[a-z0-9]+(-[a-z0-9]+)*)/(?<observation>[a-z0-9]+(-[a-z0-9]+)*)\.json$")]
    private static partial Regex ArtifactNamePattern();
}
