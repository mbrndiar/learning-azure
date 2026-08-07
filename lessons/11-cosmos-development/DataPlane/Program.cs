using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace LearningAzure.Lessons.CosmosDevelopment;

/// <summary>
/// Exercises the Cosmos DB data plane the way an application does: reading one
/// document, paging through many, updating one without losing someone else's
/// work, and changing several atomically.
/// </summary>
/// <remarks>
/// Requires the Cosmos DB emulator. Where the emulator's behaviour differs from
/// a real account the difference is printed rather than hidden, because a
/// difference you have seen is worth more than one you have been told about.
/// </remarks>
internal static class Program
{
    private const string EmulatorEndpoint = "https://localhost:8081";

    /// <summary>
    /// The emulator's well-known key. It is published in Microsoft's own
    /// documentation, is identical on every machine, and is worthless outside a
    /// container on localhost.
    /// </summary>
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string DatabaseName = "expedition-journal";
    private const string ContainerName = "readings";
    private const string Station = "station-05";

    /// <summary>How many readings the run seeds.</summary>
    private const int Readings = 120;

    /// <summary>How many documents each page is asked for.</summary>
    private const int PageSize = 25;

    private static async Task<int> Main()
    {
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? EmulatorEndpoint;
        var key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? EmulatorKey;

        var options = new CosmosClientOptions
        {
            // The emulator serves a self-signed certificate. This callback must
            // never survive into a build that talks to a real account.
            ServerCertificateCustomValidationCallback = (_, _, _) => true,
            ConnectionMode = ConnectionMode.Gateway,
            UseSystemTextJsonSerializerWithOptions =
                new JsonSerializerOptions(JsonSerializerDefaults.Web),

            // The SDK retries 429 for you, up to these bounds, before it gives
            // up and throws. Section 7 explains why leaving the default of 9
            // attempts in place is usually right and occasionally catastrophic.
            MaxRetryAttemptsOnRateLimitedRequests = 9,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
        };

        using var client = new CosmosClient(endpoint, key, options);

        Database? database = null;

        try
        {
            database = await SeedAsync(client).ConfigureAwait(false);

            var container = database.GetContainer(ContainerName);

            await PointReadAsync(container).ConfigureAwait(false);
            await PagingAsync(container).ConfigureAwait(false);
            await OptimisticConcurrencyAsync(container).ConfigureAwait(false);
            await PatchAsync(container).ConfigureAwait(false);
            await TransactionalBatchAsync(container).ConfigureAwait(false);
            await ReadManyAsync(container).ConfigureAwait(false);
            await CancellationAsync(container).ConfigureAwait(false);
            await CleanupAsync(container).ConfigureAwait(false);

            WhatTheEmulatorDidNotDo();

            return 0;
        }
        catch (CosmosException failure)
        {
            Console.Error.WriteLine($"Cosmos rejected a request: {failure.StatusCode}.");
            Console.Error.WriteLine(failure.ResponseBody);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Is the emulator running and ready?");
            Console.Error.WriteLine("  docker compose up -d cosmos");
            Console.Error.WriteLine("  curl -sf http://127.0.0.1:8080/ready");

            return 1;
        }
        catch (HttpRequestException unreachable)
        {
            Console.Error.WriteLine($"Cannot reach {endpoint}: {unreachable.Message}");
            Console.Error.WriteLine("  docker compose up -d cosmos");

            return 1;
        }
        finally
        {
            if (database is not null)
            {
                await database.DeleteAsync().ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine($"Deleted database {DatabaseName}.");
            }
        }
    }

    /// <summary>Creates the container and fills it with readings.</summary>
    private static async Task<Database> SeedAsync(CosmosClient client)
    {
        Section("0. Seed", $"One station, one logical partition, {Readings} readings.");

        try
        {
            await client.GetDatabase(DatabaseName).DeleteAsync().ConfigureAwait(false);
        }
        catch (CosmosException notThere) when (notThere.StatusCode == HttpStatusCode.NotFound)
        {
            // Nothing to clean up. This is the ordinary first run.
        }

        var database = (await client.CreateDatabaseAsync(DatabaseName).ConfigureAwait(false)).Database;

        var container = (await database
            .CreateContainerAsync(new ContainerProperties(ContainerName, "/stationId"), 400)
            .ConfigureAwait(false)).Container;

        var charge = 0.0;

        for (var index = 0; index < Readings; index++)
        {
            var reading = new Reading(
                Id: string.Create(CultureInfo.InvariantCulture, $"{Station}-{index:0000}"),
                StationId: Station,
                Sequence: index,
                Celsius: -20 + (index * 0.25),
                Status: "recorded",
                Corrections: 0);

            var response = await container
                .CreateItemAsync(reading, new PartitionKey(Station))
                .ConfigureAwait(false);

            charge += response.RequestCharge;
        }

        Console.WriteLine($"   Documents written         : {Readings}");
        Console.WriteLine($"   Total charge              : {charge:0.00} RU");

        return database;
    }

    /// <summary>Reads one document twice: once by address, once by question.</summary>
    private static async Task PointReadAsync(Container container)
    {
        Section(
            "1. Two ways to fetch one document",
            "They return the same JSON and are not the same operation.");

        var id = string.Create(CultureInfo.InvariantCulture, $"{Station}-0042");

        var direct = await container
            .ReadItemAsync<Reading>(id, new PartitionKey(Station))
            .ConfigureAwait(false);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id")
            .WithParameter("@id", id);

        using var iterator = container.GetItemQueryIterator<Reading>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(Station) });

        var page = await iterator.ReadNextAsync().ConfigureAwait(false);

        Console.WriteLine($"   ReadItemAsync             : {direct.RequestCharge:0.00} RU, status {(int)direct.StatusCode}");
        Console.WriteLine($"   SELECT ... WHERE c.id     : {page.RequestCharge:0.00} RU, {page.Count} document(s)");
        Console.WriteLine($"   ETag from the point read  : {direct.ETag}");
        Console.WriteLine();
        Console.WriteLine("   The point read goes straight to a known address: no query engine,");
        Console.WriteLine("   no plan, no index lookup. The query produces the same document by");
        Console.WriteLine("   asking a question. On a real account the second costs at least");
        Console.WriteLine("   twice the first, and the gap widens with the size of the partition.");
        Console.WriteLine();
        Console.WriteLine("   The parameter matters too. Concatenating the id into the SQL string");
        Console.WriteLine("   would be an injection defect AND would defeat the query plan cache,");
        Console.WriteLine("   because every distinct id would compile a new plan.");
    }

    /// <summary>Drains a query one page at a time, following the continuation token.</summary>
    private static async Task PagingAsync(Container container)
    {
        Section(
            "2. Paging",
            "A result set is not a list. It is a sequence of pages and a token.");

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.stationId = @station ORDER BY c.sequence")
            .WithParameter("@station", Station);

        var options = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(Station),
            MaxItemCount = PageSize,
        };

        string? continuation = null;
        var pages = 0;
        var documents = 0;
        var charge = 0.0;
        var tokenLength = 0;

        do
        {
            using var iterator = container.GetItemQueryIterator<Reading>(
                query,
                continuation,
                options);

            var page = await iterator.ReadNextAsync().ConfigureAwait(false);

            pages++;
            documents += page.Count;
            charge += page.RequestCharge;
            continuation = page.ContinuationToken;
            tokenLength = Math.Max(tokenLength, continuation?.Length ?? 0);
        }
        while (continuation is not null);

        Console.WriteLine($"   MaxItemCount requested    : {PageSize}");
        Console.WriteLine($"   Pages returned            : {pages}");
        Console.WriteLine($"   Documents                 : {documents}");
        Console.WriteLine($"   Longest continuation token: {tokenLength} characters");
        Console.WriteLine($"   Charge                    : {charge:0.00} RU");
        Console.WriteLine();

        if (pages == 1)
        {
            var expected = (documents + PageSize - 1) / PageSize;

            Console.WriteLine("   ONE page. The emulator ignores MaxItemCount and never issues a");
            Console.WriteLine("   continuation token, so this loop ran exactly once. A real account");
            Console.WriteLine($"   would have returned {expected} pages here, and would also cut a page");
            Console.WriteLine("   short on its own at a 4 MB response or a five-second execution");
            Console.WriteLine("   budget, regardless of what you asked for.");
            Console.WriteLine();
            Console.WriteLine("   Which is the point: MaxItemCount is a MAXIMUM, not a promise, and");
            Console.WriteLine("   a page that comes back short is not the end of the results. The");
            Console.WriteLine("   only end-of-results signal is a null continuation token, and code");
            Console.WriteLine("   that stops when a page is smaller than requested silently loses");
            Console.WriteLine("   data on a real account while passing every local test.");
        }
        else
        {
            Console.WriteLine("   Each page carries a token that encodes where to resume. It is");
            Console.WriteLine("   opaque, it belongs to this query and this container, and it is");
            Console.WriteLine("   the only correct end-of-results signal: a short page is not one.");
        }
    }

    /// <summary>Loses a write, then refuses to lose one.</summary>
    private static async Task OptimisticConcurrencyAsync(Container container)
    {
        Section(
            "3. Optimistic concurrency",
            "Two writers, one document, and the difference an ETag makes.");

        var id = string.Create(CultureInfo.InvariantCulture, $"{Station}-0007");
        var key = new PartitionKey(Station);

        // Both readers fetch the same version.
        var first = await container.ReadItemAsync<Reading>(id, key).ConfigureAwait(false);
        var second = await container.ReadItemAsync<Reading>(id, key).ConfigureAwait(false);

        Console.WriteLine($"   Both readers hold ETag    : {first.ETag}");

        // Writer one commits.
        var committed = await container
            .ReplaceItemAsync(first.Resource with { Celsius = -3.5 }, id, key)
            .ConfigureAwait(false);

        Console.WriteLine($"   Writer 1 replaced         : celsius -> -3.5, new ETag {committed.ETag}");

        // Writer two commits blind: last write wins, and writer one's change is
        // gone with no error anywhere.
        await container
            .ReplaceItemAsync(second.Resource with { Celsius = 99.9 }, id, key)
            .ConfigureAwait(false);

        var afterBlindWrite = await container.ReadItemAsync<Reading>(id, key).ConfigureAwait(false);

        Console.WriteLine($"   Writer 2 replaced blind   : celsius is now {afterBlindWrite.Resource.Celsius}");
        Console.WriteLine("   Writer 1's change is gone, and nothing anywhere reported an error.");
        Console.WriteLine();

        // Now the same race, with the ETag attached.
        var third = await container.ReadItemAsync<Reading>(id, key).ConfigureAwait(false);
        var staleEtag = third.ETag;

        await container
            .ReplaceItemAsync(third.Resource with { Celsius = 1.0 }, id, key)
            .ConfigureAwait(false);

        try
        {
            await container
                .ReplaceItemAsync(
                    third.Resource with { Celsius = 2.0 },
                    id,
                    key,
                    new ItemRequestOptions { IfMatchEtag = staleEtag })
                .ConfigureAwait(false);

            Console.WriteLine("   the conditional write succeeded - that would be a bug here");
        }
        catch (CosmosException conflict)
            when (conflict.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            Console.WriteLine($"   Conditional write         : {(int)conflict.StatusCode} {conflict.StatusCode}");
        }

        var final = await container.ReadItemAsync<Reading>(id, key).ConfigureAwait(false);

        Console.WriteLine($"   Document still holds      : celsius {final.Resource.Celsius}");
        Console.WriteLine();
        Console.WriteLine("   412 is not a failure. It is the system telling you that the");
        Console.WriteLine("   document you reasoned about is not the document you are writing to,");
        Console.WriteLine("   and the only correct response is to re-read, re-apply the intent,");
        Console.WriteLine("   and try again - with a bound, because an unbounded retry loop under");
        Console.WriteLine("   contention is a livelock rather than a solution.");
    }

    /// <summary>Changes part of a document without sending the rest.</summary>
    private static async Task PatchAsync(Container container)
    {
        Section(
            "4. Patch",
            "Changing one field without reading, or resending, the other twenty.");

        var id = string.Create(CultureInfo.InvariantCulture, $"{Station}-0011");
        var key = new PartitionKey(Station);

        var patched = await container
            .PatchItemAsync<Reading>(
                id,
                key,
                [
                    PatchOperation.Set("/status", "verified"),
                    PatchOperation.Increment("/corrections", 1),
                ])
            .ConfigureAwait(false);

        Console.WriteLine($"   Status                    : {patched.Resource.Status}");
        Console.WriteLine($"   Corrections               : {patched.Resource.Corrections}");
        Console.WriteLine($"   Celsius (untouched)       : {patched.Resource.Celsius}");
        Console.WriteLine($"   Charge                    : {patched.RequestCharge:0.00} RU");
        Console.WriteLine();

        // A conditional patch: the same protection a conditional replace gets.
        var current = await container.ReadItemAsync<Reading>(id, key).ConfigureAwait(false);
        var stale = current.ETag;

        await container
            .PatchItemAsync<Reading>(id, key, [PatchOperation.Increment("/corrections", 1)])
            .ConfigureAwait(false);

        try
        {
            await container
                .PatchItemAsync<Reading>(
                    id,
                    key,
                    [PatchOperation.Set("/status", "rejected")],
                    new PatchItemRequestOptions { IfMatchEtag = stale })
                .ConfigureAwait(false);

            Console.WriteLine("   the conditional patch succeeded - that would be a bug here");
        }
        catch (CosmosException conflict)
            when (conflict.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            Console.WriteLine($"   Conditional patch         : {(int)conflict.StatusCode} {conflict.StatusCode}");
        }

        Console.WriteLine();
        Console.WriteLine("   Increment is the operation that matters. A read-modify-write on a");
        Console.WriteLine("   counter is a lost update waiting to happen; an Increment is applied");
        Console.WriteLine("   by the server against whatever is there. Patch is still not a");
        Console.WriteLine("   merge, though: it takes an ordered list of operations against known");
        Console.WriteLine("   paths, and it takes an ETag when you need one.");
    }

    /// <summary>Commits several changes as one unit, or none of them.</summary>
    private static async Task TransactionalBatchAsync(Container container)
    {
        Section(
            "5. Transactional batch",
            "All or nothing, inside one logical partition.");

        var key = new PartitionKey(Station);

        var good = await container.CreateTransactionalBatch(key)
            .CreateItem(new Reading($"{Station}-9001", Station, 9001, 0.0, "recorded", 0))
            .CreateItem(new Reading($"{Station}-9002", Station, 9002, 0.5, "recorded", 0))
            .ExecuteAsync()
            .ConfigureAwait(false);

        Console.WriteLine($"   Two creates               : {(int)good.StatusCode} {good.StatusCode}, {good.RequestCharge:0.00} RU");

        // The second operation collides with a document that already exists.
        var bad = await container.CreateTransactionalBatch(key)
            .CreateItem(new Reading($"{Station}-9003", Station, 9003, 1.0, "recorded", 0))
            .CreateItem(new Reading($"{Station}-9001", Station, 9001, 1.5, "recorded", 0))
            .ExecuteAsync()
            .ConfigureAwait(false);

        Console.WriteLine($"   One create, one collision : {(int)bad.StatusCode} {bad.StatusCode}");

        for (var index = 0; index < bad.Count; index++)
        {
            Console.WriteLine(
                $"     operation {index}             : {(int)bad[index].StatusCode} {bad[index].StatusCode}");
        }

        var survivors = await CountAsync(container, "c.sequence IN (9001, 9002, 9003)")
            .ConfigureAwait(false);

        Console.WriteLine($"   Documents from both batches: {survivors}");
        Console.WriteLine();
        Console.WriteLine("   The first batch committed both documents. The second committed");
        Console.WriteLine("   NEITHER: operation 1 was the real failure (409 Conflict) and");
        Console.WriteLine("   operation 0 reports 424 Failed Dependency, which means 'this would");
        Console.WriteLine("   have worked, but the batch did not'. Reading only the batch's own");
        Console.WriteLine("   status code tells you it failed; reading the per-operation codes is");
        Console.WriteLine("   the only way to learn WHICH operation caused it.");
        Console.WriteLine();
        Console.WriteLine("   The hard boundary: a batch is scoped to ONE logical partition. Two");
        Console.WriteLine("   documents that must change together must share a partition key, and");
        Console.WriteLine("   that requirement belongs in the model - which is module 10's job,");
        Console.WriteLine("   decided before a line of this code was written.");
    }

    /// <summary>Fetches many documents by address in one call.</summary>
    private static async Task ReadManyAsync(Container container)
    {
        Section(
            "6. ReadMany",
            "Point reads in bulk, without turning them into a query.");

        var wanted = new List<(string Id, PartitionKey Key)>();

        for (var index = 0; index < 10; index++)
        {
            wanted.Add((
                string.Create(CultureInfo.InvariantCulture, $"{Station}-{index * 7:0000}"),
                new PartitionKey(Station)));
        }

        var many = await container.ReadManyItemsAsync<Reading>(wanted).ConfigureAwait(false);

        Console.WriteLine($"   Requested                 : {wanted.Count} documents by (id, key)");
        Console.WriteLine($"   Returned                  : {many.Count}");
        Console.WriteLine($"   Charge                    : {many.RequestCharge:0.00} RU");
        Console.WriteLine();
        Console.WriteLine("   The alternative is 'SELECT * FROM c WHERE c.id IN (...)', which is");
        Console.WriteLine("   a query: it compiles a plan, consults the index, and on a real");
        Console.WriteLine("   account costs several times as much. ReadMany stays a set of point");
        Console.WriteLine("   reads and simply stops paying the network cost of issuing them one");
        Console.WriteLine("   at a time.");
    }

    /// <summary>Cancels an operation and looks at what the cancellation means.</summary>
    private static async Task CancellationAsync(Container container)
    {
        Section(
            "7. Cancellation",
            "A cancelled write is not a write that did not happen.");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        var id = string.Create(CultureInfo.InvariantCulture, $"{Station}-9500");

        try
        {
            await container
                .CreateItemAsync(
                    new Reading(id, Station, 9500, 0.0, "recorded", 0),
                    new PartitionKey(Station),
                    cancellationToken: cancelled.Token)
                .ConfigureAwait(false);

            Console.WriteLine("   the cancelled write completed - that would be a bug here");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("   Cancelled create          : OperationCanceledException");
        }

        var landed = await CountAsync(container, "c.sequence = 9500").ConfigureAwait(false);

        Console.WriteLine($"   Documents with that id    : {landed}");
        Console.WriteLine();
        Console.WriteLine("   Here the token was already cancelled, so the request never left.");
        Console.WriteLine("   Cancel it a millisecond later and the answer is genuinely unknown:");
        Console.WriteLine("   the write may have been committed by the service and the response");
        Console.WriteLine("   discarded by the client. That is why a cancelled or timed-out write");
        Console.WriteLine("   must be retried through an operation that can safely happen twice -");
        Console.WriteLine("   an upsert with a deterministic id, not a create.");
        Console.WriteLine();
        Console.WriteLine("   The same reasoning applies to the SDK's own 429 retries. It retries");
        Console.WriteLine("   up to 9 times within 30s by default; every one of those attempts is");
        Console.WriteLine("   invisible latency, and a caller with a 2-second deadline needs the");
        Console.WriteLine("   bounds lowered rather than a longer timeout.");
    }

    /// <summary>Removes what the run created, and states what that costs.</summary>
    private static async Task CleanupAsync(Container container)
    {
        Section(
            "8. Cleanup",
            "Deleting is a write, and it is charged like one.");

        var key = new PartitionKey(Station);
        var charge = 0.0;
        var deleted = 0;

        for (var sequence = 9001; sequence <= 9003; sequence++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"{Station}-{sequence}");

            try
            {
                var response = await container
                    .DeleteItemAsync<Reading>(id, key)
                    .ConfigureAwait(false);

                charge += response.RequestCharge;
                deleted++;
            }
            catch (CosmosException missing) when (missing.StatusCode == HttpStatusCode.NotFound)
            {
                // 9003 was rolled back with its batch and was never created.
                Console.WriteLine($"   {id} was never committed: 404 on delete");
            }
        }

        Console.WriteLine($"   Deleted                   : {deleted} documents, {charge:0.00} RU");
        Console.WriteLine();
        Console.WriteLine("   Three ways to remove data, in increasing order of what they cost");
        Console.WriteLine("   you and decreasing order of control:");
        Console.WriteLine();
        Console.WriteLine("     DeleteItemAsync         one document, charged like a write");
        Console.WriteLine("     time-to-live            the service deletes it, using leftover RU/s");
        Console.WriteLine("     delete the container    instant, free, and total");
        Console.WriteLine();
        Console.WriteLine("   There is no 'DELETE FROM c WHERE ...'. A bulk delete is a query");
        Console.WriteLine("   followed by one delete per document, every one of them charged - or");
        Console.WriteLine("   it is a TTL you should have set when you created the container.");
    }

    /// <summary>States plainly which behaviours this run could not exercise.</summary>
    private static void WhatTheEmulatorDidNotDo()
    {
        Section(
            "9. What this run could not exercise",
            "Two of this module's central mechanics have no local behaviour.");

        Console.WriteLine("   PAGING. The emulator ignores MaxItemCount and returns every match");
        Console.WriteLine("   in a single page with a null continuation token, no matter how many");
        Console.WriteLine("   documents there are. The loop in section 2 is correct and ran once.");
        Console.WriteLine("   Against a real account it runs five times, and code written without");
        Console.WriteLine("   it would have silently truncated the result.");
        Console.WriteLine();
        Console.WriteLine("   THROTTLING. There is no rate limiter. Eight hundred concurrent");
        Console.WriteLine("   writes against a container provisioned for 400 RU/s all succeed, so");
        Console.WriteLine("   429, x-ms-retry-after-ms, and the SDK's retry policy never engage.");
        Console.WriteLine("   A load test against the emulator measures your machine, not Cosmos.");
        Console.WriteLine();
        Console.WriteLine("   Request charges are flat 1 RU here as well, so every 'RU' printed");
        Console.WriteLine("   above is a shape, not a price. The management labs in this module");
        Console.WriteLine("   are where those numbers become real.");
    }

    /// <summary>Counts documents matching a predicate inside the station's partition.</summary>
    private static async Task<int> CountAsync(Container container, string predicate)
    {
        var query = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {predicate}");

        using var iterator = container.GetItemQueryIterator<int>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(Station) });

        var total = 0;

        while (iterator.HasMoreResults)
        {
            foreach (var partial in await iterator.ReadNextAsync().ConfigureAwait(false))
            {
                total += partial;
            }
        }

        return total;
    }

    private static void Section(string title, string subtitle)
    {
        var heading = $"{title}: {subtitle}";

        Console.WriteLine();
        Console.WriteLine(heading);
        Console.WriteLine(new string('-', heading.Length));
    }

    /// <summary>One temperature reading.</summary>
    /// <param name="Id">The document id.</param>
    /// <param name="StationId">The partition key.</param>
    /// <param name="Sequence">Its position in the station's series.</param>
    /// <param name="Celsius">The temperature.</param>
    /// <param name="Status">Where the reading is in its review workflow.</param>
    /// <param name="Corrections">How many times it has been corrected.</param>
    private sealed record Reading(
        string Id,
        string StationId,
        int Sequence,
        double Celsius,
        string Status,
        int Corrections);
}
