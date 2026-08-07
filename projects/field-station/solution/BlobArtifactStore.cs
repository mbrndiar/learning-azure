using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage;
using Azure.Storage.Blobs.Models;

namespace LearningAzure.Projects.FieldStation;

/// <summary>Implements <see cref="IArtifactStore"/> over a real Blob container.</summary>
/// <remarks>
/// <para>
/// One of the three files in this project that knows Azure exists. Everything
/// above it is expressed in the domain's own vocabulary, which is why the whole
/// pipeline can be graded in memory and still run unchanged against Azurite.
/// </para>
/// <para>
/// The adapter's job is translation, and translation includes error
/// classification: a 404 answers "does this exist?" and a 412 answers "was my
/// version still current?", while a 403 answers neither and must keep travelling.
/// </para>
/// </remarks>
/// <param name="container">The container holding this expedition's artifacts.</param>
public sealed class BlobArtifactStore(BlobContainerClient container) : IArtifactStore
{
    /// <summary>The container holding this expedition's artifacts.</summary>
    public BlobContainerClient Container { get; } =
        container ?? throw new ArgumentNullException(nameof(container));

    /// <inheritdoc />
    public async Task<ArtifactWriteResult> CreateIfAbsentAsync(
        string name,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            // GAP 14 — Upload the STREAM, and let the SDK chunk it.
            //
            // Reading the stream into a byte[] first is a memory cost
            // proportional to the artifact, paid on a machine sized for the
            // metadata. UploadAsync(Stream, ...) streams in blocks; the transfer
            // options are how the block size and parallelism are chosen.
            var response = await Container.GetBlobClient(name).UploadAsync(
                content,
                new BlobUploadOptions
                {
                    // If-None-Match: * is the whole idempotency guarantee. It
                    // makes "create only if absent" one atomic service decision
                    // instead of a check and a hope.
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                    TransferOptions = TransferOptions,
                },
                cancellationToken).ConfigureAwait(false);

            return new ArtifactWriteResult(WriteOutcome.Written, Quoted(response.Value.ETag));
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            // The service reports a lost create as 409 BlobAlreadyExists, and as
            // 412 when the If-None-Match is what failed. Both mean one thing to
            // the caller: somebody else got there first.
            return new ArtifactWriteResult(WriteOutcome.AlreadyExists, null);
        }
    }

    /// <inheritdoc />
    public async Task<ArtifactWriteResult> ReplaceIfUnchangedAsync(
        string name,
        Stream content,
        string contentType,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);

        try
        {
            var response = await Container.GetBlobClient(name).UploadAsync(
                content,
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) },
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                    TransferOptions = TransferOptions,
                },
                cancellationToken).ConfigureAwait(false);

            return new ArtifactWriteResult(WriteOutcome.Written, Quoted(response.Value.ETag));
        }
        catch (RequestFailedException error) when (error.Status == 412)
        {
            // 412 is not a failure. It is the service correctly reporting that
            // this caller's copy is stale, which is exactly what was asked.
            return new ArtifactWriteResult(WriteOutcome.Stale, null);
        }
    }

    /// <inheritdoc />
    public async Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var response = await Container.GetBlobClient(name)
                .DownloadContentAsync(cancellationToken)
                .ConfigureAwait(false);

            return new ArtifactRevision(
                response.Value.Content.ToArray(),
                // "H" is the HTTP form: quoted, exactly as it must go back out in
                // an If-Match header. ToString() drops the quotes, and an
                // unquoted If-Match is rejected by the service.
                response.GetRawResponse().Headers.ETag?.ToString("H")
                    ?? throw new InvalidOperationException("A 200 response with no ETag is a service defect."),
                response.Value.Details.ContentType ?? "application/octet-stream");
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            // Caught by STATUS, not by exception type, so a 403 from a missing
            // role assignment still propagates instead of reading as "no data".
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListNamesAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // AsPages makes the paging visible, which is the point: a listing is a
        // sequence of service calls, and the token is honoured between them.
        await foreach (var page in Container
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken)
            .AsPages()
            .ConfigureAwait(false))
        {
            foreach (var item in page.Values)
            {
                yield return item.Name;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteIfExistsAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var response = await Container.GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    private static StorageTransferOptions TransferOptions => new()
    {
        InitialTransferSize = 4 * 1024 * 1024,
        MaximumTransferSize = 4 * 1024 * 1024,
        MaximumConcurrency = 2,
    };

    private static string Quoted(ETag etag) => etag.ToString("H");
}
