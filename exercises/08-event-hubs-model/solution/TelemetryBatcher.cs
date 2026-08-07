namespace LearningAzure.Exercises.EventHubsModel;

/// <summary>Raised when one event cannot fit in an empty batch.</summary>
/// <remarks>
/// This is the failure a producer must never swallow. An event larger than the
/// publication limit will never be sent by any retry, so dropping it silently
/// is data loss with no diagnostic anywhere.
/// </remarks>
public sealed class EventTooLargeException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public EventTooLargeException()
        : base("An event does not fit in an empty batch.")
    {
    }

    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="message">The message.</param>
    public EventTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public EventTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The reading that could not be batched.</summary>
    public TelemetryReading? Reading { get; init; }
}

/// <summary>Packs telemetry readings into bounded, single-key batches.</summary>
/// <remarks>
/// A batch is a size budget with one partition key. Both constraints are
/// structural: exceeding either is not a slow path, it is an impossible send.
/// </remarks>
public static class TelemetryBatcher
{
    /// <summary>Packs readings into batches ready to send.</summary>
    /// <param name="readings">The readings to publish, in send order.</param>
    /// <param name="batchFactory">Creates an empty batch for a partition key.</param>
    /// <param name="cancellationToken">Cancels the packing loop.</param>
    /// <returns>Every batch produced, in the order it should be sent.</returns>
    /// <exception cref="EventTooLargeException">
    /// An individual reading does not fit in an empty batch.
    /// </exception>
    public static IReadOnlyList<IEventBatch> Pack(
        IReadOnlyList<TelemetryReading> readings,
        EventBatchFactory batchFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(batchFactory);

        var batches = new List<IEventBatch>();
        var open = new Dictionary<string, IEventBatch>(StringComparer.Ordinal);

        foreach (var reading in readings)
        {
            // GAP 5 — Cancellation is checked on every iteration, not once
            // before the loop. A packing pass over a day of telemetry is long
            // enough for the difference to matter, and a token that is accepted
            // and ignored is worse than no token at all.
            cancellationToken.ThrowIfCancellationRequested();

            var key = PartitionKeyPlanner.PartitionKeyFor(reading);

            if (!open.TryGetValue(key, out var batch))
            {
                // GAP 6 — One open batch PER KEY. Batches cannot be shared
                // across keys, so a single "current batch" reused for every
                // reading either mixes keys — which the service rejects — or
                // silently drops the guarantee the key was chosen for.
                batch = batchFactory(key);
                open[key] = batch;
                batches.Add(batch);
            }

            if (batch.TryAdd(reading.BodyBytes))
            {
                continue;
            }

            // GAP 7 — TryAdd returning false means FULL, not failed. Close this
            // batch, open the next one for the same key, and re-add the reading
            // that did not fit. Ignoring the return value is how a producer
            // loses events with no exception raised anywhere.
            var replacement = batchFactory(key);
            open[key] = replacement;
            batches.Add(replacement);

            if (!replacement.TryAdd(reading.BodyBytes))
            {
                // GAP 8 — An event that does not fit in an EMPTY batch will
                // never fit. Retrying is an infinite loop and skipping is data
                // loss, so it is surfaced.
                throw new EventTooLargeException(
                    $"A {reading.BodyBytes}-byte reading from {reading.StationId} does not fit in an "
                    + $"empty {replacement.MaximumSizeInBytes}-byte batch.")
                {
                    Reading = reading,
                };
            }
        }

        return batches;
    }
}
