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
    public Task<PumpResult> RunAsync(
        IAsyncEnumerable<HandledEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Read the stream with CancellationToken.None and check the token
        // yourself, so a cancelled run can still record what it did.
        //
        // GAP 12 — Check cancellationToken at the top of EVERY iteration. When
        // it is set, set Cancelled on the result and BREAK; do not throw. A
        // consumer that throws out of its loop on shutdown loses the chance to
        // record what it already did, and the next instance replays it.
        //
        // GAP 13 — Apply every event to the projection, then advance that
        // partition's pending progress — INCLUDING for events the projection
        // recognised as duplicates. A recognised duplicate is a handled event;
        // its effect is already applied, so the position past it is safe to
        // record. Ask the policy after each event with the per-partition handled
        // count and the elapsed time since that partition's last checkpoint, and
        // call _ledger.Record when it says so. Reset the per-partition counter
        // and clock only when a checkpoint is actually written, and append the
        // reason to CheckpointReasons only when the ledger accepted the write.
        //
        // GAP 14 — After the loop — cancelled or not — ask the policy once more
        // per touched partition with isPartitionClosing true, and checkpoint
        // whatever is outstanding. Skipping this is what turns a routine
        // deployment into a burst of duplicates.
        //
        // Return Applied and Skipped from the projection, the number of
        // checkpoints written, and whether the run was cancelled.
        throw new NotImplementedException(
            "GAP 12: implement EventPump.RunAsync. See "
            + "lessons/09-event-hubs-processing/README.md#stopping-is-part-of-the-contract.");
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
}
