namespace LearningAzure.Exercises.QueueStorage;

/// <summary>Decides how long a message should stay invisible while it is worked on.</summary>
/// <remarks>
/// The visibility timeout is the single most misconfigured queue setting. Too
/// short and every slow item is delivered twice while the first attempt is still
/// running. Too long and a crashed consumer parks the work for that long.
/// </remarks>
public static class VisibilityPlanner
{
    /// <summary>The service's maximum visibility timeout.</summary>
    public static TimeSpan MaximumVisibility { get; } = TimeSpan.FromDays(7);

    /// <summary>The safety factor applied to the expected handler duration.</summary>
    public const double SafetyFactor = 3.0;

    /// <summary>Chooses a visibility timeout for a handler that usually takes <paramref name="expected"/>.</summary>
    /// <param name="expected">Typical handler duration.</param>
    /// <returns>A timeout with headroom, capped at the service maximum.</returns>
    public static TimeSpan Choose(TimeSpan expected) =>
        // GAP 3 — Headroom, not a guess.
        //
        // Setting the timeout to the expected duration means half of all runs
        // exceed it. The tail is what causes duplicate delivery, so the timeout
        // has to cover the tail (SafetyFactor), and then be capped at
        // MaximumVisibility. A non-positive expected duration is nonsense.
        throw new NotImplementedException(
            "GAP 3: implement VisibilityPlanner.Choose. See "
            + "lessons/06-queue-storage/README.md#the-visibility-timeout-is-a-bet.");

    /// <summary>Reports whether a handler taking <paramref name="actual"/> will be delivered twice.</summary>
    /// <param name="visibility">The configured visibility timeout.</param>
    /// <param name="actual">How long the handler actually takes.</param>
    /// <returns><c>true</c> when the message becomes visible again before the handler deletes it.</returns>
    public static bool WillBeRedelivered(TimeSpan visibility, TimeSpan actual) =>
        // GAP 4 — The message reappears the instant the timeout expires, whether
        // or not the first handler is still running. A handler that outlives its
        // visibility window is processing work a second consumer already has.
        throw new NotImplementedException(
            "GAP 4: implement VisibilityPlanner.WillBeRedelivered. See "
            + "lessons/06-queue-storage/README.md#the-visibility-timeout-is-a-bet.");
}
