namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>
/// Drains a paged query correctly: every document, no matter how the service
/// chooses to cut the pages.
/// </summary>
/// <remarks>
/// The source is a delegate rather than a Cosmos container, which is the whole
/// point: the rule this class enforces — a short page is not the end — is a
/// rule about tokens, not about Cosmos, and the emulator cannot demonstrate it
/// because it never paginates at all.
/// </remarks>
public sealed class PageReader
{
    /// <summary>The largest page size the service will honour.</summary>
    public const int MaximumPageSize = 1000;

    /// <summary>Builds the request for the next page.</summary>
    /// <param name="pageSize">The page size the caller wants.</param>
    /// <param name="continuation">The previous page's token, if any.</param>
    /// <returns>A request that resumes where the last page stopped.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is not positive.</exception>
    public static PageRequest NextRequest(int pageSize, string? continuation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        // GAP 1: the token has to survive into the next request, and the page
        // size has to stay inside what the service accepts.
        //
        // Clamping rather than throwing is deliberate: an oversized page size
        // is a caller optimising, not a caller erring, and the service would
        // silently clamp it anyway. Dropping the token, on the other hand,
        // restarts the query from the beginning — which reads as an infinite
        // loop that returns the first page forever.
        // See lessons/11-cosmos-development/README.md#the-token-is-the-only-end-signal
        return new PageRequest(Math.Min(pageSize, MaximumPageSize), continuation);
    }

    /// <summary>Decides whether a page was the last one.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="page">The page just returned.</param>
    /// <param name="requestedSize">How many documents were asked for.</param>
    /// <returns><see langword="true"/> when there is nothing left to read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    public static bool IsExhausted<T>(Page<T> page, int requestedSize)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedSize);

        // GAP 2: the requested size is not part of this decision.
        //
        // `page.Items.Count < requestedSize` is the tempting version and it is
        // wrong. Cosmos cuts a page short whenever it hits a 4 MB response, a
        // five-second execution budget, or a partition boundary, and it hands
        // back a token that says so. An empty page with a token is also legal.
        // The token is the only signal.
        // See lessons/11-cosmos-development/README.md#the-token-is-the-only-end-signal
        return string.IsNullOrEmpty(page.ContinuationToken);
    }

    /// <summary>Reads a query to the end, one page at a time.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="fetch">Returns the page for a request.</param>
    /// <param name="pageSize">How many documents to ask for per page.</param>
    /// <param name="maximumPages">
    /// A hard stop, so a service that keeps handing back tokens cannot spin the
    /// caller forever.
    /// </param>
    /// <returns>Every document the query matched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fetch"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    /// <exception cref="InvalidOperationException">The page limit was reached with a token still outstanding.</exception>
    public static DrainResult<T> Drain<T>(
        Func<PageRequest, Page<T>> fetch,
        int pageSize,
        int maximumPages)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPages);

        var items = new List<T>();
        var pages = 0;
        var charge = 0.0;
        string? continuation = null;

        // GAP 3: a do/while, not a while.
        //
        // The first request has no token, so a `while (continuation != null)`
        // loop never executes and the query returns nothing. Every page is
        // charged, including the last one that returns no documents, so the
        // charge accumulates before the exhaustion check rather than after it.
        // See lessons/11-cosmos-development/README.md#a-page-is-not-the-whole-answer
        do
        {
            if (pages == maximumPages)
            {
                throw new InvalidOperationException(
                    $"Stopped after {maximumPages} pages with a continuation token still outstanding.");
            }

            var page = fetch(NextRequest(pageSize, continuation));

            items.AddRange(page.Items);
            charge += page.RequestCharge;
            pages++;

            continuation = IsExhausted(page, pageSize) ? null : page.ContinuationToken;
        }
        while (continuation is not null);

        return new DrainResult<T>(items, pages, charge);
    }
}
