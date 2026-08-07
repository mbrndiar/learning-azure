using System.Text.Json;
using System.Text.Json.Serialization;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>Encodes and decodes the two payloads that travel between services.</summary>
/// <remarks>
/// <para>
/// A telemetry event carries the reading itself, because the reading is small and
/// the stream is the system of record for it. A queue message carries a
/// <em>pointer</em>: the artifact stays in Blob Storage and the message names it,
/// which keeps every message far below the 64 KiB post-encoding ceiling.
/// </para>
/// <para>
/// Both decoders are strict. Deserialization happily produces an object whose
/// every field is null when the body is <c>{}</c>, and letting that through moves
/// the failure to the first place a field is dereferenced — usually the registry,
/// where it claims the id "" for every malformed payload on the wire.
/// </para>
/// </remarks>
public static class JournalCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Encodes one reading as a telemetry event body.</summary>
    /// <param name="reading">The reading to encode.</param>
    /// <returns>The event body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is <c>null</c>.</exception>
    public static string EncodeReading(TelemetryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return JsonSerializer.Serialize(reading, Options);
    }

    /// <summary>Decodes one reading from a telemetry event body.</summary>
    /// <param name="body">The event body.</param>
    /// <returns>The decoded reading.</returns>
    /// <exception cref="FormatException">The body is not a complete reading.</exception>
    /// <exception cref="JsonException">The body is not valid JSON.</exception>
    public static TelemetryReading DecodeReading(string body)
    {
        // GAP 4 — A partially valid payload is still poison.
        //
        // Every field is checked here, at the boundary, because this is the last
        // place the failure is cheap. One layer down it is a null reference in
        // the middle of a partition, with a checkpoint decision waiting on it.
        TelemetryReading? reading;
        try
        {
            reading = JsonSerializer.Deserialize<TelemetryReading>(body, Options);
        }
        catch (JsonException error)
        {
            // A field of the wrong shape — a quoted "NaN" where a number belongs,
            // an object where a string belongs — is the same class of defect as a
            // missing field, and the caller has one thing to catch either way.
            throw new FormatException($"The event body is not a well-formed reading: {error.Message}", error);
        }

        if (reading is null)
        {
            throw new FormatException("The event body decoded to null.");
        }

        if (!ExpeditionNaming.IsValidIdentifier(reading.StationId)
            || !ExpeditionNaming.IsValidIdentifier(reading.ObservationId))
        {
            throw new FormatException(
                "The event body is missing a usable station id or observation id.");
        }

        if (double.IsNaN(reading.Celsius) || double.IsInfinity(reading.Celsius))
        {
            throw new FormatException($"The reading carries a non-finite temperature: {reading.Celsius}.");
        }

        if (reading.ObservedUtc == default)
        {
            // An absent timestamp deserialises to the zero instant, which is a
            // plausible-looking value that every later stage would believe.
            throw new FormatException("The event body carries no observation timestamp.");
        }

        return reading;
    }

    /// <summary>Encodes one work order as a queue message body.</summary>
    /// <param name="order">The order to encode.</param>
    /// <returns>The message body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is <c>null</c>.</exception>
    public static string EncodeWorkOrder(ArtifactWorkOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return JsonSerializer.Serialize(order, Options);
    }

    /// <summary>Decodes one work order from a queue message body.</summary>
    /// <param name="body">The message body.</param>
    /// <returns>The decoded order.</returns>
    /// <exception cref="FormatException">The body is not a complete work order.</exception>
    /// <exception cref="JsonException">The body is not valid JSON.</exception>
    public static ArtifactWorkOrder DecodeWorkOrder(string body)
    {
        ArtifactWorkOrder? order;
        try
        {
            order = JsonSerializer.Deserialize<ArtifactWorkOrder>(body, Options);
        }
        catch (JsonException error)
        {
            throw new FormatException($"The message body is not a well-formed work order: {error.Message}", error);
        }

        if (order is null)
        {
            throw new FormatException("The message body decoded to null.");
        }

        if (string.IsNullOrWhiteSpace(order.WorkOrderId)
            || string.IsNullOrWhiteSpace(order.ArtifactName)
            || !ExpeditionNaming.IsValidIdentifier(order.StationId)
            || !ExpeditionNaming.IsValidIdentifier(order.ObservationId)
            || !ExpeditionNaming.IsValidIdentifier(order.Operation))
        {
            throw new FormatException("The message body is missing a required work-order field.");
        }

        if (!string.Equals(
                order.WorkOrderId,
                ExpeditionNaming.WorkOrderId(order.Key, order.Operation),
                StringComparison.Ordinal))
        {
            // A work-order id that does not match its own fields cannot be
            // deduplicated against anything, so it is a defect at the producer,
            // not a transient condition at the consumer.
            throw new FormatException(
                $"Work order id '{order.WorkOrderId}' is not derived from its own station, "
                + "observation, and operation.");
        }

        return order;
    }

    /// <summary>Renders the artifact body one work order preserves.</summary>
    /// <param name="order">The order being worked.</param>
    /// <param name="reading">The reading the artifact records.</param>
    /// <param name="renderedUtc">When the artifact was rendered.</param>
    /// <returns>The artifact bytes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
    public static byte[] RenderArtifact(
        ArtifactWorkOrder order,
        TelemetryReading reading,
        DateTimeOffset renderedUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(reading);

        var document = new
        {
            workOrderId = order.WorkOrderId,
            stationId = reading.StationId,
            observationId = reading.ObservationId,
            celsius = reading.Celsius,
            observedUtc = ExpeditionNaming.FormatInstant(reading.ObservedUtc),
            renderedUtc = ExpeditionNaming.FormatInstant(renderedUtc),
        };

        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }
}
