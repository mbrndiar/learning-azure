using System.Text;
using System.Text.Json;

namespace LearningAzure.Exercises.QueueStorage;

/// <summary>Encodes work orders as queue messages, within the service's limits.</summary>
/// <remarks>
/// A queue message is small on purpose. The 64 KiB ceiling is the service
/// telling you what a message is for: a pointer to work, not the work itself.
/// </remarks>
public static class WorkOrderCodec
{
    /// <summary>The maximum size of a queue message, in bytes, after encoding.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>Serializer settings, fixed so encoded messages are stable across runs.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Encodes <paramref name="order"/> as a Base64 queue message body.</summary>
    /// <param name="order">The work order.</param>
    /// <returns>A Base64 string the queue will accept unchanged.</returns>
    /// <exception cref="ArgumentException">The encoded message exceeds the service limit.</exception>
    public static string Encode(WorkOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        // GAP 1 — Base64 first, then check the limit.
        //
        // The limit applies to the ENCODED bytes, and Base64 inflates by a third.
        // Checking the JSON length instead lets a 60 KiB payload through and the
        // service rejects the 80 KiB message it becomes.
        var json = JsonSerializer.SerializeToUtf8Bytes(order, SerializerOptions);
        var encoded = Convert.ToBase64String(json);

        if (Encoding.UTF8.GetByteCount(encoded) > MaxMessageBytes)
        {
            throw new ArgumentException(
                $"The encoded message is {Encoding.UTF8.GetByteCount(encoded)} bytes, over the "
                + $"{MaxMessageBytes}-byte queue limit. Put the payload in a blob and queue its name.",
                nameof(order));
        }

        return encoded;
    }

    /// <summary>Decodes a queue message body back into a work order.</summary>
    /// <param name="body">The Base64 message body.</param>
    /// <returns>The work order.</returns>
    public static WorkOrder Decode(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var json = Convert.FromBase64String(body);
        return JsonSerializer.Deserialize<WorkOrder>(json, SerializerOptions)
            ?? throw new FormatException("The message body decoded to null.");
    }

    /// <summary>Reports whether a payload of <paramref name="payloadBytes"/> fits once encoded.</summary>
    /// <param name="payloadBytes">Raw payload size before encoding.</param>
    /// <returns><c>true</c> when the Base64 form still fits.</returns>
    public static bool Fits(int payloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);

        // GAP 2 — Base64 is 4 bytes out for every 3 in, rounded up to a
        // multiple of 4. The usable payload is therefore about 48 KiB, not 64.
        var encoded = 4L * ((payloadBytes + 2) / 3);
        return encoded <= MaxMessageBytes;
    }
}
