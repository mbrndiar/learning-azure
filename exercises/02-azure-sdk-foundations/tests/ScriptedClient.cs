using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Exercises.SdkFoundations.Tests;

/// <summary>
/// Builds a real <see cref="BlobContainerClient"/> whose transport is scripted,
/// so evaluator runs exercise the SDK pipeline without a network.
/// </summary>
internal static class ScriptedClient
{
    /// <summary>Container URI every scripted client is pointed at.</summary>
    public static Uri ContainerUri { get; } = new("https://stexpedition.blob.core.windows.net/stations");

    /// <summary>Creates a client over <paramref name="handler"/> with the exercise's own options.</summary>
    /// <param name="handler">The scripted transport.</param>
    /// <param name="maxRetries">Retry budget to configure through the exercise code.</param>
    public static BlobContainerClient Create(ScriptedHandler handler, int maxRetries)
    {
        var options = StorageConnectionResolver.CreateClientOptions(maxRetries, TimeSpan.Zero);
        options.Transport = new HttpClientTransport(new HttpClient(handler));
        return new BlobContainerClient(ContainerUri, options);
    }

    /// <summary>Creates a directory adapter over <paramref name="handler"/>.</summary>
    /// <param name="handler">The scripted transport.</param>
    /// <param name="maxRetries">Retry budget to configure through the exercise code.</param>
    public static BlobStationDirectory Directory(ScriptedHandler handler, int maxRetries = 0) =>
        new(Create(handler, maxRetries));

    /// <summary>A canned station record body, as the service would return it.</summary>
    public static HttpResponseMessage StationBody(StationRecord record) =>
        StorageResponses.OkWithBody(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                record,
                BlobStationDirectory.SerializerOptions),
            "application/json");

    /// <summary>A response with an arbitrary status and storage error code.</summary>
    public static HttpResponseMessage Failure(HttpStatusCode status, string errorCode, string message) =>
        StorageResponses.Error(status, errorCode, message);
}
