namespace LearningAzure.Exercises.EventHubsProcessing;

/// <summary>
/// Decides when to spend a blob write on recording progress. Checkpointing too
/// often is a cost and a throughput ceiling; checkpointing too rarely is a
/// duplicate count.
/// </summary>
public sealed class CheckpointPolicy
{
    private readonly int _everyEvents;
    private readonly TimeSpan _everyInterval;

    /// <summary>Initialises a new instance of the <see cref="CheckpointPolicy"/> class.</summary>
    /// <param name="everyEvents">Checkpoint after this many handled events.</param>
    /// <param name="everyInterval">Checkpoint after this much time, even if fewer events arrived.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    public CheckpointPolicy(int everyEvents, TimeSpan everyInterval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(everyEvents);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(everyInterval, TimeSpan.Zero);

        _everyEvents = everyEvents;
        _everyInterval = everyInterval;
    }

    /// <summary>Gets the event bound.</summary>
    public int EveryEvents => _everyEvents;

    /// <summary>Gets the time bound.</summary>
    public TimeSpan EveryInterval => _everyInterval;

    /// <summary>Decides whether a checkpoint is due.</summary>
    /// <param name="handledSinceLastCheckpoint">Events handled since the last checkpoint.</param>
    /// <param name="elapsedSinceLastCheckpoint">Time since the last checkpoint.</param>
    /// <param name="isPartitionClosing">Whether the partition is being released right now.</param>
    /// <returns>The reason a checkpoint is due, or <see cref="CheckpointReason.None"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="handledSinceLastCheckpoint"/> is negative.</exception>
    public CheckpointReason Evaluate(
        int handledSinceLastCheckpoint,
        TimeSpan elapsedSinceLastCheckpoint,
        bool isPartitionClosing)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(handledSinceLastCheckpoint);

        // GAP 4 — Handle the closing case first: return PartitionClosing when
        // the partition is being released AND something was handled since the
        // last checkpoint, and None when nothing was. Nothing handled means
        // nothing to record, whatever the reason.
        //
        // GAP 5 — Otherwise apply the two bounds in this order: EveryEvents
        // first, then EveryInterval. A policy with only the event bound leaves a
        // partition that receives one event an hour permanently uncheckpointed;
        // a policy with only the time bound lets a busy partition build up an
        // unbounded replay.
        throw new NotImplementedException(
            "GAP 4: implement CheckpointPolicy.Evaluate. See "
            + "lessons/09-event-hubs-processing/README.md#the-two-bounds.");
    }
}
