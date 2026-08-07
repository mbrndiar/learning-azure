namespace LearningAzure.Exercises.BlobStorage.Tests;

/// <summary>
/// A read-only, non-seekable stream of unknown length that records how much of
/// itself had been consumed at any moment.
/// </summary>
/// <remarks>
/// This is what makes "did you stream or did you buffer?" an observable fact
/// rather than a code review opinion: an implementation that reads the whole
/// source before its first upload call is visible in <see cref="Consumed"/>.
/// </remarks>
internal sealed class ProbeStream(byte[] content) : Stream
{
    private readonly byte[] _content = content;
    private int _position;

    /// <summary>Bytes read from this stream so far.</summary>
    public int Consumed => _position;

    /// <summary>Largest single read request the caller made.</summary>
    public int LargestRead { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length =>
        throw new NotSupportedException("A network stream has no length; neither does this one.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("A non-seekable stream has no position.");
        set => throw new NotSupportedException("A non-seekable stream has no position.");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        LargestRead = Math.Max(LargestRead, count);
        var available = Math.Min(count, _content.Length - _position);
        if (available <= 0)
        {
            return 0;
        }

        Array.Copy(_content, _position, buffer, offset, available);
        _position += available;
        return available;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LargestRead = Math.Max(LargestRead, buffer.Length);
        var available = Math.Min(buffer.Length, _content.Length - _position);
        if (available <= 0)
        {
            return ValueTask.FromResult(0);
        }

        _content.AsMemory(_position, available).CopyTo(buffer);
        _position += available;
        return ValueTask.FromResult(available);
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("A non-seekable stream cannot seek.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Records what an upload actually did to the service.</summary>
internal sealed class RecordingTransport(ProbeStream? source = null) : IArtifactTransport
{
    private readonly List<(string BlockId, int Length, int ConsumedAtCall)> _staged = [];

    /// <summary>Every staged block, in call order, with the source position at that moment.</summary>
    public IReadOnlyList<(string BlockId, int Length, int ConsumedAtCall)> Staged => _staged;

    /// <summary>Block ids passed to the single commit call, or <c>null</c> when never committed.</summary>
    public IReadOnlyList<string>? CommittedBlockIds { get; private set; }

    /// <summary>Metadata passed to the commit call.</summary>
    public IReadOnlyDictionary<string, string>? CommittedMetadata { get; private set; }

    /// <summary>Number of commit calls; more than one is a defect.</summary>
    public int CommitCount { get; private set; }

    /// <summary>Stage index after which staging throws, or <c>null</c> to never throw.</summary>
    public int? FailAfterBlock { get; init; }

    /// <inheritdoc />
    public Task StageBlockAsync(string blockId, ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _staged.Add((blockId, block.Length, source?.Consumed ?? -1));

        if (FailAfterBlock is { } limit && _staged.Count > limit)
        {
            throw new InvalidOperationException("scripted staging failure");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CommitAsync(
        IReadOnlyList<string> blockIds,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCount++;
        CommittedBlockIds = blockIds;
        CommittedMetadata = metadata;
        return Task.CompletedTask;
    }
}

/// <summary>A paged listing source that counts how many pages were actually fetched.</summary>
internal sealed class CountingPageSource(IReadOnlyList<ArtifactListing> items, int declaredPageSize)
    : IArtifactPageSource
{
    /// <summary>Number of <see cref="GetPageAsync"/> calls, which is what Azure bills.</summary>
    public int Calls { get; private set; }

    /// <summary>Page size the caller asked for on the most recent call.</summary>
    public int LastRequestedPageSize { get; private set; }

    /// <inheritdoc />
    public Task<ArtifactPage> GetPageAsync(
        string prefix,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastRequestedPageSize = pageSize;

        var offset = continuationToken is null ? 0 : int.Parse(continuationToken, provider: null);
        var matching = items.Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var page = matching.Skip(offset).Take(declaredPageSize).ToArray();
        var next = offset + page.Length < matching.Length
            ? (offset + page.Length).ToString(provider: null)
            : null;

        return Task.FromResult(new ArtifactPage(page, next));
    }
}
