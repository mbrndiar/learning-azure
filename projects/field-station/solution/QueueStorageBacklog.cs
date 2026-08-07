using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace LearningAzure.Projects.FieldStation;

/// <summary>Implements <see cref="IWorkBacklog"/> over a real Storage queue pair.</summary>
/// <remarks>
/// <para>
/// A Storage queue has no dead-letter queue, so the poison queue is an ordinary
/// second queue this adapter owns. "Quarantine" is therefore two operations that
/// must both happen — copy aside, then delete — and the order matters: copying
/// first can duplicate a poison record, deleting first can lose the evidence
/// entirely.
/// </para>
/// <para>
/// Message bodies are Base64-encoded, which is the SDK default and the form the
/// 64 KiB limit applies to. Work orders are pointers, so they stay far below it.
/// </para>
/// </remarks>
/// <param name="work">The dispatch queue.</param>
/// <param name="poison">The queue quarantined messages are moved to.</param>
public sealed class QueueStorageBacklog(QueueClient work, QueueClient poison) : IWorkBacklog
{
    /// <summary>The dispatch queue.</summary>
    public QueueClient Work { get; } = work ?? throw new ArgumentNullException(nameof(work));

    /// <summary>The queue quarantined messages are moved to.</summary>
    public QueueClient Poison { get; } = poison ?? throw new ArgumentNullException(nameof(poison));

    /// <inheritdoc />
    public async Task SendAsync(WorkOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        await Work.SendMessageAsync(WorkOrderCodec.Encode(order), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceivedWork>> ReceiveAsync(
        int maxMessages,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);

        // 32 is the service maximum for one receive. Asking for more is a 400,
        // not a larger batch.
        var response = await Work.ReceiveMessagesAsync(
            Math.Min(maxMessages, 32),
            visibilityTimeout,
            cancellationToken).ConfigureAwait(false);

        return [.. response.Value.Select(message => new ReceivedWork(
            message.MessageId,
            message.PopReceipt,
            message.DequeueCount,
            message.Body.ToString()))];
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ReceivedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            // The pop receipt proves THIS receive. A worker holding a stale
            // receipt cannot delete the message another worker is now handling,
            // which is the protection that makes a visibility timeout safe.
            await Work.DeleteMessageAsync(work.MessageId, work.PopReceipt, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            // MessageNotFound means the visibility timeout expired and someone
            // else already settled it. The work is done either way; failing here
            // would turn a benign race into an alert.
        }
    }

    /// <inheritdoc />
    public async Task QuarantineAsync(ReceivedWork work, PoisonRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(record);

        var envelope = JsonSerializer.Serialize(new
        {
            record.MessageId,
            record.DequeueCount,
            record.Reason,
            Body = work.Body,
        });

        // Copy first, delete second. A crash between them leaves a duplicate
        // poison record, which a human can read twice; the other order leaves
        // nothing to read at all.
        await Poison.SendMessageAsync(envelope, cancellationToken).ConfigureAwait(false);
        await DeleteAsync(work, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ApproximateDepthAsync(CancellationToken cancellationToken)
    {
        QueueProperties properties = await Work.GetPropertiesAsync(cancellationToken).ConfigureAwait(false);

        // The count includes invisible messages: depth is "work not finished",
        // never "work not started".
        return properties.ApproximateMessagesCount;
    }
}
