using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;

namespace LearningAzure.Lessons.EventHubsModel;

/// <summary>
/// Shows what a partitioned event stream actually does with the events you hand
/// it: how a partition key decides placement, what a batch refuses to hold, and
/// why the same events can be read twice.
/// </summary>
/// <remarks>
/// Runs against the Event Hubs emulator declared in compose.yaml. The
/// connection string below is the emulator's published development credential:
/// it is identical in every installation, grants access to nothing outside this
/// machine, and is the only reason a key appears in source anywhere in this
/// course.
/// </remarks>
internal static class Program
{
    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;"
        + "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string EventHubName = "telemetry";

    /// <summary>Stations whose telemetry this run publishes.</summary>
    private static readonly string[] Stations =
        ["station-01", "station-02", "station-03", "station-04", "station-05"];

    /// <summary>Readings published per station in section 2.</summary>
    private const int ReadingsPerStation = 20;

    private static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("EVENTHUBS_CONNECTION_STRING") ?? EmulatorConnectionString;
        var hubName = Environment.GetEnvironmentVariable("EVENTHUBS_NAME") ?? EventHubName;

        // A run tag makes every section count only the events THIS run wrote.
        // The stream is append-only and retains everything, so without it the
        // second run of this companion would read the first run's events too —
        // which is itself the point of section 5.
        var runTag = DateTimeOffset.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture);

        Console.WriteLine($"Run tag: {runTag}");

        await using var producer = new EventHubProducerClient(connectionString, hubName);

        try
        {
            await DescribeTheHubAsync(producer).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EventHubsException or TimeoutException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Could not reach the Event Hubs emulator: {ex.Message}");
            Console.Error.WriteLine("Start it with:  ACCEPT_EULA=Y docker compose up -d eventhubs");
            return 1;
        }

        var partitionIds = await producer.GetPartitionIdsAsync().ConfigureAwait(false);
        var before = await ReadSequenceNumbersAsync(producer, partitionIds).ConfigureAwait(false);

        await SendWithoutAPartitionKeyAsync(producer, runTag).ConfigureAwait(false);
        await SendWithAPartitionKeyAsync(producer, runTag).ConfigureAwait(false);
        await FillOneBatchAsync(producer).ConfigureAwait(false);
        await ReadTheStreamBackAsync(connectionString, hubName, partitionIds, before, runTag).ConfigureAwait(false);
        await ShowWhatCannotBeChangedAsync(producer, partitionIds).ConfigureAwait(false);

        return 0;
    }

    // -------------------------------------------------------------------------
    // 0. The hub is a fixed set of partitions
    // -------------------------------------------------------------------------
    private static async Task DescribeTheHubAsync(EventHubProducerClient producer)
    {
        Section("0. The hub", "The namespace, the hub, and the partition count");

        var properties = await producer.GetEventHubPropertiesAsync().ConfigureAwait(false);

        Console.WriteLine($"   Fully qualified namespace : {producer.FullyQualifiedNamespace}");
        Console.WriteLine($"   Event hub                 : {properties.Name}");
        Console.WriteLine($"   Created                   : {properties.CreatedOn:u}");
        Console.WriteLine(
            $"   Partition ids             : {string.Join(", ", properties.PartitionIds)} "
            + $"({properties.PartitionIds.Length} partitions)");
        Console.WriteLine("   The partition count is fixed at creation. Everything below is a");
        Console.WriteLine("   consequence of that one number.");
    }

    // -------------------------------------------------------------------------
    // 1. No partition key: the service spreads events for throughput
    // -------------------------------------------------------------------------
    private static async Task SendWithoutAPartitionKeyAsync(EventHubProducerClient producer, string runTag)
    {
        Section("1. No partition key", "Throughput, and no ordering guarantee at all");

        using var batch = await producer.CreateBatchAsync().ConfigureAwait(false);

        for (var index = 0; index < 20; index++)
        {
            var reading = new Reading(Stations[index % Stations.Length], index, runTag);
            if (!batch.TryAdd(new EventData(Serialize(reading))))
            {
                break;
            }
        }

        await producer.SendAsync(batch).ConfigureAwait(false);

        Console.WriteLine($"   Sent                      : {batch.Count} events, no partition key");
        Console.WriteLine("   Landed on                 : ONE partition, chosen by the service");
        Console.WriteLine("   Ordering between batches  : none that you may rely on");
        Console.WriteLine("   The unit of placement is the BATCH, not the event. A keyless send");
        Console.WriteLine("   spreads load across many sends; it does not fan one send out. If");
        Console.WriteLine("   you expected 5 events on each of 4 partitions, section 4 will");
        Console.WriteLine("   disagree with you.");
    }

    // -------------------------------------------------------------------------
    // 2. A partition key co-locates a station's readings
    // -------------------------------------------------------------------------
    private static async Task SendWithAPartitionKeyAsync(EventHubProducerClient producer, string runTag)
    {
        Section("2. A partition key", "One station, one partition, in order, forever");

        var stopwatch = Stopwatch.StartNew();
        var batches = 0;
        var events = 0;

        foreach (var station in Stations)
        {
            // One batch per key. A batch carries ONE partition key for every
            // event in it, which is why a producer that keys its events cannot
            // also batch across keys.
            var options = new CreateBatchOptions { PartitionKey = station };
            using var batch = await producer.CreateBatchAsync(options).ConfigureAwait(false);

            for (var index = 0; index < ReadingsPerStation; index++)
            {
                var reading = new Reading(station, index, runTag);
                if (!batch.TryAdd(new EventData(Serialize(reading))))
                {
                    break;
                }
            }

            await producer.SendAsync(batch).ConfigureAwait(false);
            batches++;
            events += batch.Count;
        }

        stopwatch.Stop();

        Console.WriteLine(
            $"   Sent                      : {events} events in {batches} batches, "
            + $"one partition key each ({stopwatch.ElapsedMilliseconds} ms)");
        Console.WriteLine("   Guarantee bought          : all of a station's readings are on one");
        Console.WriteLine("                               partition, in send order");
        Console.WriteLine("   Guarantee NOT bought      : which partition. The key is hashed; the");
        Console.WriteLine("                               mapping is stable but not yours to pick");
        Console.WriteLine("   Section 4 reads the placement back and shows it.");
    }

    // -------------------------------------------------------------------------
    // 3. A batch is a size budget, and it tells you when it is full
    // -------------------------------------------------------------------------
    private static async Task FillOneBatchAsync(EventHubProducerClient producer)
    {
        Section("3. The batch", "TryAdd returns false; it does not throw and it does not send");

        using var batch = await producer.CreateBatchAsync().ConfigureAwait(false);

        Console.WriteLine($"   Maximum size              : {batch.MaximumSizeInBytes:N0} bytes");
        Console.WriteLine($"   Size when empty           : {batch.SizeInBytes:N0} bytes");

        var payload = new string('r', 1024);
        var accepted = 0;
        long sizeAtLastAccept = 0;

        while (batch.TryAdd(new EventData(Encoding.UTF8.GetBytes(payload))))
        {
            accepted++;
            sizeAtLastAccept = batch.SizeInBytes;

            if (accepted > 10_000)
            {
                break;
            }
        }

        Console.WriteLine($"   1 KiB events accepted     : {accepted:N0}");
        Console.WriteLine($"   Size when full            : {sizeAtLastAccept:N0} bytes");
        Console.WriteLine(
            $"   Overhead per event        : "
            + $"~{(sizeAtLastAccept / (double)accepted) - 1024:F1} bytes above the 1,024-byte body");
        Console.WriteLine("   The batch was NOT sent. TryAdd returning false is the signal to");
        Console.WriteLine("   send what you have and start a new batch — an unchecked TryAdd is");
        Console.WriteLine("   how events get dropped without an exception anywhere.");
    }

    // -------------------------------------------------------------------------
    // 4 and 5. Read the stream back, twice
    // -------------------------------------------------------------------------
    private static async Task ReadTheStreamBackAsync(
        string connectionString,
        string hubName,
        string[] partitionIds,
        IReadOnlyDictionary<string, long> before,
        string runTag)
    {
        Section("4. Placement", "Where the keyed events actually landed");

        await using var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            connectionString,
            hubName);

        var firstPass = await ReadRunAsync(consumer, partitionIds, before, runTag).ConfigureAwait(false);

        foreach (var partitionId in partitionIds)
        {
            var stations = firstPass.StationsByPartition.TryGetValue(partitionId, out var found)
                ? string.Join(", ", found.Order(StringComparer.Ordinal))
                : "(none)";

            var count = firstPass.CountsByPartition.TryGetValue(partitionId, out var total) ? total : 0;
            var keyless = firstPass.KeylessByPartition.TryGetValue(partitionId, out var bare) ? bare : 0;

            Console.WriteLine(
                $"   partition {partitionId} : {count,3} events ({keyless,2} keyless)   keys: {stations}");
        }

        Console.WriteLine();
        Console.WriteLine("   Every station appears under exactly one partition. Five keys did");
        Console.WriteLine("   not produce five partitions, and no amount of retrying moves one.");
        Console.WriteLine("   The keyless batch from section 1 sits whole on a single partition.");

        Section("5. Replay", "The same read, again, from the beginning");

        var secondPass = await ReadRunAsync(consumer, partitionIds, before, runTag).ConfigureAwait(false);

        Console.WriteLine($"   First pass                : {firstPass.Total} events");
        Console.WriteLine($"   Second pass               : {secondPass.Total} events");
        Console.WriteLine($"   Identical                 : {firstPass.Total == secondPass.Total}");
        Console.WriteLine("   Reading did not consume anything. There is no acknowledgement, no");
        Console.WriteLine("   lock, and no delete: a reader is a cursor over a log that the");
        Console.WriteLine("   retention window — not the reader — decides when to drop.");
        Console.WriteLine("   That is the whole difference from module 6's queue.");
    }

    // -------------------------------------------------------------------------
    // 6. The immutable decisions
    // -------------------------------------------------------------------------
    private static async Task ShowWhatCannotBeChangedAsync(EventHubProducerClient producer, string[] partitionIds)
    {
        Section("6. Sequence numbers and offsets", "Per partition, and never global");

        foreach (var partitionId in partitionIds)
        {
            var properties = await producer.GetPartitionPropertiesAsync(partitionId).ConfigureAwait(false);

            Console.WriteLine(
                $"   partition {partitionId} : beginning {properties.BeginningSequenceNumber,6}   "
                + $"last {properties.LastEnqueuedSequenceNumber,6}   empty {properties.IsEmpty}");
        }

        Console.WriteLine();
        Console.WriteLine("   Sequence numbers restart per partition, so 'event 41' is not a");
        Console.WriteLine("   position in the stream — it is a position in ONE partition. A");
        Console.WriteLine("   checkpoint is therefore per partition too, which is module 9.");
    }

    private static async Task<RunTotals> ReadRunAsync(
        EventHubConsumerClient consumer,
        string[] partitionIds,
        IReadOnlyDictionary<string, long> before,
        string runTag)
    {
        var countsByPartition = new Dictionary<string, int>(StringComparer.Ordinal);
        var keylessByPartition = new Dictionary<string, int>(StringComparer.Ordinal);
        var stationsByPartition = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var total = 0;

        foreach (var partitionId in partitionIds)
        {
            var startAfter = before[partitionId];
            var position = startAfter < 0
                ? EventPosition.Earliest
                : EventPosition.FromSequenceNumber(startAfter, isInclusive: false);

            var options = new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(2) };

            await foreach (var partitionEvent in consumer
                .ReadEventsFromPartitionAsync(partitionId, position, options)
                .ConfigureAwait(false))
            {
                if (partitionEvent.Data is null)
                {
                    break;
                }

                var reading = JsonSerializer.Deserialize<Reading>(partitionEvent.Data.EventBody.ToMemory().Span);
                if (reading is null || !string.Equals(reading.Run, runTag, StringComparison.Ordinal))
                {
                    continue;
                }

                total++;
                countsByPartition[partitionId] = countsByPartition.GetValueOrDefault(partitionId) + 1;

                if (partitionEvent.Data.PartitionKey is { Length: > 0 } key)
                {
                    if (!stationsByPartition.TryGetValue(partitionId, out var keys))
                    {
                        keys = new HashSet<string>(StringComparer.Ordinal);
                        stationsByPartition[partitionId] = keys;
                    }

                    keys.Add(key);
                }
                else
                {
                    keylessByPartition[partitionId] = keylessByPartition.GetValueOrDefault(partitionId) + 1;
                }
            }
        }

        return new RunTotals(total, countsByPartition, keylessByPartition, stationsByPartition);
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadSequenceNumbersAsync(
        EventHubProducerClient producer,
        string[] partitionIds)
    {
        var sequenceNumbers = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var partitionId in partitionIds)
        {
            var properties = await producer.GetPartitionPropertiesAsync(partitionId).ConfigureAwait(false);
            sequenceNumbers[partitionId] = properties.IsEmpty ? -1 : properties.LastEnqueuedSequenceNumber;
        }

        return sequenceNumbers;
    }

    private static BinaryData Serialize(Reading reading) => BinaryData.FromObjectAsJson(reading);

    private static void Section(string number, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"{number}: {title}");
        Console.WriteLine(new string('-', number.Length + title.Length + 2));
    }

    private sealed record Reading(string Station, int Index, string Run);

    private sealed record RunTotals(
        int Total,
        IReadOnlyDictionary<string, int> CountsByPartition,
        IReadOnlyDictionary<string, int> KeylessByPartition,
        IReadOnlyDictionary<string, HashSet<string>> StationsByPartition);
}
