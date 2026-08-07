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
    public static string Encode(WorkOrder order) =>
        // GAP 1 — Serialize to JSON, Base64-encode it, THEN check the limit.
        //
        // The limit applies to the ENCODED bytes, and Base64 inflates by a third.
        // Checking the JSON length instead lets a 60 KiB payload through and the
        // service rejects the 80 KiB message it becomes.
        //
        // Throw ArgumentException when it does not fit, and say what to do about
        // it: put the payload in a blob and queue its name.
        throw new NotImplementedException(
            "GAP 1: implement WorkOrderCodec.Encode. See "
            + "lessons/06-queue-storage/README.md#a-message-is-a-pointer-not-a-payload.");

    /// <summary>Decodes a queue message body back into a work order.</summary>
    /// <param name="body">The Base64 message body.</param>
    /// <returns>The work order.</returns>
    public static WorkOrder Decode(string body) =>
        // GAP 2a — The inverse of Encode. A body that is not valid Base64, or
        // whose JSON is not a work order, must throw rather than return a
        // half-populated object: the dispatcher relies on that to quarantine it.
        throw new NotImplementedException(
            "GAP 2: implement WorkOrderCodec.Decode. See "
            + "lessons/06-queue-storage/README.md#a-message-is-a-pointer-not-a-payload.");

    /// <summary>Reports whether a payload of <paramref name="payloadBytes"/> fits once encoded.</summary>
    /// <param name="payloadBytes">Raw payload size before encoding.</param>
    /// <returns><c>true</c> when the Base64 form still fits.</returns>
    public static bool Fits(int payloadBytes) =>
        // GAP 2b — Base64 is 4 bytes out for every 3 in, rounded up to a
        // multiple of 4. The usable payload is therefore about 48 KiB, not 64.
        throw new NotImplementedException(
            "GAP 2: implement WorkOrderCodec.Fits. See "
            + "lessons/06-queue-storage/README.md#a-message-is-a-pointer-not-a-payload.");
}
