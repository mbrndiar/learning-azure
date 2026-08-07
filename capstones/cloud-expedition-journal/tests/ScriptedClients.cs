using System.Net.Http.Headers;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>Builds real Azure SDK clients over a scripted transport.</summary>
/// <remarks>
/// The adapters are the only place the capstone touches Azure, so they are graded
/// against the real client types — real pipeline, real retry policy, real error
/// classification — with a script where the network would be. No socket is
/// opened, no emulator is required, and the wall clock is never consulted.
/// </remarks>
internal static class ScriptedClients
{
    /// <summary>The container URI every scripted blob client addresses.</summary>
    public static Uri ContainerUri { get; } = new("https://stexpedition.blob.core.windows.net/expedition-journal");

    /// <summary>Wraps <paramref name="handler"/> in a real container client.</summary>
    /// <param name="handler">The scripted transport.</param>
    /// <returns>A real client that talks to the script.</returns>
    public static BlobContainerClient Container(ScriptedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var options = ExpeditionEnvironmentFactory.BlobOptions(maxRetries: 2);
        options.Transport = new HttpClientTransport(new HttpClient(handler));

        // Retries are instant so the evaluator stays fast; the number of attempts
        // is what is under test, never the wall clock.
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;
        return new BlobContainerClient(ContainerUri, options);
    }

    /// <summary>A 200 response carrying blob properties, metadata, and an ETag.</summary>
    /// <param name="owner">The lease owner recorded in metadata.</param>
    /// <param name="claimedAt">When the claim was last renewed.</param>
    /// <param name="etag">The blob's current version.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Properties(string owner, DateTimeOffset claimedAt, string etag)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        };

        response.Headers.TryAddWithoutValidation("x-ms-request-id", StorageResponses.RequestId);
        response.Headers.TryAddWithoutValidation("x-ms-version", "2025-11-05");
        response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        response.Headers.TryAddWithoutValidation($"x-ms-meta-{BlobCheckpointVault.OwnerMetadataKey}", owner);
        response.Headers.TryAddWithoutValidation(
            $"x-ms-meta-{BlobCheckpointVault.ClaimedAtMetadataKey}",
            ExpeditionNaming.FormatInstant(claimedAt));
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        response.Content.Headers.TryAddWithoutValidation("Last-Modified", "Mon, 06 Jul 2026 12:00:00 GMT");
        return response;
    }

    /// <summary>A 200 download response carrying <paramref name="content"/> and an ETag.</summary>
    /// <param name="content">The stored body.</param>
    /// <param name="etag">The blob's current version.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Download(string content, string etag)
    {
        var response = StorageResponses.OkWithBody(
            System.Text.Encoding.UTF8.GetBytes(content),
            "application/json");

        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }
}
