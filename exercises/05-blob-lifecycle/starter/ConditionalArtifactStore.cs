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
    public Task<ArtifactRevision?> TryReadAsync(string name, CancellationToken cancellationToken) =>
        // GAP 1 — Download the blob and return its bytes AND its ETag together.
        //
        // The ETag is not decoration: it is the only thing that makes the next
        // write safe. Reading content without capturing the version it came from
        // is how a lost update starts.
        //
        // A 404 means "no such artifact" and must return null. Catch it by
        // STATUS, so a 403 from a missing role assignment still propagates
        // instead of masquerading as "no data".
        //
        //   var response = await Container.GetBlobClient(name)
        //       .DownloadContentAsync(cancellationToken);
        //   response.Value.Content.ToArray()            // the bytes
        //   response.GetRawResponse().Headers.ETag?.ToString("H")   // the version
        //
        // The "H" format is the HTTP form: quoted, exactly as it must go back
        // out in an If-Match header. ToString() drops the quotes, and an
        // unquoted If-Match is rejected by the service.
        throw new NotImplementedException(
            "GAP 1: implement ConditionalArtifactStore.TryReadAsync. See "
            + "lessons/05-blob-lifecycle/README.md#an-etag-is-a-version-you-can-bet-on.");

    /// <inheritdoc />
    public Task<PreconditionOutcome> WriteIfUnchangedAsync(
        string name,
        byte[] content,
        string ifMatch,
        CancellationToken cancellationToken) =>
        // GAP 2 — Upload with an If-Match precondition.
        //
        // Without Conditions the service happily overwrites whatever is there,
        // and the competing writer's work vanishes with no error, no log line,
        // and no way to notice.
        //
        // A 412 is not a failure: it is the service reporting, correctly, that
        // the caller's copy is stale. Return PreconditionOutcome.Stale for it.
        //
        //   new BlobUploadOptions
        //   {
        //       Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) },
        //   }
        throw new NotImplementedException(
            "GAP 2: implement ConditionalArtifactStore.WriteIfUnchangedAsync. See "
            + "lessons/05-blob-lifecycle/README.md#an-etag-is-a-version-you-can-bet-on.");

    /// <inheritdoc />
    public Task<PreconditionOutcome> CreateIfAbsentAsync(
        string name,
        byte[] content,
        CancellationToken cancellationToken) =>
        // GAP 3 — "Only if it does not exist yet" is If-None-Match: *.
        //
        // Checking existence first and then writing is a race: two callers can
        // both see "absent" and both write. One header removes it.
        //
        // The service reports a lost create as 409, and as 412 when the
        // If-None-Match is what failed. Both mean AlreadyExists to the caller.
        //
        //   Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        throw new NotImplementedException(
            "GAP 3: implement ConditionalArtifactStore.CreateIfAbsentAsync. See "
            + "lessons/05-blob-lifecycle/README.md#an-etag-is-a-version-you-can-bet-on.");
}
