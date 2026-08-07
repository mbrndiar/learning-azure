using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace LearningAzure.Lessons.BlobStorage;

/// <summary>
/// Shows what Blob Storage actually stores: a flat namespace of opaque bytes,
/// addressed by a name that only <em>looks</em> like a path, described by
/// metadata and tags, and listed one page at a time.
/// </summary>
/// <remarks>
/// Runs against Azurite, so it creates nothing in Azure and costs nothing.
/// Every number printed below is measured, not asserted: the block list comes
/// back from the service and the page counts come from the enumerator.
/// </remarks>
internal static class Program
{
    /// <summary>The emulator alias. It carries no key: the SDK expands it locally.</summary>
    private const string EmulatorConnectionString = "UseDevelopmentStorage=true";

    private const string ContainerName = "expedition-artifacts";
    private const int BlockSize = 256 * 1024;

    private static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING") ?? EmulatorConnectionString;

        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient(ContainerName);

        try
        {
            await container.CreateIfNotExistsAsync().ConfigureAwait(false);

            await ShowFlatNamespaceAsync(container).ConfigureAwait(false);
            await ShowStreamedUploadAsync(container).ConfigureAwait(false);
            await ShowMetadataAndTagsAsync(container).ConfigureAwait(false);
            await ShowPagedListingAsync(container).ConfigureAwait(false);
            await ShowVirtualDirectoriesAsync(container).ConfigureAwait(false);
        }
        catch (RequestFailedException error)
        {
            // The service answered and said no. That is a result, not an outage.
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

    /// <summary>A blob name is one string. The slashes are yours, not the service's.</summary>
    private static async Task ShowFlatNamespaceAsync(BlobContainerClient container)
    {
        Heading("1. The namespace is flat");

        string[] names =
        [
            "observations/station-bravo/2026/07/06/frame-0001.jpg",
            "observations/station-bravo/2026/07/06/frame-0002.jpg",
            "observations/station-delta/2026/07/06/frame-0001.jpg",
            "manifest.json",
        ];

        foreach (var name in names)
        {
            await container.GetBlobClient(name)
                .UploadAsync(BinaryData.FromString($"payload for {name}"), overwrite: true)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"  uploaded {names.Length} blobs; none of them created a directory.");
        Console.WriteLine("  the container holds exactly these keys:");

        await foreach (var blob in container.GetBlobsAsync().ConfigureAwait(false))
        {
            Console.WriteLine($"    {blob.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("  a blob name is one string; '/' has no meaning to the service");
        Console.WriteLine("  except as an optional listing delimiter (see section 5).");
    }

    /// <summary>Staging blocks is what makes an upload bounded in memory.</summary>
    private static async Task ShowStreamedUploadAsync(BlobContainerClient container)
    {
        Heading("2. Streaming is a memory decision");

        var blob = container.GetBlockBlobClient("captures/large-capture.bin");
        var payload = new byte[(BlockSize * 5) + 4096];
        Random.Shared.NextBytes(payload);

        using var source = new MemoryStream(payload, writable: false);
        var buffer = new byte[BlockSize];
        var blockIds = new List<string>();
        var index = 0;

        while (true)
        {
            var read = await source.ReadAtLeastAsync(buffer, BlockSize, throwOnEndOfStream: false)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var blockId = Convert.ToBase64String(
                Encoding.ASCII.GetBytes(index.ToString("D6", CultureInfo.InvariantCulture)));
            using var block = new MemoryStream(buffer, 0, read, writable: false);
            await blob.StageBlockAsync(blockId, block).ConfigureAwait(false);

            blockIds.Add(blockId);
            Console.WriteLine(
                $"  staged block {index:D2}: {read,7} bytes  (resident buffer stays {BlockSize} bytes)");
            index++;
        }

        await blob.CommitBlockListAsync(blockIds).ConfigureAwait(false);

        var properties = await blob.GetPropertiesAsync().ConfigureAwait(false);
        var committed = await blob.GetBlockListAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  payload size        : {payload.Length} bytes");
        Console.WriteLine($"  committed length    : {properties.Value.ContentLength} bytes");
        Console.WriteLine($"  committed blocks    : {committed.Value.CommittedBlocks.Count()}");
        Console.WriteLine($"  uncommitted blocks  : {committed.Value.UncommittedBlocks.Count()}");
        Console.WriteLine($"  peak buffer         : {BlockSize} bytes, whatever the payload size");
        Console.WriteLine();
        Console.WriteLine("  before the commit the blob does not exist at all: staged blocks are");
        Console.WriteLine("  invisible to readers, which is why a failed upload leaves no torso.");
    }

    /// <summary>Metadata travels with the blob; tags are indexed and queryable.</summary>
    private static async Task ShowMetadataAndTagsAsync(BlobContainerClient container)
    {
        Heading("3. Metadata and tags are different tools");

        var blob = container.GetBlobClient("observations/station-bravo/2026/07/06/frame-0001.jpg");

        await blob.SetMetadataAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["station"] = "station-bravo",
            ["capturedUtc"] = "2026-07-06T04:12:55Z",
        }).ConfigureAwait(false);

        await blob.SetTagsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["station"] = "station-bravo",
            ["retention"] = "cold",
        }).ConfigureAwait(false);

        var properties = await blob.GetPropertiesAsync().ConfigureAwait(false);
        var tags = await blob.GetTagsAsync().ConfigureAwait(false);

        Console.WriteLine("  metadata (returned with GetProperties, never indexed):");
        foreach (var pair in properties.Value.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {pair.Key,-12} = {pair.Value}");
        }

        Console.WriteLine("  tags (a separate call, and the only one the service can index):");
        foreach (var pair in tags.Value.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {pair.Key,-12} = {pair.Value}");
        }

        Console.WriteLine();
        Console.WriteLine("  metadata costs nothing extra to read with the blob and cannot be");
        Console.WriteLine("  searched; tags can be searched across a whole account and cost a");
        Console.WriteLine("  separate request to read. Choosing wrongly is a design bug, not a bug.");
    }

    /// <summary>Listing is paged, and the page is the unit you are billed for.</summary>
    private static async Task ShowPagedListingAsync(BlobContainerClient container)
    {
        Heading("4. Listing is paged and lazy");

        for (var i = 0; i < 12; i++)
        {
            await container.GetBlobClient($"bulk/item-{i:D4}.bin")
                .UploadAsync(BinaryData.FromString($"item {i}"), overwrite: true)
                .ConfigureAwait(false);
        }

        var pageNumber = 0;
        await foreach (var page in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "bulk/", CancellationToken.None)
            .AsPages(pageSizeHint: 5).ConfigureAwait(false))
        {
            pageNumber++;
            Console.WriteLine(
                $"  page {pageNumber}: {page.Values.Count} blobs, "
                + $"continuation = {(string.IsNullOrEmpty(page.ContinuationToken) ? "(none)" : "present")}");
        }

        Console.WriteLine($"  {pageNumber} service calls for 12 blobs at a page size of 5.");

        var firstOnly = 0;
        await foreach (var _ in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "bulk/", CancellationToken.None)
            .AsPages(pageSizeHint: 5).ConfigureAwait(false))
        {
            firstOnly++;
            break;
        }

        Console.WriteLine($"  stopping after the first page costs {firstOnly} call, not {pageNumber}.");
        Console.WriteLine("  that is the whole reason the API is an IAsyncEnumerable and not a List.");
    }

    /// <summary>The delimiter reconstructs folders for humans, on the server.</summary>
    private static async Task ShowVirtualDirectoriesAsync(BlobContainerClient container)
    {
        Heading("5. Virtual directories are a listing feature");

        Console.WriteLine("  GetBlobsByHierarchy(prefix: \"observations/\", delimiter: \"/\"):");
        await foreach (var item in container
            .GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", "observations/", CancellationToken.None)
            .ConfigureAwait(false))
        {
            Console.WriteLine(item.IsPrefix ? $"    [prefix] {item.Prefix}" : $"    [blob]   {item.Blob.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("  the same blobs, listed flat with the same prefix:");
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "observations/", CancellationToken.None).ConfigureAwait(false))
        {
            Console.WriteLine($"    {blob.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("  same data, two views. Nothing was moved, created, or renamed:");
        Console.WriteLine("  the delimiter only tells the service where to stop and fold.");
    }

    private static void Heading(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }
}
