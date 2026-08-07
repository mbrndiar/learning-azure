namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>
/// Decides how long to wait after a throttled request, and when to stop
/// waiting altogether.
/// </summary>
/// <remarks>
/// The schedule is computed rather than executed, which is what makes it
/// testable: a policy that only exists as sleeps inside a loop can be reasoned
/// about but never checked. The emulator never returns 429, so this is the only
/// place in the module where throttling can be exercised at all.
/// </remarks>
public sealed class ThrottlePolicy
{
    /// <summary>The wait before the first retry.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest the client will wait on its own initiative.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(5);

    /// <summary>The client's own backoff curve, used when the service says nothing.</summary>
    /// <param name="attempt">The one-based attempt that just failed.</param>
    /// <returns>How long to wait before the next attempt.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is not positive.</exception>
    public static TimeSpan Backoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        // GAP 6: exponential, and capped.
        //
        // Linear backoff does not reduce load fast enough to let a throttled
        // partition recover: with a hundred clients retrying every 100 ms, the
        // service is still receiving the same storm that caused the 429.
        // Uncapped doubling has the opposite failure — attempt 20 waits a day,
        // and attempt 60 overflows TimeSpan outright.
        // See lessons/11-cosmos-development/README.md#retry-is-a-budget-not-a-loop
        var milliseconds = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);

        return milliseconds >= MaximumDelay.TotalMilliseconds
            ? MaximumDelay
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>Decides how long to wait after one specific response.</summary>
    /// <param name="response">The response that failed.</param>
    /// <param name="attempt">The one-based attempt that produced it.</param>
    /// <returns>The wait, and where the number came from.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is not positive.</exception>
    public static RetryStep WaitFor(ServiceResponse response, int attempt)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        // GAP 7: when the service tells you how long to wait, that number wins.
        //
        // x-ms-retry-after-ms is not advice. The service knows when the
        // partition's replenishment will have caught up; the client's curve is
        // a guess made with no information at all. Capping the server's value
        // at MaximumDelay looks prudent and is not: it produces a retry that
        // arrives before the budget is restored and is throttled again.
        // See lessons/11-cosmos-development/README.md#the-server-knows-how-long-to-wait
        return response.RetryAfter is { } told
            ? new RetryStep(attempt, told, FromServer: true)
            : new RetryStep(attempt, Backoff(attempt), FromServer: false);
    }

    /// <summary>Works out the whole retry schedule for a sequence of responses.</summary>
    /// <param name="responses">
    /// What the service returns on attempt 1, attempt 2, and so on.
    /// </param>
    /// <param name="maximumAttempts">The most attempts the caller will make.</param>
    /// <param name="budget">
    /// The caller's deadline: the total time it is willing to spend waiting.
    /// </param>
    /// <returns>The waits that will actually happen, and whether the caller gave up.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="responses"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    public static RetryPlan Plan(
        IReadOnlyList<ServiceResponse> responses,
        int maximumAttempts,
        TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(responses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks);

        var steps = new List<RetryStep>();
        var total = TimeSpan.Zero;

        for (var index = 0; index < responses.Count; index++)
        {
            var response = responses[index];

            if (response.StatusCode == ConcurrencyGuard.Ok)
            {
                return new RetryPlan(steps, Exhausted: false, total);
            }

            if (!ConcurrencyGuard.ShouldRetry(response.StatusCode))
            {
                return new RetryPlan(steps, Exhausted: false, total);
            }

            if (index + 1 >= maximumAttempts)
            {
                return new RetryPlan(steps, Exhausted: true, total);
            }

            var step = WaitFor(response, index + 1);

            // GAP 8: a wait that outlives the caller's deadline is not a wait,
            // it is a timeout with extra steps.
            //
            // The caller has a deadline — an HTTP request to answer, a queue
            // lock to renew. Spending it inside a retry the caller will never
            // see the result of turns a fast failure it could have handled into
            // a slow one it cannot. Stop BEFORE the wait that would breach it.
            // See lessons/11-cosmos-development/README.md#retry-is-a-budget-not-a-loop
            if (total + step.Delay > budget)
            {
                return new RetryPlan(steps, Exhausted: true, total);
            }

            steps.Add(step);
            total += step.Delay;
        }

        return new RetryPlan(steps, Exhausted: true, total);
    }
}
