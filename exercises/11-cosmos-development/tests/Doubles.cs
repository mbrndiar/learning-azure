using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// A query result that pages exactly the way Cosmos does and the emulator does
/// not: it honours the requested page size, it is allowed to cut a page short
/// on its own, and it signals the end with a null token rather than with a
/// small page.
/// </summary>
internal sealed class PagedSource
{
    private readonly IReadOnlyList<int> _documents;
    private readonly IReadOnlyList<int>? _forcedPageSizes;

    /// <summary>Initialises a new instance of the <see cref="PagedSource"/> class.</summary>
    /// <param name="documents">Everything the query matches.</param>
    /// <param name="forcedPageSizes">
    /// Page sizes the service imposes regardless of what was asked for, used to
    /// model a 4 MB cut or a partition boundary. <see langword="null"/> means
    /// the requested size is honoured.
    /// </param>
    public PagedSource(IReadOnlyList<int> documents, IReadOnlyList<int>? forcedPageSizes = null)
    {
        _documents = documents;
        _forcedPageSizes = forcedPageSizes;
    }

    /// <summary>Gets how many pages have been requested.</summary>
    public int Calls { get; private set; }

    /// <summary>Gets the page sizes actually asked for, in order.</summary>
    public List<int> RequestedSizes { get; } = [];

    /// <summary>Returns one page.</summary>
    /// <param name="request">The page request.</param>
    /// <returns>The page.</returns>
    public Page<int> Fetch(PageRequest request)
    {
        RequestedSizes.Add(request.MaxItemCount);

        var offset = request.ContinuationToken is null
            ? 0
            : int.Parse(request.ContinuationToken, System.Globalization.CultureInfo.InvariantCulture);

        var size = _forcedPageSizes is null
            ? request.MaxItemCount
            : Math.Min(request.MaxItemCount, _forcedPageSizes[Math.Min(Calls, _forcedPageSizes.Count - 1)]);

        Calls++;

        var take = Math.Min(size, _documents.Count - offset);
        var items = _documents.Skip(offset).Take(take).ToList();
        var next = offset + take;

        return new Page<int>(
            items,
            next >= _documents.Count
                ? null
                : next.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RequestCharge: 2.5 + (take * 0.1));
    }
}

/// <summary>
/// A store that enforces ETags, and that can be told to have someone else
/// change the document underneath the caller a fixed number of times.
/// </summary>
internal sealed class RacingStore : IConditionalStore
{
    private readonly int _interferences;
    private readonly int _refuseWith;

    private StoredDocument _document;
    private int _version;

    /// <summary>Initialises a new instance of the <see cref="RacingStore"/> class.</summary>
    /// <param name="interferences">
    /// How many times a competing writer commits between the caller's read and
    /// the caller's write.
    /// </param>
    /// <param name="refuseWith">
    /// A status the store returns instead of doing anything, or <c>0</c> for a
    /// store that behaves.
    /// </param>
    public RacingStore(int interferences, int refuseWith = 0)
    {
        _interferences = interferences;
        _refuseWith = refuseWith;
        _document = new StoredDocument("reading-1", "station-05", "etag-0", Corrections: 0);
    }

    /// <summary>Gets how many reads the caller performed.</summary>
    public int Reads { get; private set; }

    /// <summary>Gets how many conditional writes the caller attempted.</summary>
    public int Writes { get; private set; }

    /// <summary>Gets the document as it now stands.</summary>
    public StoredDocument Current => _document;

    /// <inheritdoc />
    public StoredDocument Read(string id)
    {
        Reads++;

        return _document;
    }

    /// <inheritdoc />
    public int TryReplace(StoredDocument document, string ifMatchEtag)
    {
        Writes++;

        if (_refuseWith != 0)
        {
            return _refuseWith;
        }

        // A competing writer commits first, on the first N attempts.
        if (Writes <= _interferences)
        {
            _version++;
            _document = _document with
            {
                ETag = $"etag-{_version}",
                Corrections = _document.Corrections + 1,
            };
        }

        if (!string.Equals(ifMatchEtag, _document.ETag, StringComparison.Ordinal))
        {
            return ConcurrencyGuard.PreconditionFailed;
        }

        _version++;
        _document = document with { ETag = $"etag-{_version}" };

        return ConcurrencyGuard.Ok;
    }
}

/// <summary>Fixed data for the evaluator.</summary>
internal static class Fixtures
{
    /// <summary>The mutation the concurrency tests apply: add one correction.</summary>
    public static StoredDocument AddCorrection(StoredDocument current) =>
        current with { Corrections = current.Corrections + 1 };

    /// <summary>A run of documents.</summary>
    public static IReadOnlyList<int> Documents(int count) => [.. Enumerable.Range(0, count)];

    /// <summary>A throttled response that carries no advice.</summary>
    public static ServiceResponse Throttled() => new(ConcurrencyGuard.TooManyRequests, null);

    /// <summary>A throttled response that says exactly how long to wait.</summary>
    public static ServiceResponse ThrottledFor(int milliseconds) =>
        new(ConcurrencyGuard.TooManyRequests, TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>A response the caller should stop on.</summary>
    public static ServiceResponse Ok() => new(ConcurrencyGuard.Ok, null);

    /// <summary>An operation of a given size in a given partition.</summary>
    public static BatchOperation Operation(string partitionKey, int index, int sizeBytes = 1024) =>
        new($"{partitionKey}-{index:0000}", partitionKey, sizeBytes);

    /// <summary>A run of operations in one partition.</summary>
    public static List<BatchOperation> Operations(string partitionKey, int count, int sizeBytes = 1024) =>
        [.. Enumerable.Range(0, count).Select(index => Operation(partitionKey, index, sizeBytes))];
}
