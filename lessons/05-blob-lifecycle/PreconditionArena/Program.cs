using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LearningAzure.Lessons.BlobLifecycle;

/// <summary>
/// Stages a lost update, then prevents it — twice, against the same emulator,
/// with the only difference being one HTTP header.
/// </summary>
/// <remarks>
/// Everything printed here is a real response from Azurite. Section 4 is the
/// point of the live checkpoint: the emulator answers "no" to questions a real
/// account answers "yes" to, and the difference is not cosmetic.
/// </remarks>
internal static class Program
{
    /// <summary>The emulator alias. It carries no key: the SDK expands it locally.</summary>
    private const string EmulatorConnectionString = "UseDevelopmentStorage=true";

    private const string ContainerName = "expedition-lifecycle";
    private const string BlobName = "observations/station-bravo/notes.txt";

    private static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING") ?? EmulatorConnectionString;

        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient(ContainerName);

        try
        {
            await container.CreateIfNotExistsAsync().ConfigureAwait(false);

            await ShowLostUpdateAsync(container).ConfigureAwait(false);
            await ShowConditionalWriteAsync(container).ConfigureAwait(false);
            await ShowCreateIfAbsentAsync(container).ConfigureAwait(false);
            await ShowEmulatorLimitsAsync(service, container).ConfigureAwait(false);
        }
        catch (RequestFailedException error)
        {
            Console.Error.WriteLine($"The service rejected a request: {error.ErrorCode} (HTTP {error.Status}).");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (Exception error) when (error is HttpRequestException or AggregateException)
        {
            Console.Error.WriteLine(
                "Could not reach Azurite on 127.0.0.1:10000. Start it with "
                + "'docker compose up -d azurite' and try again.");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Two writers, no preconditions, one silently destroyed update.</summary>
    private static async Task ShowLostUpdateAsync(BlobContainerClient container)
    {
        Heading("1. The lost update, with no error anywhere");

        var blob = container.GetBlobClient(BlobName);
        await blob.UploadAsync(BinaryData.FromString("temp=-3C"), overwrite: true).ConfigureAwait(false);

        // Both field laptops read the same starting point.
        var alice = (await blob.DownloadContentAsync().ConfigureAwait(false)).Value.Content.ToString();
        var bob = (await blob.DownloadContentAsync().ConfigureAwait(false)).Value.Content.ToString();

        Console.WriteLine($"  alice read : {alice}");
        Console.WriteLine($"  bob   read : {bob}");

        await blob.UploadAsync(BinaryData.FromString($"{alice};wind=12kt"), overwrite: true).ConfigureAwait(false);
        Console.WriteLine("  alice wrote: temp=-3C;wind=12kt   -> HTTP 201");

        await blob.UploadAsync(BinaryData.FromString($"{bob};ice=thin"), overwrite: true).ConfigureAwait(false);
        Console.WriteLine("  bob   wrote: temp=-3C;ice=thin    -> HTTP 201");

        var final = (await blob.DownloadContentAsync().ConfigureAwait(false)).Value.Content.ToString();
        Console.WriteLine();
        Console.WriteLine($"  stored now : {final}");
        Console.WriteLine("  alice's wind reading is gone. Both writes returned 201. Nothing");
        Console.WriteLine("  failed, nothing logged, and no retry would have helped.");
    }

    /// <summary>The same race, with If-Match. The second writer is told, in HTTP.</summary>
    private static async Task ShowConditionalWriteAsync(BlobContainerClient container)
    {
        Heading("2. The same race, with one header");

        var blob = container.GetBlobClient(BlobName);
        await blob.UploadAsync(BinaryData.FromString("temp=-3C"), overwrite: true).ConfigureAwait(false);

        var aliceRead = await blob.DownloadContentAsync().ConfigureAwait(false);
        var bobRead = await blob.DownloadContentAsync().ConfigureAwait(false);

        var aliceETag = aliceRead.GetRawResponse().Headers.ETag!.Value;
        var bobETag = bobRead.GetRawResponse().Headers.ETag!.Value;

        Console.WriteLine($"  alice read ETag : {aliceETag.ToString("H")}");
        Console.WriteLine($"  bob   read ETag : {bobETag.ToString("H")}");
        Console.WriteLine($"  identical       : {aliceETag == bobETag}");

        var afterAlice = await blob.UploadAsync(
            BinaryData.FromString("temp=-3C;wind=12kt"),
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = aliceETag } })
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  alice wrote with If-Match: {aliceETag.ToString("H")} -> HTTP 201");
        Console.WriteLine($"  the ETag is now          : {afterAlice.Value.ETag.ToString("H")}");

        try
        {
            await blob.UploadAsync(
                BinaryData.FromString("temp=-3C;ice=thin"),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = bobETag } })
                .ConfigureAwait(false);

            Console.WriteLine("  bob wrote -> HTTP 201 (this line should be unreachable)");
        }
        catch (RequestFailedException error) when (error.Status == 412)
        {
            Console.WriteLine($"  bob   wrote with If-Match: {bobETag.ToString("H")} -> HTTP {error.Status} {error.ErrorCode}");
        }

        var final = (await blob.DownloadContentAsync().ConfigureAwait(false)).Value.Content.ToString();
        Console.WriteLine();
        Console.WriteLine($"  stored now : {final}");
        Console.WriteLine("  bob's write was refused, not silently applied. He now knows his");
        Console.WriteLine("  copy is stale and can re-read, re-apply, and try again.");
    }

    /// <summary>"Only if it does not exist" is one header, not a check-then-write.</summary>
    private static async Task ShowCreateIfAbsentAsync(BlobContainerClient container)
    {
        Heading("3. Create-if-absent is a header, not a check");

        var blob = container.GetBlobClient("observations/station-bravo/claim.txt");
        var conditions = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        };

        var first = await blob.UploadAsync(BinaryData.FromString("claimed by node-1"), conditions)
            .ConfigureAwait(false);
        Console.WriteLine($"  node-1 create with If-None-Match: * -> HTTP {first.GetRawResponse().Status}");

        try
        {
            await blob.UploadAsync(BinaryData.FromString("claimed by node-2"), conditions).ConfigureAwait(false);
            Console.WriteLine("  node-2 create -> succeeded (this line should be unreachable)");
        }
        catch (RequestFailedException error)
        {
            Console.WriteLine($"  node-2 create with If-None-Match: * -> HTTP {error.Status} {error.ErrorCode}");
        }

        var owner = (await blob.DownloadContentAsync().ConfigureAwait(false)).Value.Content.ToString();
        Console.WriteLine();
        Console.WriteLine($"  stored now : {owner}");
        Console.WriteLine("  exactly one node won, decided by the service. An 'ExistsAsync then");
        Console.WriteLine("  Upload' would have let both of them think they won.");
    }

    /// <summary>What the emulator will not answer, and why that needs a live checkpoint.</summary>
    private static async Task ShowEmulatorLimitsAsync(BlobServiceClient service, BlobContainerClient container)
    {
        Heading("4. What Azurite cannot decide for you");

        var properties = await service.GetPropertiesAsync().ConfigureAwait(false);
        var softDelete = properties.Value.DeleteRetentionPolicy;

        Console.WriteLine($"  service reports soft delete enabled : {softDelete?.Enabled.ToString() ?? "(not reported)"}");
        Console.WriteLine($"  service reports retention days      : {softDelete?.Days?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(not reported)"}");

        var blob = container.GetBlobClient(BlobName);
        var withVersions = await blob.GetPropertiesAsync().ConfigureAwait(false);
        Console.WriteLine($"  blob reports a version id           : {withVersions.Value.VersionId ?? "(none)"}");

        Console.WriteLine();
        Console.WriteLine("  Conditional writes are identical here and in Azure: same headers,");
        Console.WriteLine("  same 412, same semantics. Everything below is not:");
        Console.WriteLine();
        Console.WriteLine("    versioning        - no version id above means no version to promote");
        Console.WriteLine("    soft delete       - undelete cannot be rehearsed here");
        Console.WriteLine("    lifecycle rules   - the management plane the rules live in is absent");
        Console.WriteLine("    blob index tags   - the account-wide tag index does not exist");
        Console.WriteLine("    tier transitions  - Archive and its rehydration delay are not emulated");
        Console.WriteLine();
        Console.WriteLine("  A retention promise that has only been tested here has not been");
        Console.WriteLine("  tested. That is what the required live checkpoint is for.");
    }

    private static void Heading(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }
}
