using System.Globalization;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace LearningAzure.Lessons.StorageAccount;

/// <summary>
/// Shows what a storage account actually <em>is</em>: one name, one set of
/// service endpoints, one auth boundary, three services sharing all of it.
/// </summary>
/// <remarks>
/// Runs against Azurite, so it creates nothing in Azure and costs nothing. The
/// parity table at the end records the differences that change a design
/// decision — the ones the live checkpoint exists to confirm.
/// </remarks>
internal static class Program
{
    /// <summary>The emulator alias. It carries no key: the SDK expands it locally.</summary>
    private const string EmulatorConnectionString = "UseDevelopmentStorage=true";

    private const string Suffix = "expedition-tour";

    private static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING") ?? EmulatorConnectionString;

        var blobService = new BlobServiceClient(connectionString);
        var queueService = new QueueServiceClient(connectionString);
        var tableService = new TableServiceClient(connectionString);

        try
        {
            ShowEndpoints(blobService, queueService, tableService);
            await ShowOneAccountThreeServicesAsync(blobService, queueService, tableService).ConfigureAwait(false);
            ShowLiveShape();
            ShowParity();
        }
        catch (Exception error) when (error is HttpRequestException or AggregateException)
        {
            Console.Error.WriteLine(
                "Could not reach Azurite on 127.0.0.1:10000-10002. Start it with "
                + "'docker compose up -d azurite' and try again.");
            Console.Error.WriteLine(error.Message);
            return 1;
        }

        return 0;
    }

    /// <summary>The account name is the first label of every service endpoint.</summary>
    private static void ShowEndpoints(
        BlobServiceClient blobService,
        QueueServiceClient queueService,
        TableServiceClient tableService)
    {
        Heading("1. One account, one endpoint per service");

        Console.WriteLine($"  account name : {blobService.AccountName}");
        Console.WriteLine($"  blob         : {blobService.Uri}");
        Console.WriteLine($"  queue        : {queueService.Uri}");
        Console.WriteLine($"  table        : {tableService.Uri}");
        Console.WriteLine();
        Console.WriteLine("  The account name is not a label. It is the DNS name every service");
        Console.WriteLine("  endpoint is derived from, which is why it must be globally unique.");
        Console.WriteLine();
    }

    /// <summary>Create one resource in each service to show the shared boundary.</summary>
    private static async Task ShowOneAccountThreeServicesAsync(
        BlobServiceClient blobService,
        QueueServiceClient queueService,
        TableServiceClient tableService)
    {
        Heading("2. Three services inside one boundary");

        var container = blobService.GetBlobContainerClient($"artifacts-{Suffix}");
        var queue = queueService.GetQueueClient($"work-{Suffix}");
        var table = tableService.GetTableClient("observations" + Suffix.Replace("-", string.Empty, StringComparison.Ordinal));

        await container.CreateIfNotExistsAsync().ConfigureAwait(false);
        await queue.CreateIfNotExistsAsync().ConfigureAwait(false);
        await table.CreateIfNotExistsAsync().ConfigureAwait(false);

        Console.WriteLine($"  container : {container.Uri.AbsolutePath}");
        Console.WriteLine($"  queue     : {queue.Uri.AbsolutePath}");
        Console.WriteLine($"  table     : {table.Uri.AbsolutePath}");
        Console.WriteLine();

        // The account root enumerates containers across the whole account, so a
        // raw count depends on whatever else the learner has created in the same
        // Azurite volume. Ask the narrower question instead: is *this* container
        // reachable from the account root? That is the boundary claim, and it is
        // the same answer on a fresh stack and on a well-used one.
        var listedFromAccountRoot = false;
        await foreach (var item in blobService.GetBlobContainersAsync(prefix: container.Name).ConfigureAwait(false))
        {
            listedFromAccountRoot |= string.Equals(item.Name, container.Name, StringComparison.Ordinal);
        }

        Console.WriteLine(
            FormattableString.Invariant($"  this container listed from the account root : {(listedFromAccountRoot ? "yes" : "no")}"));
        Console.WriteLine();
        Console.WriteLine("  One credential reached all three. Deleting the account deletes all");
        Console.WriteLine("  three. Throttling limits apply to all three together. The account is");
        Console.WriteLine("  the unit of billing, naming, access control, and blast radius.");
        Console.WriteLine();

        await container.DeleteIfExistsAsync().ConfigureAwait(false);
        await queue.DeleteIfExistsAsync().ConfigureAwait(false);
        await table.DeleteAsync().ConfigureAwait(false);
        Console.WriteLine("  cleaned up : container, queue, and table deleted");
        Console.WriteLine();
    }

    /// <summary>The endpoint shape the same code produces against real Azure.</summary>
    private static void ShowLiveShape()
    {
        Heading("3. The same account, live");

        const string account = "stexpeditiondev7k2m";
        Console.WriteLine($"  account name : {account}");
        Console.WriteLine($"  blob         : https://{account}.blob.core.windows.net/");
        Console.WriteLine($"  queue        : https://{account}.queue.core.windows.net/");
        Console.WriteLine($"  table        : https://{account}.table.core.windows.net/");
        Console.WriteLine();
        Console.WriteLine("  Live, the constructor takes a Uri and a DefaultAzureCredential instead");
        Console.WriteLine("  of a connection string. Nothing above the adapter changes.");
        Console.WriteLine();
    }

    /// <summary>The differences that change a design decision, not the trivia.</summary>
    private static void ShowParity()
    {
        Heading("4. Emulator parity: what does NOT carry over");

        var rows = new (string Feature, string Azurite, string Live)[]
        {
            ("authentication", "shared key only", "Entra ID + RBAC, shared key optionally disabled"),
            ("redundancy", "single local copy, not configurable", "LRS / ZRS / GRS / GZRS, chosen at creation"),
            ("access tiers", "not enforced", "Hot / Cool / Cold / Archive, with rehydration latency"),
            ("lifecycle rules", "not implemented", "management policy evaluated once per day"),
            ("network rules", "none — anything on localhost", "firewall, private endpoints, service endpoints"),
            ("throttling", "none", "per-account IOPS and ingress/egress limits"),
            ("cost", "zero", "storage GiB-months + transactions + egress"),
        };

        Console.WriteLine($"  {"feature",-18}{"azurite",-38}live");
        Console.WriteLine($"  {new string('-', 18)}{new string('-', 38)}{new string('-', 46)}");
        foreach (var (feature, azurite, live) in rows)
        {
            Console.WriteLine($"  {feature,-18}{azurite,-38}{live}");
        }

        Console.WriteLine();
        Console.WriteLine("  Every row above is a reason the live checkpoint in this module is not");
        Console.WriteLine("  optional: redundancy, tiers, and the auth boundary cannot be observed");
        Console.WriteLine("  here at all.");
    }

    private static void Heading(string title)
    {
        Console.WriteLine(title.ToUpper(CultureInfo.InvariantCulture));
        Console.WriteLine(new string('=', 72));
    }
}
