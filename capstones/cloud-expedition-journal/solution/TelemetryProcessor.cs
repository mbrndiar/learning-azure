namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>What one processor pass did.</summary>
/// <param name="PartitionsOwned">Partitions this instance successfully claimed.</param>
/// <param name="PartitionsLost">Partitions another instance already owned.</param>
/// <param name="EventsRead">Events the stream handed back.</param>
/// <param name="EventsHandled">Events the handler actually ran for.</param>
/// <param name="ReplaysSkipped">Events at or behind the checkpoint, recognised and skipped.</param>
/// <param name="CheckpointsWritten">Checkpoints that landed.</param>
/// <param name="OwnershipLost">Checkpoints refused because the lease had moved on.</param>
public sealed record ProcessorReport(
    int PartitionsOwned,
    int PartitionsLost,
    int EventsRead,
    int EventsHandled,
    int ReplaysSkipped,
    int CheckpointsWritten,
    int OwnershipLost);

/// <summary>
/// Reads telemetry partitions under a lease, handles each event once, and records
/// how far it got in Blob Storage.
/// </summary>
/// <remarks>
/// <para>
/// Milestone 3. Three facts drive every decision here, and all three come from
/// the service:
/// </para>
/// <list type="number">
/// <item>A stream is a log with a cursor, not a queue. Nothing is removed by
/// reading it, so "where did I get to" is state the consumer owns.</item>
/// <item>That cursor is per partition. A checkpoint records one partition's
/// position and says nothing about any other.</item>
/// <item>Delivery is at least once. A restart re-reads from the last checkpoint,
/// so every event between the checkpoint and the crash arrives a second time.</item>
/// </list>
/// <para>
/// The checkpoint interval is the dial between cost and duplicate volume:
/// checkpointing every event is a Blob write per event, and checkpointing never
/// replays the whole partition after a restart. Neither end is right; the
/// interval is a decision the operator makes with the duplicate handling in view.
/// </para>
/// </remarks>
/// <param name="stream">The stream to read.</param>
/// <param name="checkpoints">The checkpoint and ownership store.</param>
/// <param name="ownerId">This processor instance's identity.</param>
/// <param name="checkpointEvery">Events handled between checkpoints.</param>
public sealed class TelemetryProcessor(
    ITelemetryFeed stream,
    ICheckpointStore checkpoints,
    string ownerId,
    int checkpointEvery = 5)
{
    private readonly ITelemetryFeed _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    private readonly ICheckpointStore _checkpoints =
        checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));

    private readonly string _ownerId = !string.IsNullOrWhiteSpace(ownerId)
        ? ownerId
        : throw new ArgumentException("A processor instance needs an identity.", nameof(ownerId));

    private readonly int _checkpointEvery = checkpointEvery > 0
        ? checkpointEvery
        : throw new ArgumentOutOfRangeException(
            nameof(checkpointEvery),
            checkpointEvery,
            "A checkpoint interval of zero would never record progress.");

    /// <summary>Reads every partition this instance can claim and handles what it finds.</summary>
    /// <param name="handler">The per-event effect.</param>
    /// <param name="cancellationToken">Cancels between and during events.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
    public async Task<ProcessorReport> RunAsync(
        Func<StreamEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        int owned = 0, lost = 0, read = 0, handled = 0, replays = 0, written = 0, ownershipLost = 0;

        var partitions = await _stream.GetPartitionIdsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var partitionId in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GAP 13 — Claim the partition before reading it.
            //
            // Two processors reading the same partition of the same consumer
            // group both handle every event and both checkpoint over each other.
            // The claim is a conditional write on a lease blob, so exactly one
            // instance wins and the loser finds out by losing rather than by
            // being told.
            var ownership = await _checkpoints
                .TryClaimAsync(partitionId, _ownerId, cancellationToken)
                .ConfigureAwait(false);

            if (ownership is null)
            {
                lost++;
                continue;
            }

            owned++;

            // GAP 14 — Resume from the checkpoint, and treat what arrives twice
            // as a replay rather than as new work.
            //
            // The service resumes from a position, and positions are coarse: a
            // checkpoint at sequence 40 with a crash at 47 redelivers 41 to 47.
            // The consumer therefore needs its own watermark, in memory for this
            // pass and in the checkpoint across passes, and must compare against
            // it BEFORE handling.
            var checkpoint = await _checkpoints
                .TryReadCheckpointAsync(partitionId, cancellationToken)
                .ConfigureAwait(false);

            var watermark = checkpoint?.SequenceNumber ?? -1;
            var sinceCheckpoint = 0;
            StreamEvent? pending = null;

            await foreach (var streamEvent in _stream
                .ReadPartitionAsync(partitionId, watermark, cancellationToken)
                .ConfigureAwait(false))
            {
                read++;

                if (streamEvent.SequenceNumber <= watermark)
                {
                    replays++;
                    continue;
                }

                await handler(streamEvent, cancellationToken).ConfigureAwait(false);
                handled++;
                watermark = streamEvent.SequenceNumber;
                pending = streamEvent;

                // GAP 15 — Checkpoint AFTER the handler succeeded, never before.
                //
                // A checkpoint written before handling turns every crash into
                // silent data loss: the position says the event was handled and
                // nothing will ever deliver it again. Written after, the same
                // crash costs a duplicate, which the ledger already absorbs.
                if (++sinceCheckpoint < _checkpointEvery)
                {
                    continue;
                }

                var renewed = await _checkpoints.TryWriteCheckpointAsync(
                    new Checkpoint(partitionId, streamEvent.SequenceNumber, streamEvent.Offset),
                    ownership,
                    cancellationToken).ConfigureAwait(false);

                if (renewed is null)
                {
                    // The lease expired and someone else owns this partition now.
                    // Anything this instance keeps handling is work the new owner
                    // is also doing, so stop rather than race it.
                    ownershipLost++;
                    pending = null;
                    break;
                }

                ownership = renewed;
                written++;
                sinceCheckpoint = 0;
                pending = null;
            }

            // A closing checkpoint for the tail the interval did not cover. Without
            // it, a clean shutdown replays up to _checkpointEvery events for no
            // reason at all.
            if (pending is not null)
            {
                var renewed = await _checkpoints.TryWriteCheckpointAsync(
                    new Checkpoint(partitionId, pending.SequenceNumber, pending.Offset),
                    ownership,
                    cancellationToken).ConfigureAwait(false);

                if (renewed is null)
                {
                    ownershipLost++;
                }
                else
                {
                    written++;
                }
            }
        }

        return new ProcessorReport(owned, lost, read, handled, replays, written, ownershipLost);
    }
}
