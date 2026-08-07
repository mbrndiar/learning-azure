using System.Diagnostics;
using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace LearningAzure.Lessons.TableStorage;

/// <summary>
/// Loads the same 5,000 observations once and answers the same question three
/// ways — point read, partition scan, table scan — counting what each costs.
/// </summary>
/// <remarks>
/// Everything printed here is measured against Azurite, not estimated. The
/// three queries return the same entity; only the key predicates differ.
/// </remarks>
internal static class Program
{
    /// <summary>The emulator alias. It carries no key: the SDK expands it locally.</summary>
    private const string EmulatorConnectionString = "UseDevelopmentStorage=true";

    private const string TableName = "expeditionobservations";
    private const int Stations = 5;
    private const int ReadingsPerStation = 1000;

    private static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING") ?? EmulatorConnectionString;

        var service = new TableServiceClient(connectionString);
        var table = service.GetTableClient(TableName);

        try
        {
            await table.CreateIfNotExistsAsync().ConfigureAwait(false);

            var target = await SeedAsync(table).ConfigureAwait(false);
            await ShowPointReadAsync(table, target).ConfigureAwait(false);
            await ShowPartitionScanAsync(table, target).ConfigureAwait(false);
            await ShowTableScanAsync(table, target).ConfigureAwait(false);
            await ShowConcurrencyAsync(table, target).ConfigureAwait(false);
            await ShowBatchLimitsAsync(table).ConfigureAwait(false);
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
                "Could not reach Azurite on 127.0.0.1:10002. Start it with "
                + "'docker compose up -d azurite' and try again.");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            await table.DeleteAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Writes the data set in transactional batches and returns one known row.</summary>
    private static async Task<(string PartitionKey, string RowKey)> SeedAsync(TableClient table)
    {
        Console.WriteLine("0. Seeding");
        Console.WriteLine("----------");

        var start = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        var batches = 0;

        for (var station = 1; station <= Stations; station++)
        {
            var partitionKey = $"station-{station:00}|2026-07-06";
            var operations = new List<TableTransactionAction>();

            for (var reading = 0; reading < ReadingsPerStation; reading++)
            {
                var observedAt = start.AddMinutes(reading);
                var entity = new TableEntity(partitionKey, RowKey(observedAt))
                {
                    ["StationId"] = $"station-{station:00}",
                    ["ObservedAt"] = observedAt.UtcDateTime,
                    ["TemperatureC"] = -3.0 - (reading % 17),
                    ["Status"] = "pending",
                };

                operations.Add(new TableTransactionAction(TableTransactionActionType.Add, entity));

                if (operations.Count == 100)
                {
                    await table.SubmitTransactionAsync(operations).ConfigureAwait(false);
                    operations.Clear();
                    batches++;
                }
            }
        }

        stopwatch.Stop();

        Console.WriteLine(
            $"   {Stations * ReadingsPerStation} entities in {batches} transactional batches "
            + $"({stopwatch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"   {Stations} partitions of {ReadingsPerStation} rows each");
        Console.WriteLine();

        // Pick the target row from the middle of a partition rather than a fixed
        // minute offset, so the bounded experiment at the end of the lesson —
        // which changes ReadingsPerStation — still names a row that exists.
        return ($"station-03|2026-07-06", RowKey(start.AddMinutes(ReadingsPerStation / 2)));
    }

    /// <summary>Section 1: both keys known.</summary>
    private static async Task ShowPointReadAsync(TableClient table, (string PartitionKey, string RowKey) target)
    {
        Console.WriteLine("1. Point read: both keys known");
        Console.WriteLine("------------------------------");

        var stopwatch = Stopwatch.StartNew();
        var response = await table
            .GetEntityAsync<TableEntity>(target.PartitionKey, target.RowKey)
            .ConfigureAwait(false);
        stopwatch.Stop();

        Console.WriteLine($"   PartitionKey        : {target.PartitionKey}");
        Console.WriteLine($"   RowKey              : {target.RowKey}");
        Console.WriteLine($"   Entities returned   : 1");
        Console.WriteLine($"   Entities read by the service: 1 (a keyed GET reads one row, by construction)");
        Console.WriteLine($"   ETag                : {response.Value.ETag}");
        Console.WriteLine($"   Elapsed             : {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine();
    }

    /// <summary>Section 2: the partition key only.</summary>
    private static async Task ShowPartitionScanAsync(TableClient table, (string PartitionKey, string RowKey) target)
    {
        Console.WriteLine("2. Partition scan: partition key only");
        Console.WriteLine("-------------------------------------");

        var filter = $"PartitionKey eq '{target.PartitionKey}' and RowKey eq '{target.RowKey}'";
        var (returned, elapsed) = await CountAsync(table, filter).ConfigureAwait(false);

        Console.WriteLine($"   Filter              : PartitionKey eq '…' and RowKey eq '…'");
        Console.WriteLine($"   Entities returned   : {returned}");
        Console.WriteLine($"   Elapsed             : {elapsed:F1} ms");
        var wideFilter = $"PartitionKey eq '{target.PartitionKey}' and TemperatureC lt -18.0";
        var wide = await CountAsync(table, wideFilter).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"   Now filter the same partition on a NON-KEY property:");
        Console.WriteLine($"   Filter              : PartitionKey eq '…' and TemperatureC lt -18.0");
        Console.WriteLine($"   Entities returned   : {wide.Returned}");
        Console.WriteLine($"   Elapsed             : {wide.Elapsed:F1} ms");
        Console.WriteLine($"   Candidate partition : {ReadingsPerStation} rows");
        Console.WriteLine(
            "   Table Storage does not expose an exact server-side scanned-row count.");
        Console.WriteLine();
    }

    /// <summary>Section 3: no usable key at all.</summary>
    private static async Task ShowTableScanAsync(TableClient table, (string PartitionKey, string RowKey) target)
    {
        Console.WriteLine("3. Table scan: no key predicate");
        Console.WriteLine("-------------------------------");

        var filter = $"RowKey eq '{target.RowKey}'";
        var (returned, elapsed) = await CountAsync(table, filter).ConfigureAwait(false);

        Console.WriteLine($"   Filter              : RowKey eq '…'   (no PartitionKey!)");
        Console.WriteLine($"   Entities returned   : {returned}");
        Console.WriteLine($"   Elapsed             : {elapsed:F1} ms");
        Console.WriteLine(
            $"   Same row key in every partition, so it returned {returned} rows from "
            + $"{Stations * ReadingsPerStation}.");

        var propertyFilter = "StationId eq 'station-03'";
        var property = await CountAsync(table, propertyFilter).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("   And the query that LOOKS identical to a partition scan:");
        Console.WriteLine($"   Filter              : StationId eq 'station-03'");
        Console.WriteLine($"   Entities returned   : {property.Returned}");
        Console.WriteLine($"   Elapsed             : {property.Elapsed:F1} ms");
        Console.WriteLine($"   Candidate table rows : {Stations * ReadingsPerStation}");
        Console.WriteLine(
            "   Same syntax, wider key range: StationId is a duplicated column, not a key.");
        Console.WriteLine();
    }

    /// <summary>Section 4: the entity ETag, and what a stale one does.</summary>
    private static async Task ShowConcurrencyAsync(TableClient table, (string PartitionKey, string RowKey) target)
    {
        Console.WriteLine("4. The entity ETag");
        Console.WriteLine("------------------");

        var alice = (await table.GetEntityAsync<TableEntity>(target.PartitionKey, target.RowKey)
            .ConfigureAwait(false)).Value;
        var bob = (await table.GetEntityAsync<TableEntity>(target.PartitionKey, target.RowKey)
            .ConfigureAwait(false)).Value;

        Console.WriteLine($"   alice read ETag     : {alice.ETag}");
        Console.WriteLine($"   bob   read ETag     : {bob.ETag}  (identical: same version)");

        alice["Status"] = "ingested";
        var aliceWrite = await table.UpdateEntityAsync(alice, alice.ETag, TableUpdateMode.Replace)
            .ConfigureAwait(false);

        Console.WriteLine($"   alice write         : HTTP {aliceWrite.Status}, new ETag {aliceWrite.Headers.ETag}");

        bob["Status"] = "rejected";

        try
        {
            await table.UpdateEntityAsync(bob, bob.ETag, TableUpdateMode.Replace).ConfigureAwait(false);
            Console.WriteLine("   bob   write         : succeeded — alice's change is gone");
        }
        catch (RequestFailedException error)
        {
            Console.WriteLine($"   bob   write         : REJECTED {error.ErrorCode} (HTTP {error.Status})");
            Console.WriteLine(
                "   Bob was told. That is the whole difference between a lost update and a");
            Console.WriteLine("   retry: one is silent and one is a status code.");
        }

        var stored = (await table.GetEntityAsync<TableEntity>(target.PartitionKey, target.RowKey)
            .ConfigureAwait(false)).Value;
        Console.WriteLine($"   stored Status       : {stored.GetString("Status")}");
        Console.WriteLine();
    }

    /// <summary>Section 5: what a transactional batch will and will not accept.</summary>
    private static async Task ShowBatchLimitsAsync(TableClient table)
    {
        Console.WriteLine("5. Transactional batches");
        Console.WriteLine("------------------------");

        var start = new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

        var samePartition = new List<TableTransactionAction>
        {
            Add("station-01|2026-07-07", RowKey(start)),
            Add("station-01|2026-07-07", RowKey(start.AddMinutes(1))),
        };

        var accepted = await table.SubmitTransactionAsync(samePartition).ConfigureAwait(false);
        Console.WriteLine($"   two rows, one partition   : accepted, {accepted.Value.Count} sub-responses");

        var crossPartition = new List<TableTransactionAction>
        {
            Add("station-01|2026-07-07", RowKey(start.AddMinutes(2))),
            Add("station-02|2026-07-07", RowKey(start.AddMinutes(2))),
        };

        await ReportAsync(
            "two rows, two partitions  ",
            () => table.SubmitTransactionAsync(crossPartition)).ConfigureAwait(false);

        var oversized = Enumerable.Range(0, 101)
            .Select(n => Add("station-01|2026-07-08", RowKey(start.AddDays(1).AddMinutes(n))))
            .ToList();

        await ReportAsync(
            "101 rows, one partition   ",
            () => table.SubmitTransactionAsync(oversized)).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(
            "   Read those two results again. Azure rejects both with InvalidInput; Azurite");
        Console.WriteLine(
            "   accepted the cross-partition batch outright and answered the oversized one");
        Console.WriteLine(
            "   with a response the SDK cannot even parse. The emulator does not enforce");
        Console.WriteLine(
            "   either rule, which is exactly why the exercise validates them in your own");
        Console.WriteLine("   code instead of discovering them in production.");
        Console.WriteLine();
        Console.WriteLine(
            "   The operation limit is a splitting problem. The partition limit is a");
        Console.WriteLine(
            "   DESIGN problem: two entities that must land together must share a");
        Console.WriteLine("   partition key, and that decision is made before any data exists.");
        Console.WriteLine();
    }

    /// <summary>Runs a batch submission and reports exactly what came back.</summary>
    private static async Task ReportAsync(string label, Func<Task> submit)
    {
        try
        {
            await submit().ConfigureAwait(false);
            Console.WriteLine($"   {label}: ACCEPTED — the emulator does not enforce this rule");
        }
        catch (RequestFailedException error)
        {
            Console.WriteLine($"   {label}: rejected {error.ErrorCode} (HTTP {error.Status})");
        }
        catch (InvalidOperationException error)
        {
            Console.WriteLine(
                $"   {label}: the emulator returned a response the SDK could not parse "
                + $"({error.Message.Split('\n')[0]})");
        }
    }

    private static TableTransactionAction Add(string partitionKey, string rowKey) =>
        new(
            TableTransactionActionType.Add,
            new TableEntity(partitionKey, rowKey) { ["Status"] = "pending" });

    private static async Task<(int Returned, double Elapsed)> CountAsync(
        TableClient table,
        string filter)
    {
        var stopwatch = Stopwatch.StartNew();
        var returned = 0;

        await foreach (var _ in table.QueryAsync<TableEntity>(filter).ConfigureAwait(false))
        {
            returned++;
        }

        stopwatch.Stop();
        return (returned, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static string RowKey(DateTimeOffset observedAt) =>
        observedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
