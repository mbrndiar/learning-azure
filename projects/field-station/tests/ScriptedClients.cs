using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>Builds real Azure SDK clients over a scripted transport.</summary>
/// <remarks>
/// The adapters are the only place the project touches Azure, so they are graded
/// against the real client types — real pipeline, real retry policy, real error
/// classification — with a script where the network would be. No socket is
/// opened, no emulator is required, and the wall clock is never consulted.
/// </remarks>
internal static class ScriptedClients
{
    /// <summary>The container URI every scripted blob client addresses.</summary>
    public static Uri ContainerUri { get; } = new("https://stexpedition.blob.core.windows.net/expedition-artifacts");

    /// <summary>The queue URI every scripted queue client addresses.</summary>
    public static Uri QueueUri { get; } = new("https://stexpedition.queue.core.windows.net/artifact-work");

    /// <summary>The poison queue URI.</summary>
    public static Uri PoisonQueueUri { get; } = new("https://stexpedition.queue.core.windows.net/artifact-work-poison");

    /// <summary>The table URI every scripted table client addresses.</summary>
    public static Uri TableUri { get; } = new("https://stexpedition.table.core.windows.net");

    /// <summary>A syntactically valid, entirely fictional account key.</summary>
    /// <remarks>
    /// The Tables client requires a credential to construct. Nothing signs a real
    /// request here, and the value is not a secret: it is 64 zero bytes.
    /// </remarks>
    private static readonly string FakeKey = Convert.ToBase64String(new byte[64]);

    /// <summary>Wraps <paramref name="handler"/> in a real container client.</summary>
    public static BlobContainerClient Container(ScriptedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var options = FieldStationClients.BlobOptions(maxRetries: 2);
        options.Transport = new HttpClientTransport(new HttpClient(handler));

        // Retries are instant so the evaluator stays fast; the number of attempts
        // is what is under test, never the wall clock.
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;
        return new BlobContainerClient(ContainerUri, options);
    }

    /// <summary>Wraps <paramref name="handler"/> in a real queue client.</summary>
    public static QueueClient Queue(ScriptedHandler handler, Uri? uri = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var options = FieldStationClients.QueueOptions(maxRetries: 2);
        options.Transport = new HttpClientTransport(new HttpClient(handler));
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;
        return new QueueClient(uri ?? QueueUri, options);
    }

    /// <summary>Wraps <paramref name="handler"/> in a real table client.</summary>
    public static TableClient Table(ScriptedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var options = FieldStationClients.TableOptions(maxRetries: 2);
        options.Transport = new HttpClientTransport(new HttpClient(handler));
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;

        // A Tables client insists on a credential before it will build, even
        // though the scripted transport never validates one. The key is fake and
        // never leaves the process.
        var credential = new TableSharedKeyCredential("stexpedition", FakeKey);
        return new TableClient(TableUri, "stationstatus", credential, options);
    }

    /// <summary>A 200 download response carrying <paramref name="content"/> and an ETag.</summary>
    public static HttpResponseMessage Download(string content, string etag, string contentType = "application/json")
    {
        var response = StorageResponses.OkWithBody(System.Text.Encoding.UTF8.GetBytes(content), contentType);
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    /// <summary>A 200 JSON response, as the Tables service answers a point read with.</summary>
    public static HttpResponseMessage TableEntity(
        string partitionKey,
        string rowKey,
        string state,
        int processedCount,
        string etag)
    {
        var payload = $$"""
        {"PartitionKey":"{{partitionKey}}","RowKey":"{{rowKey}}","State":"{{state}}",
        "ProcessedCount":{{processedCount}},"ArtifactName":"stations/{{partitionKey}}/{{rowKey}}.json",
        "UpdatedUtc":"2026-07-06T12:00:00.0000000+00:00","odata.etag":{{System.Text.Json.JsonSerializer.Serialize(etag)}}}
        """;

        var response = StorageResponses.OkWithBody(System.Text.Encoding.UTF8.GetBytes(payload), "application/json");
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    /// <summary>A 204 response carrying an ETag, as a Tables write answers with.</summary>
    public static HttpResponseMessage TableWritten(string etag)
    {
        var response = NoContent();
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    /// <summary>204 No Content, which is what a successful queue delete returns.</summary>
    public static HttpResponseMessage NoContent()
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", StorageResponses.RequestId);
        response.Headers.TryAddWithoutValidation("x-ms-version", "2025-11-05");
        return response;
    }

    /// <summary>201 Created carrying the send receipt a queue send returns.</summary>
    public static HttpResponseMessage MessageSent(string messageId = "mid-poison-1") =>
        StorageResponses.WithXml(System.Net.HttpStatusCode.Created, $"""
        <?xml version="1.0" encoding="utf-8"?>
        <QueueMessagesList><QueueMessage>
          <MessageId>{messageId}</MessageId>
          <InsertionTime>Mon, 06 Jul 2026 12:00:00 GMT</InsertionTime>
          <ExpirationTime>Mon, 13 Jul 2026 12:00:00 GMT</ExpirationTime>
          <PopReceipt>receipt-poison</PopReceipt>
          <TimeNextVisible>Mon, 06 Jul 2026 12:00:00 GMT</TimeNextVisible>
        </QueueMessage></QueueMessagesList>
        """);
}
