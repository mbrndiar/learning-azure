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

        // GAP 5 — Check cancellation on EVERY iteration, not once before the
        // loop. A packing pass over a day of telemetry is long enough for the
        // difference to matter, and a token that is accepted and ignored is
        // worse than no token at all.
        //
        // GAP 6 — Keep one open batch PER KEY, from batchFactory(key). Batches
        // cannot be shared across keys, so a single "current batch" reused for
        // every reading either mixes keys — which the service rejects — or
        // silently drops the guarantee the key was chosen for.
        //
        // GAP 7 — TryAdd returning false means FULL, not failed. Close the
        // batch, open the next one for the same key, and re-add the reading
        // that did not fit. Ignoring the return value is how a producer loses
        // events with no exception raised anywhere.
        //
        // GAP 8 — A reading that does not fit in an EMPTY batch will never fit.
        // Retrying is an infinite loop and skipping is data loss, so throw
        // EventTooLargeException with the reading attached.
        //
        // Return every batch in the order it should be sent.
        throw new NotImplementedException(
            "GAP 5-8: implement TelemetryBatcher.Pack. See "
            + "lessons/08-event-hubs-model/README.md#a-batch-is-a-size-budget-with-one-key.");
    }
}
