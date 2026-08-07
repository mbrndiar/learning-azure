using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LearningAzure.Exercises.BlobLifecycle;

/// <summary>Implements <see cref="IArtifactStore"/> with a real <see cref="BlobClient"/>.</summary>
/// <remarks>
/// This is the only file in the exercise that knows Azure exists. Everything the
/// evaluator asserts here — the conditional header on the wire, the status code
/// the service answers with, and the classification the SDK produces — is real
/// SDK behavior driven over a scripted transport.
/// </remarks>
/// <param name="container">The container holding the artifacts.</param>
public sealed class ConditionalArtifactStore(BlobContainerClient container) : IArtifactStore
{
    /// <summary>The container holding the artifacts.</summary>
    public BlobContainerClient Container { get; } =
        container ?? throw new ArgumentNullException(nameof(container));

    /// <inheritdoc />
    public async Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var response = await Container.GetBlobClient(name)
                .DownloadContentAsync(cancellationToken)
                .ConfigureAwait(false);

            // GAP 1 — Return the bytes AND the ETag together.
            //
            // The ETag is not decoration: it is the only thing that makes the
            // next write safe. Reading content without capturing the version it
            // came from is how a lost update starts.
            return new ArtifactRevision(
                response.Value.Content.ToArray(),
                // "H" is the HTTP form: quoted, exactly as it must go back out
                // in an If-Match header. ToString() drops the quotes, and an
                // unquoted If-Match is rejected by the service.
                response.GetRawResponse().Headers.ETag?.ToString("H")
                    ?? throw new InvalidOperationException("A 200 response with no ETag is a service defect."));
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            // 404 answers "does this artifact exist?". It is caught by STATUS so
            // a 403 from a missing role assignment still propagates.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PreconditionOutcome> WriteIfUnchangedAsync(
        string name,
        byte[] content,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);

        try
        {
            // GAP 2 — Send the precondition.
            //
            // Without Conditions the service happily overwrites whatever is
            // there, and the competing writer's work vanishes with no error,
            // no log line, and no way to notice.
            await Container.GetBlobClient(name).UploadAsync(
                BinaryData.FromBytes(content),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) },
                },
                cancellationToken).ConfigureAwait(false);

            return PreconditionOutcome.Written;
        }
        catch (RequestFailedException error) when (error.Status == 412)
        {
            // 412 is not a failure. It is the service reporting, correctly, that
            // the caller's copy is stale — which is exactly what was asked.
            return PreconditionOutcome.Stale;
        }
    }

    /// <inheritdoc />
    public async Task<PreconditionOutcome> CreateIfAbsentAsync(
        string name,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            // GAP 3 — "Only if it does not exist yet" is If-None-Match: *.
            //
            // Checking existence first and then writing is a race: two callers
            // can both see "absent" and both write. One header removes it.
            await Container.GetBlobClient(name).UploadAsync(
                BinaryData.FromBytes(content),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                },
                cancellationToken).ConfigureAwait(false);

            return PreconditionOutcome.Written;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            // The service reports a lost create as 409 BlobAlreadyExists, and as
            // 412 when the If-None-Match is what failed. Both mean the same
            // thing to the caller, so both map to one outcome.
            return PreconditionOutcome.AlreadyExists;
        }
    }
}
