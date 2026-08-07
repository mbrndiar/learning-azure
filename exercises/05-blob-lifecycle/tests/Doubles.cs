using System.Net;
using Azure.Storage.Blobs;
using Azure.Core.Pipeline;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Exercises.BlobLifecycle.Tests;

/// <summary>Builds real SDK clients over a scripted transport.</summary>
internal static class ScriptedClient
{
    /// <summary>The container URI every scripted client addresses.</summary>
    public static Uri ContainerUri { get; } = new("https://stexpedition.blob.core.windows.net/artifacts");

    /// <summary>Wraps <paramref name="handler"/> in a real <see cref="BlobContainerClient"/>.</summary>
    public static BlobContainerClient Container(ScriptedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler)),
        };

        // Retries are instant so a deterministic evaluator stays fast; the
        // number of attempts is what is under test, never the wall clock.
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;

        return new BlobContainerClient(ContainerUri, options);
    }

    /// <summary>A 200 download response carrying <paramref name="content"/> and an ETag.</summary>
    public static HttpResponseMessage Download(string content, string etag)
    {
        var response = StorageResponses.OkWithBody(System.Text.Encoding.UTF8.GetBytes(content));
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }
}

/// <summary>
/// An in-memory <see cref="IArtifactStore"/> whose ETag changes on every write,
/// with a scripted competing writer that can steal the race.
/// </summary>
internal sealed class RacingStore : IArtifactStore
{
    private readonly Queue<Action> _competingWrites;
    private byte[] _content;
    private int _version;

    public RacingStore(byte[] initial, int stealCount)
    {
        _content = initial;
        _version = 1;
        _competingWrites = new Queue<Action>(
            Enumerable.Repeat<Action>(() => Bump(), stealCount));
    }

    /// <summary>Every read the updater performed, as the ETag it saw.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Every conditional write the updater attempted, as the ETag it bet on.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>The bytes currently stored.</summary>
    public byte[] Content => _content;

    /// <summary>The current ETag, in the service's quoted form.</summary>
    public string ETag => $"\"v{_version}\"";

    /// <summary>Set to make the artifact appear absent.</summary>
    public bool Absent { get; set; }

    public Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Absent)
        {
            return Task.FromResult<ArtifactRevision?>(null);
        }

        Reads.Add(ETag);
        return Task.FromResult<ArtifactRevision?>(new ArtifactRevision([.. _content], ETag));
    }

    public Task<PreconditionOutcome> WriteIfUnchangedAsync(
        string name,
        byte[] content,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add(ifMatch);

        // The competing writer lands between the caller's read and its write,
        // which is exactly the window a conditional header exists to close.
        if (_competingWrites.Count > 0)
        {
            _competingWrites.Dequeue()();
        }

        if (ifMatch != ETag)
        {
            return Task.FromResult(PreconditionOutcome.Stale);
        }

        _content = content;
        Bump();
        return Task.FromResult(PreconditionOutcome.Written);
    }

    public Task<PreconditionOutcome> CreateIfAbsentAsync(
        string name,
        byte[] content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Absent)
        {
            return Task.FromResult(PreconditionOutcome.AlreadyExists);
        }

        _content = content;
        Absent = false;
        Bump();
        return Task.FromResult(PreconditionOutcome.Written);
    }

    private void Bump() => _version++;
}
