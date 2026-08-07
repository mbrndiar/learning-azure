using System.Text.Json;
using System.Text.Json.Serialization;

namespace LearningAzure.Projects.FieldStation;

/// <summary>Encodes and decodes work orders on the wire.</summary>
/// <remarks>
/// A queue message is a pointer to work, not the work. The artifact stays in Blob
/// Storage and the message carries its name, which keeps every message far below
/// the 64 KiB post-encoding ceiling and keeps the queue cheap to drain.
/// </remarks>
public static class WorkOrderCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Encodes one work order as compact JSON.</summary>
    /// <param name="order">The order to encode.</param>
    /// <returns>The message body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is <c>null</c>.</exception>
    public static string Encode(WorkOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return JsonSerializer.Serialize(order, Options);
    }

    /// <summary>Decodes one work order from a message body.</summary>
    /// <param name="body">The message body.</param>
    /// <returns>The decoded order.</returns>
    /// <exception cref="FormatException">The body is not a complete work order.</exception>
    /// <exception cref="JsonException">The body is not valid JSON.</exception>
    public static WorkOrder Decode(string body)
    {
        // GAP 3 — A partially valid message is still poison.
        //
        // JSON deserialization happily produces a WorkOrder whose every field is
        // null when the body is `{}`. Letting that through moves the failure to
        // the first place a field is dereferenced — usually the ledger, where it
        // claims the id "..." for every malformed message on the queue.
        var order = JsonSerializer.Deserialize<WorkOrder>(body, Options)
            ?? throw new FormatException("The message body decoded to null.");

        if (string.IsNullOrWhiteSpace(order.WorkOrderId)
            || string.IsNullOrWhiteSpace(order.StationId)
            || string.IsNullOrWhiteSpace(order.ObservationId)
            || string.IsNullOrWhiteSpace(order.ArtifactName)
            || string.IsNullOrWhiteSpace(order.Operation))
        {
            throw new FormatException("The message body is missing a required work-order field.");
        }

        if (!StationNaming.IsValidIdentifier(order.StationId)
            || !StationNaming.IsValidIdentifier(order.ObservationId)
            || !StationNaming.IsValidIdentifier(order.Operation)
            || order.WorkOrderId != StationNaming.WorkOrderId(order.Key, order.Operation))
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
}
