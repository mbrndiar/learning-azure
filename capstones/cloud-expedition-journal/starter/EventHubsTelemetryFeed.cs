using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>Translates between the domain's reading and the transport's event.</summary>
/// <remarks>
/// <para>
/// Milestone 3. AMQP cannot be scripted over HTTP, so this mapper exists as a
/// separate, pure type: it is the part of the Event Hubs adapter that carries
/// decisions worth grading, and keeping it free of a client makes it gradeable
/// offline with <c>EventHubsModelFactory</c>.
/// </para>
/// <para>
/// The event's body is the reading. Its <em>system</em> properties — partition
/// id, sequence number, offset — are the stream's own coordinates, assigned by
/// the service and meaningless to invent. Everything replay and checkpointing do
/// downstream depends on the mapper carrying them across faithfully rather than
/// recomputing them from the payload.
/// </para>
/// </remarks>
public static class TelemetryEventMapper
{
    /// <summary>The application property carrying the routing key, for diagnostics.</summary>
    public const string PartitionKeyProperty = "expedition-partition-key";

    /// <summary>The application property carrying the station, for filtering without a body read.</summary>
    public const string StationProperty = "expedition-station";

    /// <summary>Builds the event that carries one reading.</summary>
    /// <param name="reading">The reading to send.</param>
    /// <returns>The event, with the routing key repeated as an application property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is <c>null</c>.</exception>
    public static EventData ToEventData(TelemetryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var data = new EventData(Encoding.UTF8.GetBytes(JournalCodec.EncodeReading(reading)))
        {
            ContentType = "application/json",
            MessageId = $"{reading.StationId}.{reading.ObservationId}",
        };

        // The partition key itself is a send-time argument, not a body field, so
        // it is repeated here purely so an operator reading one event can tell
        // where it was meant to land.
        data.Properties[PartitionKeyProperty] = ExpeditionNaming.PartitionKey(reading.Key);
        data.Properties[StationProperty] = reading.StationId;
        return data;
    }

    /// <summary>Rebuilds the domain event from what the service delivered.</summary>
    /// <param name="partitionEvent">The delivered event.</param>
    /// <returns>The stream event, or <c>null</c> when the read simply timed out.</returns>
    /// <exception cref="FormatException">The body is not a well-formed reading.</exception>
    public static StreamEvent? ToStreamEvent(PartitionEvent partitionEvent)
    {
        // A read that reaches its maximum wait time yields an empty event rather
        // than ending the enumeration. Treating that as data is how a drained
        // partition turns into a NullReferenceException.
        if (partitionEvent.Data is null)
        {
            return null;
        }

        // GAP 21 — Carry the service's coordinates, do not invent them.
        //
        // The sequence number and offset are how the partition addresses itself:
        // they are what a checkpoint stores and what a resume seeks to. A counter
        // maintained by the consumer looks identical on a first run and diverges
        // permanently after the first restart, duplicate, or rebalance.
        // Decode the body with JournalCodec.DecodeReading, then take the partition
        // id from partitionEvent.Partition and the sequence number, offset, and
        // partition key from partitionEvent.Data. EventData.OffsetString can be
        // absent on some emulators; fall back to the sequence number rather than
        // to a value the consumer made up.
        throw new NotImplementedException(
            "GAP 21: rebuild the stream event from the delivered event. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-3-the-telemetry-pipeline.");
    }
}

/// <summary>Implements <see cref="ITelemetryFeed"/> over a real event hub.</summary>
/// <remarks>
/// <para>
/// The producer sends one batch per partition key, because a batch is the unit
/// the service routes: mixing keys in one batch is either rejected or silently
/// unroutable, depending on how the batch was created.
/// </para>
/// <para>
/// The consumer here reads a partition directly rather than using the processor
/// library, because the capstone owns its checkpoint store and its ownership
/// rules — see <see cref="BlobCheckpointVault"/>. The processor library is the
/// right choice in production; writing the mechanism once is how it stops being
/// magic.
/// </para>
/// </remarks>
/// <param name="producer">The producer readings are published through.</param>
/// <param name="consumer">The consumer partitions are read through.</param>
/// <param name="maximumWaitTime">How long a drained partition waits before the read ends.</param>
public sealed class EventHubsTelemetryFeed(
    EventHubProducerClient producer,
    EventHubConsumerClient consumer,
    TimeSpan maximumWaitTime) : ITelemetryFeed
{
    /// <summary>The producer readings are published through.</summary>
    public EventHubProducerClient Producer { get; } =
        producer ?? throw new ArgumentNullException(nameof(producer));

    /// <summary>The consumer partitions are read through.</summary>
    public EventHubConsumerClient Consumer { get; } =
        consumer ?? throw new ArgumentNullException(nameof(consumer));

    /// <summary>How long a drained partition waits before the read ends.</summary>
    public TimeSpan MaximumWaitTime { get; } = maximumWaitTime > TimeSpan.Zero
        ? maximumWaitTime
        : throw new ArgumentOutOfRangeException(nameof(maximumWaitTime));

    /// <inheritdoc />
    public async Task<PublishReceipt> PublishAsync(
        IReadOnlyList<TelemetryReading> readings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var batches = 0;
        var sent = 0;
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var group in readings.GroupBy(reading => ExpeditionNaming.PartitionKey(reading.Key)))
        {
            var pending = group.ToList();

            while (pending.Count > 0)
            {
                using var batch = await Producer
                    .CreateBatchAsync(new CreateBatchOptions { PartitionKey = group.Key }, cancellationToken)
                    .ConfigureAwait(false);

                var added = 0;
                foreach (var reading in pending)
                {
                    // TryAdd is the size check. A batch that is asked to hold
                    // more than the service allows refuses, and the caller's job
                    // is to start another one rather than to guess a count.
                    if (!batch.TryAdd(TelemetryEventMapper.ToEventData(reading)))
                    {
                        break;
                    }

                    added++;
                }

                if (added == 0)
                {
                    throw new InvalidOperationException(
                        $"Reading '{pending[0].ObservationId}' does not fit in an empty batch, so no batch size "
                        + "will ever accept it.");
                }

                await Producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);
                batches++;
                sent += added;
                byKey[group.Key] = byKey.GetValueOrDefault(group.Key) + added;
                pending.RemoveRange(0, added);
            }
        }

        return new PublishReceipt(batches, sent, byKey);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetPartitionIdsAsync(CancellationToken cancellationToken) =>
        await Consumer.GetPartitionIdsAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> ReadPartitionAsync(
        string partitionId,
        long afterSequenceNumber,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);

        // Exclusive from the checkpointed position, or the earliest retained
        // event when there is no checkpoint. "Latest" would look correct on an
        // idle stream and quietly drop everything published during a restart.
        var position = afterSequenceNumber < 0
            ? EventPosition.Earliest
            : EventPosition.FromSequenceNumber(afterSequenceNumber, isInclusive: false);

        var options = new ReadEventOptions { MaximumWaitTime = MaximumWaitTime };

        await foreach (var partitionEvent in Consumer
            .ReadEventsFromPartitionAsync(partitionId, position, options, cancellationToken)
            .ConfigureAwait(false))
        {
            var mapped = TelemetryEventMapper.ToStreamEvent(partitionEvent);
            if (mapped is null)
            {
                // Drained. A batch job stops here; a long-running service would
                // keep waiting, which is the only difference between the two.
                yield break;
            }

            yield return mapped;
        }
    }
}
