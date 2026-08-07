using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LearningAzure.Support.AzureFakes;

/// <summary>One request an Azure SDK client sent through <see cref="ScriptedHandler"/>.</summary>
/// <param name="Method">HTTP method the client chose.</param>
/// <param name="Uri">Absolute request URI, including the query the client built.</param>
/// <param name="Headers">Every request header, including SDK-added conditional headers.</param>
/// <param name="Body">The request body the client sent, or an empty array when there was none.</param>
public sealed record RecordedRequest(
    string Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    /// <summary>Returns the single value of <paramref name="name"/>, or <c>null</c> when absent.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A deterministic <see cref="HttpMessageHandler"/> that answers Azure SDK requests
/// from a script instead of a network.
/// </summary>
/// <remarks>
/// <para>
/// Azure SDK clients accept any <see cref="HttpClient"/> through
/// <c>ClientOptions.Transport</c>, so scripting the handler drives the real
/// client — its pipeline, its retry policy, its response parsing, and its error
/// classification — with no service behind it. That keeps evaluator behavior
/// reproducible while still asserting against the code the course teaches.
/// </para>
/// <para>
/// The script is consumed in order. Running past its end is a scripting defect,
/// not a service condition, so it throws rather than inventing a response.
/// </para>
/// </remarks>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<RecordedRequest, HttpResponseMessage>> _script;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Creates a handler that answers requests with <paramref name="script"/>, in order.</summary>
    public ScriptedHandler(params IEnumerable<Func<RecordedRequest, HttpResponseMessage>> script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = new Queue<Func<RecordedRequest, HttpResponseMessage>>(script);
    }

    /// <summary>Creates a handler that answers every request with the same response factory.</summary>
    public static ScriptedHandler Always(Func<RecordedRequest, HttpResponseMessage> response, int times = 64)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ScriptedHandler(Enumerable.Repeat(response, times));
    }

    /// <summary>Every request the client sent, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<RecordedRequest>([.. _requests]);
            }
        }
    }

    /// <summary>Number of requests the client sent, which is attempts, not logical operations.</summary>
    public int AttemptCount => Requests.Count;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Observing cancellation here is what lets an evaluator prove that a
        // token reached the transport instead of being dropped on the way down.
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(",", values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(",", values);
            }
        }

        var recorded = new RecordedRequest(
            request.Method.Method,
            request.RequestUri ?? new Uri("http://unset.invalid/"),
            headers,
            body);

        Func<RecordedRequest, HttpResponseMessage> next;
        lock (_gate)
        {
            _requests.Add(recorded);
            if (_script.Count == 0)
            {
                throw new InvalidOperationException(
                    $"ScriptedHandler ran out of scripted responses at attempt {_requests.Count} "
                    + $"({recorded.Method} {recorded.Uri.AbsolutePath}). Extend the script.");
            }

            next = _script.Dequeue();
        }

        var response = next(recorded);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>Canned Azure Storage REST responses for scripted evaluator runs.</summary>
/// <remarks>
/// Status codes, headers, and payload shapes follow the Azure Storage REST
/// specification, because the SDK's own parsing and error classification is the
/// behavior under test. Values are fixed so a run is reproducible.
/// </remarks>
public static class StorageResponses
{
    /// <summary>The request identifier every canned response reports.</summary>
    public const string RequestId = "00000000-0000-0000-0000-00000000cafe";

    /// <summary>A representative fixed ETag, quoted exactly as the service returns it.</summary>
    public const string ETag = "\"0x8DEADBEEFCAFE01\"";

    /// <summary>201 Created with an ETag, as a successful blob or entity write returns.</summary>
    public static HttpResponseMessage Created(string etag = ETag) =>
        Build(HttpStatusCode.Created, etag: etag);

    /// <summary>200 OK with no body, as a successful metadata or property write returns.</summary>
    public static HttpResponseMessage Ok(string? etag = ETag) =>
        Build(HttpStatusCode.OK, etag: etag);

    /// <summary>200 OK with a body, as a successful download returns.</summary>
    public static HttpResponseMessage OkWithBody(byte[] content, string contentType = "application/octet-stream")
    {
        ArgumentNullException.ThrowIfNull(content);
        var response = Build(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(content);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        response.Content.Headers.ContentLength = content.Length;
        return response;
    }

    /// <summary>202 Accepted, as an asynchronous delete returns.</summary>
    public static HttpResponseMessage Accepted() => Build(HttpStatusCode.Accepted, etag: null);

    /// <summary>404 with <c>BlobNotFound</c>, the classification a missing artifact produces.</summary>
    public static HttpResponseMessage NotFound(string errorCode = "BlobNotFound") =>
        Error(HttpStatusCode.NotFound, errorCode, "The specified blob does not exist.");

    /// <summary>412 with <c>ConditionNotMet</c>, the classification a failed ETag precondition produces.</summary>
    public static HttpResponseMessage PreconditionFailed() =>
        Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet", "The condition specified using HTTP conditional header(s) is not met.");

    /// <summary>409 with <c>BlobAlreadyExists</c>, the classification an if-none-match conflict produces.</summary>
    public static HttpResponseMessage Conflict(string errorCode = "BlobAlreadyExists") =>
        Error(HttpStatusCode.Conflict, errorCode, "The specified blob already exists.");

    /// <summary>503 with <c>ServerBusy</c>, the retryable classification Storage uses for throttling.</summary>
    public static HttpResponseMessage ServerBusy() =>
        Error(HttpStatusCode.ServiceUnavailable, "ServerBusy", "The server is busy.");

    /// <summary>An error response carrying the Storage error code header and XML body.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string errorCode, string message)
    {
        var response = Build(status, etag: null);
        response.Headers.TryAddWithoutValidation("x-ms-error-code", errorCode);
        response.Content = new StringContent(
            $"""<?xml version="1.0" encoding="utf-8"?><Error><Code>{errorCode}</Code><Message>{message}</Message></Error>""");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return response;
    }

    /// <summary>A response carrying a raw body the caller has already shaped.</summary>
    public static HttpResponseMessage WithXml(HttpStatusCode status, string xml)
    {
        var response = Build(status, etag: null);
        response.Content = new StringContent(xml);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return response;
    }

    private static HttpResponseMessage Build(HttpStatusCode status, string? etag = null)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", RequestId);
        response.Headers.TryAddWithoutValidation("x-ms-version", "2025-11-05");
        response.Headers.TryAddWithoutValidation("Date", "Mon, 06 Jul 2026 12:00:00 GMT");
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
            response.Headers.TryAddWithoutValidation("Last-Modified", "Mon, 06 Jul 2026 12:00:00 GMT");
        }

        return response;
    }
}
