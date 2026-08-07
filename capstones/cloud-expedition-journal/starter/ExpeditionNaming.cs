using System.Globalization;
using System.Text.RegularExpressions;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>
/// Derives every name the journal uses from one observation key, and rejects the
/// keys that cannot be named safely across five services at once.
/// </summary>
/// <remarks>
/// <para>
/// Five services, five naming rules, one identifier. A Table key may not contain
/// <c>/</c>, <c>\</c>, <c>#</c>, or <c>?</c>; a Cosmos item id may not contain
/// <c>/</c>, <c>\</c>, <c>#</c>, or <c>?</c> either; a blob name may contain all
/// of them; an Event Hubs partition key is a free string. The strictest rule wins,
/// so one validated shape satisfies all five and nothing downstream has to
/// re-validate.
/// </para>
/// <para>
/// The partition key deserves its own paragraph. It decides which stream
/// partition a reading lands on, and therefore which readings are ordered
/// relative to each other. Keying on the station gives per-station order — which
/// is the only order the journal actually needs — while keying on the observation
/// would spread one station across every partition and destroy it.
/// </para>
/// </remarks>
public static partial class ExpeditionNaming
{
    /// <summary>The row key of the per-station watermark row.</summary>
    /// <remarks>
    /// <c>~</c> is legal in a Table row key, is rejected by
    /// <see cref="IsValidIdentifier"/> for observation ids, and sorts after every
    /// alphanumeric row, so the watermark lands at the end of the partition and
    /// can never collide with an observation.
    /// </remarks>
    public const string WatermarkRowKey = "~watermark";

    /// <summary>The blob-name prefix every ownership and checkpoint record shares.</summary>
    public const string CheckpointPrefix = "checkpoints/";

    /// <summary>The virtual-directory prefix every artifact of one station shares.</summary>
    /// <param name="stationId">The station.</param>
    /// <returns>The blob-name prefix, ending in a slash.</returns>
    /// <exception cref="ArgumentException">The station id is not a safe identifier.</exception>
    public static string StationPrefix(string stationId)
    {
        RequireIdentifier(stationId, nameof(stationId));
        return $"journal/{stationId}/";
    }

    /// <summary>Derives the stream partition key for one reading.</summary>
    /// <param name="key">The observation identity.</param>
    /// <returns>The key the producer stamps on the batch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The station id is unsafe.</exception>
    public static string PartitionKey(ObservationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // GAP 1 — The partition key decides what stays ordered.
        //
        // Event Hubs guarantees order inside a partition and nothing across
        // partitions. Keying on the station keeps one station's readings in one
        // partition, in the order the station sent them, which is the only order
        // the journal needs. Keying on the observation would spread a single
        // station over every partition and make "the last reading from this
        // station" a lie that is right most of the time.
        //
        // Validate with RequireIdentifier before returning: an unusable key
        // becomes an unusable blob name and an unusable row key four services later.
        throw new NotImplementedException(
            "GAP 1: derive the partition key from the observation key. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-1-the-domain-and-the-ports.");
    }

    /// <summary>Derives the artifact blob name for one observation.</summary>
    /// <param name="key">The observation identity.</param>
    /// <returns>A deterministic, hierarchical blob name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either identifier is unsafe.</exception>
    public static string ArtifactName(ObservationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireIdentifier(key.StationId, nameof(key.StationId));
        RequireIdentifier(key.ObservationId, nameof(key.ObservationId));

        // GAP 2 — Every derived name is a pure function of the key.
        //
        // Nothing time-based, nothing random. A name carrying DateTimeOffset.UtcNow
        // passes every single-run check and breaks every duplicate check, because
        // the second delivery of the same observation then writes a second blob
        // instead of colliding with the first.
        //
        // Build a hierarchical name under StationPrefix so one station's artifacts
        // can be listed, and deleted, by prefix.
        throw new NotImplementedException(
            "GAP 2: derive the artifact name from the observation key. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-1-the-domain-and-the-ports.");
    }

    /// <summary>Derives the work-order id for one observation and operation.</summary>
    /// <param name="key">The observation identity.</param>
    /// <param name="operation">The operation to perform.</param>
    /// <returns>A deterministic work-order id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">An identifier or the operation is unsafe.</exception>
    public static string WorkOrderId(ObservationKey key, string operation)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireIdentifier(key.StationId, nameof(key.StationId));
        RequireIdentifier(key.ObservationId, nameof(key.ObservationId));
        RequireIdentifier(operation, nameof(operation));

        return $"{key.StationId}.{key.ObservationId}.{operation}";
    }

    /// <summary>Derives the Cosmos item id for one observation.</summary>
    /// <param name="key">The observation identity.</param>
    /// <returns>The item id, unique inside the station's logical partition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either identifier is unsafe.</exception>
    public static string JournalItemId(ObservationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireIdentifier(key.StationId, nameof(key.StationId));
        RequireIdentifier(key.ObservationId, nameof(key.ObservationId));

        // GAP 3 — A Cosmos item is identified by (partition key, id), not by id.
        //
        // The station is already the partition key, so repeating it inside the id
        // buys nothing and costs bytes on every index entry. What the id must be
        // is stable: derive it from the observation so a replayed reading
        // addresses the document it already wrote, which is what makes the
        // projection idempotent without a read-then-write check.
        throw new NotImplementedException(
            "GAP 3: derive the Cosmos item id from the observation key. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-1-the-domain-and-the-ports.");
    }

    /// <summary>Derives the blob name holding one partition's ownership and checkpoint record.</summary>
    /// <param name="partitionId">The stream partition.</param>
    /// <returns>The checkpoint blob name.</returns>
    /// <exception cref="ArgumentException">The partition id is not numeric.</exception>
    public static string CheckpointName(string partitionId)
    {
        if (string.IsNullOrWhiteSpace(partitionId) || !partitionId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"'{partitionId}' is not an Event Hubs partition id; the service numbers partitions from 0.",
                nameof(partitionId));
        }

        return $"{CheckpointPrefix}{partitionId}";
    }

    /// <summary>True when <paramref name="value"/> is safe in every service this capstone touches.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><c>true</c> when the value is lowercase alphanumeric with internal dashes.</returns>
    public static bool IsValidIdentifier(string? value) =>
        value is not null
        && value.Length is >= 2 and <= 63
        && IdentifierPattern().IsMatch(value);

    /// <summary>Reads the observation back out of a derived artifact name.</summary>
    /// <param name="artifactName">A name produced by <see cref="ArtifactName"/>.</param>
    /// <returns>The key it was derived from, or <c>null</c> when the name does not fit the convention.</returns>
    public static ObservationKey? TryParseArtifactName(string? artifactName)
    {
        if (artifactName is null)
        {
            return null;
        }

        var match = ArtifactNamePattern().Match(artifactName);
        return match.Success
            ? new ObservationKey(match.Groups["station"].Value, match.Groups["observation"].Value)
            : null;
    }

    /// <summary>Formats a UTC instant the way every stored record records time.</summary>
    /// <param name="instant">The instant to format.</param>
    /// <returns>A round-trippable, culture-independent representation.</returns>
    public static string FormatInstant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (!IsValidIdentifier(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a safe expedition identifier. Use 2-63 lowercase letters, "
                + "digits, and internal dashes, so the same value is legal as a blob name "
                + "segment, a Table row key, a Cosmos item id, and a partition key.",
                parameterName);
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^journal/(?<station>[a-z0-9]+(-[a-z0-9]+)*)/(?<observation>[a-z0-9]+(-[a-z0-9]+)*)\.json$")]
    private static partial Regex ArtifactNamePattern();
}
