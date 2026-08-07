using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace LearningAzure.Lessons.CosmosModeling;

/// <summary>
/// Stores identical documents in two containers that differ by one property
/// name — the partition key — and then asks both of them the same questions.
/// </summary>
/// <remarks>
/// Requires the Cosmos DB emulator. Every count printed below is measured
/// against the running emulator; the request charges are not, and the lesson
/// says so at the point where it matters.
/// </remarks>
internal static class Program
{
    private const string EmulatorEndpoint = "https://localhost:8081";

    /// <summary>
    /// The emulator's well-known key. It is published in Microsoft's own
    /// documentation, is identical on every machine, and is worthless outside a
    /// container on localhost — which is exactly why a real account's key must
    /// never be pasted anywhere a source file can reach.
    /// </summary>
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string DatabaseName = "expedition";
    private const string ByStationContainer = "readings-by-station";
    private const string ByDayContainer = "readings-by-day";
    private const string SerialsContainer = "station-serials";

    /// <summary>How many stations the sample data covers.</summary>
    private const int Stations = 8;

    /// <summary>How many readings each station reports.</summary>
    private const int ReadingsPerStation = 25;

    /// <summary>The single day every reading in this run belongs to.</summary>
    private const string Day = "2026-08-07";

    /// <summary>The station whose readings every question in this run asks for.</summary>
    private const string Station = "station-05";

    private static async Task<int> Main()
    {
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? EmulatorEndpoint;
        var key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? EmulatorKey;

        var options = new CosmosClientOptions
        {
            // The emulator serves a self-signed certificate. This callback is
            // the emulator's price of admission and must never survive into a
            // build that talks to a real account.
            ServerCertificateCustomValidationCallback = (_, _, _) => true,
            ConnectionMode = ConnectionMode.Gateway,

            // Without this the SDK's default serializer writes "Id" and Cosmos
            // rejects the document. The id property is lower-case, always: it is
            // part of the wire contract rather than a naming preference.
            UseSystemTextJsonSerializerWithOptions =
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
        };

        using var client = new CosmosClient(endpoint, key, options);

        Database? database = null;

        try
        {
            database = await CreateContainersAsync(client).ConfigureAwait(false);

            var byStation = database.GetContainer(ByStationContainer);
            var byDay = database.GetContainer(ByDayContainer);
            var serials = database.GetContainer(SerialsContainer);

            await WriteAsync(byStation, byDay).ConfigureAwait(false);
            await PointReadAsync(byStation).ConfigureAwait(false);
            await SinglePartitionQueryAsync(byStation).ConfigureAwait(false);
            await TheSameQuestionOfBothModelsAsync(byStation, byDay).ConfigureAwait(false);
            await SkewAsync(byStation, byDay).ConfigureAwait(false);
            await CrossPartitionAsync(byStation).ConfigureAwait(false);
            await UniqueKeysAsync(serials).ConfigureAwait(false);
            await ThroughputAsync(byStation).ConfigureAwait(false);

            WhatTheNumbersDidNotSay();

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
                // The database is the unit of cleanup. Deleting it removes every
                // container, document and throughput allocation in one call, so
                // a failed run cannot leave the next one reading stale data.
                await database.DeleteAsync().ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine($"Deleted database {DatabaseName}.");
            }
        }
    }

    /// <summary>
    /// Creates two containers that hold identical documents and differ only in
    /// which property Cosmos uses to place them.
    /// </summary>
    private static async Task<Database> CreateContainersAsync(CosmosClient client)
    {
        Section(
            "0. One decision, two containers",
            "The documents are identical. The partition key is not.");

        // Deleting first makes the run repeatable: a container's partition key
        // and unique key policy are fixed at creation and cannot be replaced.
        try
        {
            await client.GetDatabase(DatabaseName).DeleteAsync().ConfigureAwait(false);
        }
        catch (CosmosException notThere) when (notThere.StatusCode == HttpStatusCode.NotFound)
        {
            // Nothing to clean up. This is the ordinary first run.
        }

        var database = await client.CreateDatabaseAsync(DatabaseName).ConfigureAwait(false);

        var byStation = await database.Database
            .CreateContainerAsync(new ContainerProperties(ByStationContainer, "/stationId"), 400)
            .ConfigureAwait(false);

        var byDay = await database.Database
            .CreateContainerAsync(new ContainerProperties(ByDayContainer, "/day"), 400)
            .ConfigureAwait(false);

        // A unique key is scoped to a logical partition, which is why the policy
        // is declared on the container and enforced per partition key value.
        var serialProperties = new ContainerProperties(SerialsContainer, "/region");
        serialProperties.UniqueKeyPolicy.UniqueKeys.Add(new UniqueKey { Paths = { "/serial" } });

        await database.Database
            .CreateContainerAsync(serialProperties, 400)
            .ConfigureAwait(false);

        Console.WriteLine($"   {ByStationContainer,-22} partition key /stationId   400 RU/s");
        Console.WriteLine($"   {ByDayContainer,-22} partition key /day         400 RU/s");
        Console.WriteLine($"   {SerialsContainer,-22} partition key /region      400 RU/s");
        Console.WriteLine();
        Console.WriteLine("   The first two hold exactly the same documents. Everything that");
        Console.WriteLine("   follows is a consequence of that one property name.");

        return database.Database;
    }

    /// <summary>Writes the sample readings to both containers.</summary>
    private static async Task WriteAsync(Container byStation, Container byDay)
    {
        Section(
            "1. The documents",
            "Eight stations, twenty-five readings each, all on one day.");

        var written = 0;

        for (var station = 1; station <= Stations; station++)
        {
            var stationId = string.Create(CultureInfo.InvariantCulture, $"station-{station:00}");

            for (var index = 0; index < ReadingsPerStation; index++)
            {
                var reading = new Reading(
                    Id: string.Create(CultureInfo.InvariantCulture, $"{stationId}-{index:0000}"),
                    StationId: stationId,
                    Day: Day,
                    Sequence: index,
                    Celsius: -20 + (index * 0.5));

                // The partition key passed here must match the value inside the
                // document. Cosmos does not derive it; it verifies it.
                await byStation.CreateItemAsync(reading, new PartitionKey(reading.StationId))
                    .ConfigureAwait(false);

                await byDay.CreateItemAsync(reading, new PartitionKey(reading.Day))
                    .ConfigureAwait(false);

                written++;
            }
        }

        Console.WriteLine($"   Documents written         : {written} to each container");
        Console.WriteLine($"   Stations                  : {Stations}");
        Console.WriteLine($"   Readings per station      : {ReadingsPerStation}");
        Console.WriteLine();
        Console.WriteLine("   Not one byte differs between the two containers. What differs is");
        Console.WriteLine("   where each document landed, and that is decided entirely by the");
        Console.WriteLine("   partition key path declared when the container was created.");
    }

    /// <summary>
    /// Reads one document by id — first with the right partition key, then with
    /// the wrong one.
    /// </summary>
    private static async Task PointReadAsync(Container byStation)
    {
        Section(
            "2. The point read",
            "The partition key is not metadata. It is half the address.");

        var id = string.Create(CultureInfo.InvariantCulture, $"{Station}-0007");

        var found = await byStation
            .ReadItemAsync<Reading>(id, new PartitionKey(Station))
            .ConfigureAwait(false);

        Console.WriteLine($"   Read {id} with /stationId = {Station}");
        Console.WriteLine($"     status                  : {(int)found.StatusCode} {found.StatusCode}");
        Console.WriteLine($"     celsius                 : {found.Resource.Celsius}");

        try
        {
            await byStation
                .ReadItemAsync<Reading>(id, new PartitionKey("station-01"))
                .ConfigureAwait(false);

            Console.WriteLine("     the wrong key found it — that would be a bug in this lesson");
        }
        catch (CosmosException missing) when (missing.StatusCode == HttpStatusCode.NotFound)
        {
            Console.WriteLine();
            Console.WriteLine($"   Read {id} with /stationId = station-01");
            Console.WriteLine($"     status                  : {(int)missing.StatusCode} {missing.StatusCode}");
        }

        Console.WriteLine();
        Console.WriteLine("   The document exists. The read still failed, and it failed with 404");
        Console.WriteLine("   rather than an error, because the id was looked for in a partition");
        Console.WriteLine("   that never held it. An id is unique WITHIN a logical partition, not");
        Console.WriteLine("   within a container: (partition key, id) is the primary key.");
    }

    /// <summary>Queries one logical partition and measures how much of it was read.</summary>
    private static async Task SinglePartitionQueryAsync(Container byStation)
    {
        Section(
            "3. A query inside one logical partition",
            "Everything examined is something returned.");

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.stationId = @station ORDER BY c.sequence DESC")
            .WithParameter("@station", Station);

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(Station) };

        var returned = await CountAsync(byStation, query, options).ConfigureAwait(false);
        var partitionSize = await LogicalPartitionSizeAsync(byStation, "stationId", Station)
            .ConfigureAwait(false);

        Report(returned, partitionSize);

        Console.WriteLine();
        Console.WriteLine("   The filter is the partition key, so the partition it selects holds");
        Console.WriteLine("   nothing else. Read amplification of 1.00x is the target every");
        Console.WriteLine("   partition key design is aiming at, and the number that tells you");
        Console.WriteLine("   whether it hit.");
    }

    /// <summary>Asks both containers the same question and compares the work.</summary>
    private static async Task TheSameQuestionOfBothModelsAsync(Container byStation, Container byDay)
    {
        Section(
            "4. The same question, two models",
            $"'Everything {Station} reported on {Day}.'");

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.stationId = @station AND c.day = @day")
            .WithParameter("@station", Station)
            .WithParameter("@day", Day);

        var stationReturned = await CountAsync(
                byStation,
                query,
                new QueryRequestOptions { PartitionKey = new PartitionKey(Station) })
            .ConfigureAwait(false);

        var stationPartition = await LogicalPartitionSizeAsync(byStation, "stationId", Station)
            .ConfigureAwait(false);

        var dayReturned = await CountAsync(
                byDay,
                query,
                new QueryRequestOptions { PartitionKey = new PartitionKey(Day) })
            .ConfigureAwait(false);

        var dayPartition = await LogicalPartitionSizeAsync(byDay, "day", Day)
            .ConfigureAwait(false);

        Console.WriteLine("                              returned   partition   amplification");
        Console.WriteLine(
            $"   /stationId              {stationReturned,10} {stationPartition,11}   "
            + $"{Amplification(stationReturned, stationPartition):0.00}x");
        Console.WriteLine(
            $"   /day                    {dayReturned,10} {dayPartition,11}   "
            + $"{Amplification(dayReturned, dayPartition):0.00}x");
        Console.WriteLine();
        Console.WriteLine("   Same question, same answer, same documents. The /day container had");
        Console.WriteLine($"   to sit on a partition holding all {Stations} stations and discard {Stations - 1}");
        Console.WriteLine($"   readings out of every {Stations}. That ratio is not a constant: it IS the");
        Console.WriteLine("   number of stations. Add stations and the /stationId model does");
        Console.WriteLine("   not move, while /day gets linearly worse.");
    }

    /// <summary>Measures how evenly each key spreads the same documents.</summary>
    private static async Task SkewAsync(Container byStation, Container byDay)
    {
        Section(
            "5. Skew",
            "A partition key is a promise about distribution, and it is checkable.");

        var stationGroups = await GroupAsync(byStation, "stationId").ConfigureAwait(false);
        var dayGroups = await GroupAsync(byDay, "day").ConfigureAwait(false);

        Describe("/stationId", stationGroups);
        Describe("/day", dayGroups);

        Console.WriteLine();
        Console.WriteLine("   A logical partition is capped at 20 GB and served by one physical");
        Console.WriteLine("   partition's throughput. /day does not just read badly: on the day");
        Console.WriteLine("   it is current it absorbs EVERY write in the system, and no amount");
        Console.WriteLine("   of provisioned RU/s can be spent on a partition that does not exist.");
    }

    /// <summary>Runs a query that cannot name a partition key.</summary>
    private static async Task CrossPartitionAsync(Container byStation)
    {
        Section(
            "6. The query that cannot name a key",
            "Fan-out is not a slower query. It is a different query.");

        var query = new QueryDefinition("SELECT * FROM c WHERE c.celsius > @threshold")
            .WithParameter("@threshold", -15.0);

        var returned = await CountAsync(byStation, query, options: null).ConfigureAwait(false);
        var total = await LogicalPartitionSizeAsync(byStation, path: null, value: null)
            .ConfigureAwait(false);

        var ranges = await byStation.GetFeedRangesAsync().ConfigureAwait(false);

        Console.WriteLine($"   Documents returned        : {returned}");
        Console.WriteLine($"   Documents in container    : {total}");
        Console.WriteLine($"   Read amplification        : {Amplification(returned, total):0.00}x");
        Console.WriteLine($"   Logical partitions        : {Stations}");
        Console.WriteLine($"   Physical partitions       : {ranges.Count}");
        Console.WriteLine();
        Console.WriteLine("   The predicate touches no partition key, so the query is dispatched");
        Console.WriteLine("   to every physical partition and the results are merged by the");
        Console.WriteLine("   client SDK. The cost scales with the number of physical partitions,");
        Console.WriteLine("   not with the number of rows that come back — which is why a");
        Console.WriteLine("   cross-partition query that returns one document can still be the");
        Console.WriteLine("   most expensive operation in an application.");
    }

    /// <summary>Shows the one constraint Cosmos will enforce for you.</summary>
    private static async Task UniqueKeysAsync(Container serials)
    {
        Section(
            "7. Unique keys",
            "The only uniqueness Cosmos enforces beyond the id, and it is per partition.");

        var first = new StationSerial("north-01", "arctic", "SN-4417");
        await serials.CreateItemAsync(first, new PartitionKey(first.Region)).ConfigureAwait(false);
        Console.WriteLine($"   Created {first.Id,-10} region={first.Region,-8} serial={first.Serial}");

        var duplicate = new StationSerial("north-02", "arctic", "SN-4417");

        try
        {
            await serials.CreateItemAsync(duplicate, new PartitionKey(duplicate.Region))
                .ConfigureAwait(false);

            Console.WriteLine("   the duplicate was accepted — that would be a bug in this lesson");
        }
        catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
        {
            Console.WriteLine(
                $"   Rejected {duplicate.Id,-9} region={duplicate.Region,-8} "
                + $"serial={duplicate.Serial}  -> {(int)conflict.StatusCode} {conflict.StatusCode}");
        }

        var elsewhere = new StationSerial("south-01", "antarctic", "SN-4417");
        await serials.CreateItemAsync(elsewhere, new PartitionKey(elsewhere.Region))
            .ConfigureAwait(false);

        Console.WriteLine(
            $"   Created {elsewhere.Id,-10} region={elsewhere.Region,-8} serial={elsewhere.Serial}");
        Console.WriteLine();
        Console.WriteLine("   The same serial was refused inside one region and accepted in");
        Console.WriteLine("   another. A unique key policy is scoped to the logical partition, so");
        Console.WriteLine("   global uniqueness is only available when the partition key is");
        Console.WriteLine("   global — and the policy is fixed for the life of the container.");
    }

    /// <summary>Reads and changes the container's throughput.</summary>
    private static async Task ThroughputAsync(Container byStation)
    {
        Section(
            "8. Throughput",
            "Provisioned RU/s is a rate, and it is divided before it is spent.");

        var manual = await byStation.ReadThroughputAsync().ConfigureAwait(false);
        Console.WriteLine($"   Manual throughput         : {manual} RU/s");

        var autoscale = await byStation
            .ReplaceThroughputAsync(ThroughputProperties.CreateAutoscaleThroughput(1000))
            .ConfigureAwait(false);

        Console.WriteLine(
            $"   Autoscale maximum         : {autoscale.Resource.AutoscaleMaxThroughput} RU/s");
        Console.WriteLine(
            $"   Autoscale floor (10%)     : {autoscale.Resource.AutoscaleMaxThroughput / 10} RU/s");
        Console.WriteLine();
        Console.WriteLine("   Provisioned throughput is split evenly across PHYSICAL partitions,");
        Console.WriteLine("   and each logical partition is capped at its physical partition's");
        Console.WriteLine("   share. 400 RU/s over four physical partitions is 100 RU/s each, no");
        Console.WriteLine("   matter how idle the other three are. This is why a hot partition");
        Console.WriteLine("   throttles while the account-level chart shows plenty of headroom.");
    }

    /// <summary>States plainly which numbers this run could not produce.</summary>
    private static void WhatTheNumbersDidNotSay()
    {
        Section(
            "9. What this run could not measure",
            "The emulator answers questions about shape, not about price.");

        Console.WriteLine("   Every response above carried a request charge of 1 RU, including");
        Console.WriteLine("   the 200-document cross-partition query. That is not a discovery");
        Console.WriteLine("   about Cosmos; it is a limitation of the emulator, which does not");
        Console.WriteLine("   run the metering that produces a real charge. The same is true of");
        Console.WriteLine("   the query metrics header: retrievedDocumentCount comes back as 0.");
        Console.WriteLine();
        Console.WriteLine("   So this lesson measures the thing the emulator DOES model — how");
        Console.WriteLine("   many documents a question has to be asked of — and treats request");
        Console.WriteLine("   units as what they are: a charge proportional to that number. The");
        Console.WriteLine("   proportion itself has to be read off a real account, which is what");
        Console.WriteLine("   the live checkpoint in this module is for.");
    }

    // ---------------------------------------------------------------------
    // Measurement helpers
    // ---------------------------------------------------------------------

    /// <summary>Runs a query to completion and returns how many documents it produced.</summary>
    private static async Task<int> CountAsync(
        Container container,
        QueryDefinition query,
        QueryRequestOptions? options)
    {
        using var iterator = container.GetItemQueryIterator<Reading>(query, requestOptions: options);

        var count = 0;

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync().ConfigureAwait(false);
            count += page.Count;
        }

        return count;
    }

    /// <summary>
    /// Counts the documents a query on <paramref name="path"/> would have to be
    /// evaluated against — the size of the logical partition it lands in.
    /// </summary>
    private static async Task<int> LogicalPartitionSizeAsync(
        Container container,
        string? path,
        string? value)
    {
        var query = path is null
            ? new QueryDefinition("SELECT VALUE COUNT(1) FROM c")
            : new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE c.{path} = @value")
                .WithParameter("@value", value);

        var options = value is null
            ? null
            : new QueryRequestOptions { PartitionKey = new PartitionKey(value) };

        using var iterator = container.GetItemQueryIterator<int>(query, requestOptions: options);

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

    /// <summary>Counts the documents in every logical partition of a container.</summary>
    private static async Task<List<PartitionCount>> GroupAsync(Container container, string path)
    {
        var query = new QueryDefinition(
            $"SELECT c.{path} AS key, COUNT(1) AS count FROM c GROUP BY c.{path}");

        using var iterator = container.GetItemQueryIterator<PartitionCount>(query);

        var groups = new List<PartitionCount>();

        while (iterator.HasMoreResults)
        {
            groups.AddRange(await iterator.ReadNextAsync().ConfigureAwait(false));
        }

        groups.Sort(static (left, right) => right.Count.CompareTo(left.Count));

        return groups;
    }

    private static void Describe(string key, List<PartitionCount> groups)
    {
        var total = groups.Sum(static group => group.Count);
        var largest = groups.Count == 0 ? 0 : groups[0].Count;
        var share = total == 0 ? 0 : 100.0 * largest / total;

        Console.WriteLine(
            $"   {key,-12} partitions {groups.Count,3}   largest {largest,4} docs "
            + $"({share:0.0}% of all documents)   key of largest: {groups[0].Key}");
    }

    private static void Report(int returned, int partitionSize)
    {
        Console.WriteLine($"   Documents returned        : {returned}");
        Console.WriteLine($"   Documents in the partition: {partitionSize}");
        Console.WriteLine($"   Read amplification        : {Amplification(returned, partitionSize):0.00}x");
    }

    private static double Amplification(int returned, int examined) =>
        returned == 0 ? examined : (double)examined / returned;

    private static void Section(string title, string subtitle)
    {
        var heading = $"{title}: {subtitle}";

        Console.WriteLine();
        Console.WriteLine(heading);
        Console.WriteLine(new string('-', heading.Length));
    }

    /// <summary>One temperature reading from one station.</summary>
    /// <param name="Id">The document id, unique within its logical partition.</param>
    /// <param name="StationId">Which station reported it.</param>
    /// <param name="Day">The day it belongs to.</param>
    /// <param name="Sequence">Its position in the station's series for the day.</param>
    /// <param name="Celsius">The temperature.</param>
    private sealed record Reading(
        string Id,
        string StationId,
        string Day,
        int Sequence,
        double Celsius);

    /// <summary>A station's hardware serial, used to demonstrate unique keys.</summary>
    /// <param name="Id">The document id.</param>
    /// <param name="Region">The partition key, and the scope of the unique key.</param>
    /// <param name="Serial">The value that must not repeat within a region.</param>
    private sealed record StationSerial(string Id, string Region, string Serial);

    /// <summary>How many documents one logical partition holds.</summary>
    /// <param name="Key">The partition key value.</param>
    /// <param name="Count">The number of documents stored under it.</param>
    private sealed record PartitionCount(string Key, int Count);
}
