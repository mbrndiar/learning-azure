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

        // GAP 14 — Upload the STREAM under an If-None-Match: * precondition.
        //
        // Reading the stream into a byte[] first is a memory cost proportional to
        // the artifact, paid on a machine sized for the metadata.
        // UploadAsync(Stream, BlobUploadOptions, ...) streams in blocks;
        // `TransferOptions` is how the block size and parallelism are chosen.
        //
        // Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } is
        // the whole idempotency guarantee: it makes "create only if absent" one
        // atomic service decision instead of a check and a hope. Record
        // `contentType` through HttpHeaders so the artifact is readable later.
        //
        // The service reports a lost create as 409 BlobAlreadyExists, and as 412
        // when the If-None-Match is what failed. Both mean AlreadyExists to the
        // caller. Catch by STATUS, never by exception type, so a 403 from a
        // missing role assignment still propagates. Return the new version in
        // quoted HTTP form with Quoted(...).
        throw new NotImplementedException(
            "GAP 14: create the blob only when it is absent. See "
            + "projects/field-station/README.md#milestone-2-preserving-artifacts.");
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

        // Same upload, one different precondition: IfMatch = new ETag(ifMatch).
        // ETag.All here would mean "overwrite whatever is there", which is the
        // lost update this method exists to prevent.
        //
        // 412 is not a failure. It is the service correctly reporting that this
        // caller's copy is stale, which is exactly what was asked, so return
        // WriteOutcome.Stale rather than throwing.
        throw new NotImplementedException(
            "GAP 14: replace the blob only when it is unchanged. See "
            + "projects/field-station/README.md#milestone-2-preserving-artifacts.");
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
