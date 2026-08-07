namespace LearningAzure.Exercises.SecureOperableCloud;

/// <summary>
/// Prices a live run before it happens, and prices the same resources sitting
/// idle afterwards.
/// </summary>
/// <remarks>
/// The second number is the one that matters. A checkpoint that costs a cent to
/// run costs several pounds a month to forget, and the difference between those
/// two sentences is a single <c>az group delete</c> nobody typed.
/// </remarks>
public static class CostEnvelope
{
    /// <summary>Hours in a day, named so the idle arithmetic reads as itself.</summary>
    public const int HoursPerDay = 24;

    /// <summary>Prices a set of resources over a run, and while nobody uses them.</summary>
    /// <param name="resources">The resources the live checkpoint creates.</param>
    /// <param name="runLength">How long the checkpoint is expected to take.</param>
    /// <param name="budgetUsd">The ceiling the course promises to stay under.</param>
    /// <returns>The run cost, the idle cost per day, and what dominates it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resources"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="resources"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="runLength"/> is not positive, or <paramref name="budgetUsd"/> is negative.
    /// </exception>
    public static CostEstimate Estimate(
        IReadOnlyList<BilledResource> resources,
        TimeSpan runLength,
        decimal budgetUsd)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0)
        {
            throw new ArgumentException("An empty architecture has no cost shape worth printing.", nameof(resources));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(runLength, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetUsd);

        // GAP 12: two totals, because the billing shape decides which applies.
        //
        // During the run everything is charged: provisioned resources for
        // existing, consumption resources for the work the run does, storage
        // for the bytes it writes. Afterwards the difference appears.
        // Provisioned throughput and storage keep billing at exactly the same
        // rate with nobody connected -- that is what "provisioned" means -- and
        // consumption resources drop to nothing, because there is no work. So
        // the idle figure sums only the shapes that bill for existing, and the
        // dominant resource is the one contributing most to that figure, not
        // the most expensive resource overall. A run that fits the budget is
        // WithinBudget; equal to the budget still fits.
        // See lessons/12-secure-operable-cloud/README.md#the-bill-has-two-numbers
        throw new NotImplementedException(
            "GAP 12: implement CostEnvelope.Estimate. "
            + "See lessons/12-secure-operable-cloud/README.md#the-bill-has-two-numbers.");
    }
}
