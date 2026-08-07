using System.Diagnostics;
using System.Globalization;
using Azure;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Blobs;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Lessons.SdkFoundations;

/// <summary>
/// Makes the four seams every Azure SDK client exposes observable, without a
/// service behind them.
/// </summary>
/// <remarks>
/// The tour drives a real <see cref="BlobContainerClient"/> — its real pipeline,
/// its real retry policy, its real response parsing — over a scripted transport.
/// Nothing here reaches the network, so the output is the same on every machine
/// and in continuous integration.
/// </remarks>
internal static class Program
{
    private static readonly Uri ContainerUri = new("https://stexpedition.blob.core.windows.net/stations");

    private static async Task Main()
    {
        ShowCredentialSeam();
        await ShowRetrySeamAsync().ConfigureAwait(false);
        await ShowCancellationSeamAsync().ConfigureAwait(false);
        await ShowErrorClassificationSeamAsync().ConfigureAwait(false);
    }

    /// <summary>Seam 1 — where the client gets its identity, and where it must not.</summary>
    private static void ShowCredentialSeam()
    {
        Heading("1. Credential seam");

        // Constructing a credential performs no network call and reads no secret;
        // the chain is only walked when a token is first requested.
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
        });

        Console.WriteLine($"  live credential   : {credential.GetType().FullName}");
        Console.WriteLine("  resolves          : environment -> workload identity -> managed identity -> Azure CLI -> ...");
        Console.WriteLine("  secret in source  : none — the chain reads the ambient environment");
        Console.WriteLine();
        Console.WriteLine("  emulator          : Azurite's well-known development account");
        Console.WriteLine("  secret in source  : none — read from the AZURITE_CONNECTION_STRING variable");
        Console.WriteLine("  boundary          : the emulator key is public and worthless; it is NOT a pattern for live Azure");
        Console.WriteLine();
    }

    /// <summary>Seam 2 — the retry policy, and the fact that it is bounded.</summary>
    private static async Task ShowRetrySeamAsync()
    {
        Heading("2. Retry seam");

        // Storage classifies 503 ServerBusy as retryable, so the pipeline retries
        // it without the application seeing anything.
        var handler = new ScriptedHandler(
            _ => StorageResponses.ServerBusy(),
            _ => StorageResponses.ServerBusy(),
            _ => StorageResponses.OkWithBody("{\"stationId\":\"station-bravo\"}"u8.ToArray(), "application/json"));

        var client = CreateClient(handler, maxRetries: 3);

        var response = await client.GetBlobClient("station-bravo.json")
            .DownloadContentAsync()
            .ConfigureAwait(false);

        Console.WriteLine($"  configured retries : 3, exponential, zero delay for this tour");
        Console.WriteLine($"  transport attempts : {handler.AttemptCount}");
        Console.WriteLine($"  application saw    : one call returning HTTP {response.GetRawResponse().Status}");
        foreach (var (attempt, request) in handler.Requests.Select((request, index) => (index + 1, request)))
        {
            Console.WriteLine(
                FormattableString.Invariant(
                    $"    attempt {attempt}: {request.Method} {request.Uri.AbsolutePath}"));
        }

        Console.WriteLine();
        Console.WriteLine("  Retries are BOUNDED. With MaxRetries = 3 the client makes at most four");
        Console.WriteLine("  attempts and then surfaces the failure. An unbounded retry loop turns a");
        Console.WriteLine("  throttled service into an outage that never resolves.");
        Console.WriteLine();
    }

    /// <summary>Seam 3 — the cancellation token, and what honouring it looks like.</summary>
    private static async Task ShowCancellationSeamAsync()
    {
        Heading("3. Cancellation seam");

        var handler = ScriptedHandler.Always(_ => StorageResponses.ServerBusy());
        var client = CreateClient(handler, maxRetries: 5);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.GetBlobClient("station-bravo.json")
                .DownloadContentAsync(cancellation.Token)
                .ConfigureAwait(false);
            Console.WriteLine("  UNREACHABLE: the call should not have completed");
        }
        catch (OperationCanceledException error)
        {
            stopwatch.Stop();
            Console.WriteLine($"  exception type     : {error.GetType().FullName}");
            Console.WriteLine($"  transport attempts : {handler.AttemptCount}");
            Console.WriteLine("  elapsed            : immediate — no retry budget was spent");
        }

        Console.WriteLine();
        Console.WriteLine("  A cancelled token stops the operation BEFORE the retry policy runs. Code");
        Console.WriteLine("  that catches Exception and returns a default here converts a caller's");
        Console.WriteLine("  cancellation into a silent wrong answer.");
        Console.WriteLine();
    }

    /// <summary>Seam 4 — error classification: which failures are values and which are exceptions.</summary>
    private static async Task ShowErrorClassificationSeamAsync()
    {
        Heading("4. Error-classification seam");

        foreach (var (label, response) in new (string, Func<HttpResponseMessage>)[]
        {
            ("missing blob", () => StorageResponses.NotFound()),
            ("no permission", () => StorageResponses.Error(System.Net.HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch", "This request is not authorized.")),
        })
        {
            var handler = ScriptedHandler.Always(_ => response());
            var client = CreateClient(handler, maxRetries: 0);

            try
            {
                await client.GetBlobClient("station-bravo.json").DownloadContentAsync().ConfigureAwait(false);
            }
            catch (RequestFailedException error)
            {
                Console.WriteLine(
                    FormattableString.Invariant(
                        $"  {label,-14}: status {error.Status}, ErrorCode '{error.ErrorCode}', attempts {handler.AttemptCount}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Both arrive as RequestFailedException, and the Status and ErrorCode are the");
        Console.WriteLine("  only things that distinguish them. 404 is usually an expected value — the");
        Console.WriteLine("  station has no record yet — and belongs in a TryGet that returns null. 403");
        Console.WriteLine("  is a configuration defect and must keep propagating.");
        Console.WriteLine();
        Console.WriteLine("  Notice the attempt counts: neither was retried. Storage classifies 404 and");
        Console.WriteLine("  403 as non-retryable, so the pipeline surfaces them immediately.");
    }

    /// <summary>
    /// The construction seam: options carry the transport, the retry budget, and
    /// the timeout, which is exactly why an SDK client is testable at all.
    /// </summary>
    private static BlobContainerClient CreateClient(ScriptedHandler handler, int maxRetries)
    {
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler)),
        };
        options.Retry.MaxRetries = maxRetries;
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);

        return new BlobContainerClient(ContainerUri, options);
    }

    private static void Heading(string title)
    {
        Console.WriteLine(title.ToUpper(CultureInfo.InvariantCulture));
        Console.WriteLine(new string('=', 60));
    }
}
