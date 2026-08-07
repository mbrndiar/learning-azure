using System.Runtime.CompilerServices;

namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>
/// The loop every Event Hubs consumer eventually writes: handle, decide whether
/// to record progress, and stop cleanly when asked.
/// </summary>
public sealed class EventPump
{
    private readonly CheckpointLedger _ledger;
    private readonly IdempotentProjection _projection;
    private readonly CheckpointPolicy _policy;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="EventPump"/> class.</summary>
    /// <param name="ledger">Where progress is recorded.</param>
    /// <param name="projection">What the events are applied to.</param>
    /// <param name="policy">When progress is recorded.</param>
    /// <param name="timeProvider">The clock the time bound is measured against.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public EventPump(
        CheckpointLedger ledger,
        IdempotentProjection projection,
        CheckpointPolicy policy,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _ledger = ledger;
        _projection = projection;
        _policy = policy;
        _timeProvider = timeProvider;
    }

    /// <summary>Gets the reasons checkpoints were written, in order.</summary>
    public List<CheckpointReason> CheckpointReasons { get; } = [];

    /// <summary>Pumps a stream of events until it ends or the caller gives up.</summary>
    /// <param name="events">The delivered events, in arrival order.</param>
    /// <param name="cancellationToken">Stops the pump.</param>
    /// <returns>What the run did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is <see langword="null"/>.</exception>
    public async Task<PumpResult> RunAsync(
        IAsyncEnumerable<HandledEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var pending = new Dictionary<string, PartitionProgress>(StringComparer.Ordinal);
        var cancelled = false;
        var checkpoints = 0;

        await foreach (var handled in events.WithCancellation(CancellationToken.None).ConfigureAwait(false))
        {
            // GAP 12: cancellation is checked every iteration, and it stops the
            // pump WITHOUT throwing.
            //
            // A consumer that throws out of its loop on shutdown loses the
            // chance to record what it already did, and the next instance
            // replays it. Checking once before the loop is worse still: the
            // pump then runs to completion no matter what the caller asked for.
            // See lessons/09-event-hubs-processing/README.md#stopping-is-part-of-the-contract
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            _projection.Apply(handled);

            // GAP 13: progress advances for duplicates too.
            //
            // A recognised duplicate is a handled event: its effect is already
            // in the projection, so the position past it is safe to record.
            // Advancing only on newly applied events pins the checkpoint behind
            // a run of duplicates and replays them again on every restart.
            if (!pending.TryGetValue(handled.PartitionId, out var progress))
            {
                progress = new PartitionProgress(_timeProvider.GetUtcNow());
                pending[handled.PartitionId] = progress;
            }

            progress.HandledSinceCheckpoint++;
            progress.LastSequenceNumber = handled.SequenceNumber;

            var reason = _policy.Evaluate(
                progress.HandledSinceCheckpoint,
                _timeProvider.GetUtcNow() - progress.LastCheckpointAt,
                isPartitionClosing: false);

            if (reason != CheckpointReason.None && Checkpoint(handled.PartitionId, progress, reason))
            {
                checkpoints++;
            }
        }

        // GAP 14: the shutdown checkpoint. Whatever was handled but not yet
        // recorded is recorded now, under the closing reason, for every
        // partition this pump touched. Skipping it is what turns a routine
        // deployment into a burst of duplicates.
        foreach (var (partitionId, progress) in pending.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var reason = _policy.Evaluate(
                progress.HandledSinceCheckpoint,
                _timeProvider.GetUtcNow() - progress.LastCheckpointAt,
                isPartitionClosing: true);

            if (reason != CheckpointReason.None && Checkpoint(partitionId, progress, reason))
            {
                checkpoints++;
            }
        }

        return new PumpResult(_projection.Applied, _projection.Skipped, checkpoints, cancelled);
    }

    /// <summary>Turns a list into an async stream, for tests and the lesson companion.</summary>
    /// <param name="events">The events to stream.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The events, one at a time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is <see langword="null"/>.</exception>
    public static async IAsyncEnumerable<HandledEvent> Stream(
        IEnumerable<HandledEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var handled in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return handled;
            await Task.Yield();
        }
    }

    private bool Checkpoint(string partitionId, PartitionProgress progress, CheckpointReason reason)
    {
        var recorded = _ledger.Record(partitionId, progress.LastSequenceNumber);

        progress.HandledSinceCheckpoint = 0;
        progress.LastCheckpointAt = _timeProvider.GetUtcNow();

        if (recorded)
        {
            CheckpointReasons.Add(reason);
        }

        return recorded;
    }

    private sealed class PartitionProgress
    {
        public PartitionProgress(DateTimeOffset startedAt) => LastCheckpointAt = startedAt;

        public int HandledSinceCheckpoint { get; set; }

        public long LastSequenceNumber { get; set; }

        public DateTimeOffset LastCheckpointAt { get; set; }
    }
}
