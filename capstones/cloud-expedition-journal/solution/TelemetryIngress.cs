namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>One batch of readings that share a partition key.</summary>
/// <param name="PartitionKey">The key every reading in the batch carries.</param>
/// <param name="Readings">The readings, in the order they were offered.</param>
public sealed record TelemetryBatch(string PartitionKey, IReadOnlyList<TelemetryReading> Readings);

/// <summary>Groups readings into keyed batches and publishes them.</summary>
/// <remarks>
/// <para>
/// Milestone 3. Two rules decide every batch this class produces, and both come
/// from the service rather than from taste.
/// </para>
/// <para>
/// <b>One partition key per batch.</b> A batch is stamped with a partition key,
/// not each event inside it, so a batch mixing two stations either has to be sent
/// unkeyed — losing per-station order — or split. Splitting is the only option
/// that keeps the order guarantee the journal depends on.
/// </para>
/// <para>
/// <b>A batch has a size ceiling.</b> The service rejects an oversized batch
/// outright, so the producer decides where a batch ends. The ceiling here is
/// expressed in events because the readings are fixed-shape; a producer with
/// variable payloads uses <c>EventDataBatch.TryAdd</c>, which measures bytes and
/// is what the adapter actually calls.
/// </para>
/// </remarks>
/// <param name="stream">The stream to publish to.</param>
/// <param name="maxEventsPerBatch">How many readings one batch may carry.</param>
public sealed class TelemetryIngress(ITelemetryFeed stream, int maxEventsPerBatch = 32)
{
    private readonly ITelemetryFeed _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    private readonly int _maxEventsPerBatch = maxEventsPerBatch > 0
        ? maxEventsPerBatch
        : throw new ArgumentOutOfRangeException(
            nameof(maxEventsPerBatch),
            maxEventsPerBatch,
            "A batch must carry at least one event.");

    /// <summary>Groups readings into batches that are legal to send.</summary>
    /// <param name="readings">The readings to group.</param>
    /// <returns>Batches, each carrying exactly one partition key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readings"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A reading carries an unsafe station id.</exception>
    public IReadOnlyList<TelemetryBatch> Plan(IReadOnlyList<TelemetryReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        // GAP 5 — A batch carries one partition key and at most _maxEventsPerBatch
        // events.
        //
        // Grouping is stable on purpose: readings keep the order they were offered
        // in, within their key, because that order is the one the station observed
        // and the only one the consumer can reconstruct.
        var batches = new List<TelemetryBatch>();
        foreach (var group in readings.GroupBy(reading => ExpeditionNaming.PartitionKey(reading.Key), StringComparer.Ordinal))
        {
            var pending = new List<TelemetryReading>(_maxEventsPerBatch);
            foreach (var reading in group)
            {
                pending.Add(reading);
                if (pending.Count == _maxEventsPerBatch)
                {
                    batches.Add(new TelemetryBatch(group.Key, pending));
                    pending = new List<TelemetryReading>(_maxEventsPerBatch);
                }
            }

            if (pending.Count > 0)
            {
                batches.Add(new TelemetryBatch(group.Key, pending));
            }
        }

        return batches;
    }

    /// <summary>Plans and publishes one set of readings.</summary>
    /// <param name="readings">The readings to publish.</param>
    /// <param name="cancellationToken">Cancels the publish between batches.</param>
    /// <returns>What was sent, by partition key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readings"/> is <c>null</c>.</exception>
    public async Task<PublishReceipt> PublishAsync(
        IReadOnlyList<TelemetryReading> readings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var batches = Plan(readings);
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _stream.PublishAsync(batch.Readings, cancellationToken).ConfigureAwait(false);
            byKey[batch.PartitionKey] = byKey.GetValueOrDefault(batch.PartitionKey) + batch.Readings.Count;
        }

        return new PublishReceipt(batches.Count, readings.Count, byKey);
    }
}
