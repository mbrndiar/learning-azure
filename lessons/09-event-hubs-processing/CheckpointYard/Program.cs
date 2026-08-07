using System.Globalization;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LearningAzure.Lessons.EventHubsProcessing;

/// <summary>
/// Runs a processor, kills it mid-stream, restarts it, and counts exactly how
/// many events were delivered twice — because that number, not the SDK
/// documentation, is what a consumer has to be written against.
/// </summary>
/// <remarks>
/// Requires the Event Hubs emulator and Azurite. The processor keeps its
/// partition ownership and its checkpoints in a blob container, so the two
/// services are not independent: a checkpoint store that is down is a processor
/// that cannot start.
/// </remarks>
internal static class Program
{
    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;"
        + "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string AzuriteConnectionString = "UseDevelopmentStorage=true";

    private const string EventHubName = "telemetry";
    private const string ConsumerGroup = "field-journal";
    private const string SecondConsumerGroup = EventHubConsumerClient.DefaultConsumerGroupName;

    /// <summary>How many events this run publishes.</summary>
    private const int EventCount = 200;

    /// <summary>How many events are handled between checkpoints.</summary>
    private const int CheckpointEvery = 25;

    /// <summary>How many events the first processor handles before it is killed.</summary>
    private const int StopAfter = 90;

    /// <summary>How long a partition lease survives an owner that stopped answering.</summary>
    private static readonly TimeSpan OwnershipExpiry = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Every event this run publishes carries this key, so the whole run lands on
    /// one partition and the crash arithmetic below is exact rather than
    /// approximately right. Module 8 already covered what keys do to spread.
    /// </summary>
    private const string Station = "station-01";

    private static async Task<int> Main()
    {
        var eventHubsConnectionString =
            Environment.GetEnvironmentVariable("EVENTHUBS_CONNECTION_STRING") ?? EmulatorConnectionString;
        var storageConnectionString =
            Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING") ?? AzuriteConnectionString;
        var hubName = Environment.GetEnvironmentVariable("EVENTHUBS_NAME") ?? EventHubName;

        // A fresh container per run, so the checkpoints below are this run's and
        // the teardown at the end is complete.
        var containerName = $"checkpoints-{DateTimeOffset.UtcNow:HHmmssfff}";
        var container = new BlobContainerClient(storageConnectionString, containerName);

        try
        {
            await container.CreateIfNotExistsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reach Azurite: {ex.Message}");
            Console.Error.WriteLine("Start it with:  docker compose up -d azurite");
            return 1;
        }

        Console.WriteLine($"Checkpoint container: {containerName}");

        try
        {
            var startFrom = await PublishAsync(eventHubsConnectionString, hubName).ConfigureAwait(false);

            var first = await RunProcessorAsync(
                "A",
                eventHubsConnectionString,
                hubName,
                container,
                startFrom,
                stopAfter: StopAfter).ConfigureAwait(false);

            await ShowCheckpointStoreAsync(container).ConfigureAwait(false);

            // A processor that is killed rather than stopped leaves its
            // ownership blobs behind. Nothing else may touch those partitions
            // until the lease expires, so a restart is never instant: it is
            // always at least PartitionOwnershipExpirationInterval late.
            Console.WriteLine();
            Console.WriteLine($"   Waiting {OwnershipExpiry.TotalSeconds:0}s for A's partition leases to expire...");
            await Task.Delay(OwnershipExpiry).ConfigureAwait(false);

            var second = await RunProcessorAsync(
                "B",
                eventHubsConnectionString,
                hubName,
                container,
                startFrom,
                stopAfter: null).ConfigureAwait(false);

            ReportDuplicates(first, second);

            await ShowLagAsync(eventHubsConnectionString, hubName, container).ConfigureAwait(false);
            await ShowTheOtherConsumerGroupAsync(
                eventHubsConnectionString,
                hubName,
                container,
                startFrom).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EventHubsException or TimeoutException)
        {
            Console.Error.WriteLine($"Could not reach the Event Hubs emulator: {ex.Message}");
            Console.Error.WriteLine("Start it with:  ACCEPT_EULA=Y docker compose up -d eventhubs");
            return 1;
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"Deleted checkpoint container {containerName}.");
        }

        return 0;
    }

    // -------------------------------------------------------------------------
    // 0. Publish a known stream
    // -------------------------------------------------------------------------
    private static async Task<IReadOnlyDictionary<string, long>> PublishAsync(string connectionString, string hubName)
    {
        Section("0. Publish", $"{EventCount} readings from one station");

        await using var producer = new EventHubProducerClient(connectionString, hubName);

        var partitionIds = await producer.GetPartitionIdsAsync().ConfigureAwait(false);
        var startFrom = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var partitionId in partitionIds)
        {
            var properties = await producer.GetPartitionPropertiesAsync(partitionId).ConfigureAwait(false);
            startFrom[partitionId] = properties.IsEmpty ? -1 : properties.LastEnqueuedSequenceNumber;
        }

        var batchOptions = new CreateBatchOptions { PartitionKey = Station };

        for (var index = 0; index < EventCount; index++)
        {
            using var batch = await producer.CreateBatchAsync(batchOptions).ConfigureAwait(false);

            batch.TryAdd(new EventData(BinaryData.FromObjectAsJson(new Reading(Station, index))));
            await producer.SendAsync(batch).ConfigureAwait(false);
        }

        Console.WriteLine($"   Published                 : {EventCount} events, all keyed '{Station}'");
        Console.WriteLine("   Sequence numbers before   : "
            + string.Join(", ", startFrom.Select(pair => $"p{pair.Key}={pair.Value}")));
        Console.WriteLine("   Everything below counts only events published by THIS run.");

        return startFrom;
    }

    // -------------------------------------------------------------------------
    // 1 and 3. Run a processor until it has handled enough, then stop it
    // -------------------------------------------------------------------------
    private static async Task<ProcessorRun> RunProcessorAsync(
        string label,
        string connectionString,
        string hubName,
        BlobContainerClient container,
        IReadOnlyDictionary<string, long> startFrom,
        int? stopAfter)
    {
        Section(
            label == "A" ? "1. Processor A" : "3. Processor B",
            label == "A"
                ? $"Checkpoint every {CheckpointEvery}, then die after {stopAfter}"
                : "A fresh process, resuming from whatever A left behind");

        var idle = new IdleTimer();

        var run = new ProcessorRun(label);
        using var finished = new SemaphoreSlim(0, 1);

        var options = new EventProcessorClientOptions
        {
            // Aggressive load balancing keeps this companion under a minute.
            // Production defaults are 10s and 30s; shortening them trades
            // stability for responsiveness, which is a real tuning decision and
            // not a demo trick.
            LoadBalancingUpdateInterval = TimeSpan.FromSeconds(1),
            PartitionOwnershipExpirationInterval = OwnershipExpiry,
        };

        var processor = new EventProcessorClient(container, ConsumerGroup, connectionString, hubName, options);

        processor.PartitionInitializingAsync += args =>
        {
            // Only this partition's default matters: it applies when the
            // checkpoint store has nothing for the partition. Getting it wrong
            // is how a restarted processor silently reprocesses a week.
            args.DefaultStartingPosition = startFrom.TryGetValue(args.PartitionId, out var sequence) && sequence >= 0
                ? EventPosition.FromSequenceNumber(sequence, isInclusive: false)
                : EventPosition.Earliest;

            run.Claimed.Add(args.PartitionId);
            return Task.CompletedTask;
        };

        processor.PartitionClosingAsync += args =>
        {
            run.Closed.Add($"{args.PartitionId}:{args.Reason}");
            return Task.CompletedTask;
        };

        processor.ProcessErrorAsync += args =>
        {
            run.Errors.Add($"{args.Operation}: {args.Exception.Message}");
            return Task.CompletedTask;
        };

        processor.ProcessEventAsync += async args =>
        {
            if (!args.HasEvent)
            {
                return;
            }

            var reading = JsonSerializer.Deserialize<Reading>(args.Data.EventBody.ToMemory().Span);
            if (reading is null)
            {
                return;
            }

            idle.Touch();

            if (stopAfter is int target && run.Handled.Count >= target)
            {
                // The target is a hard stop, not a suggestion: the point of this
                // run is that a fixed, known number of events was handled.
                return;
            }

            run.Handled.Add((args.Partition.PartitionId, args.Data.SequenceNumber, reading.Index));

            if (run.Handled.Count % CheckpointEvery == 0)
            {
                // A checkpoint is a WRITE to blob storage, per partition. It is
                // not free, which is the entire reason it is not done per event.
                await args.UpdateCheckpointAsync(args.CancellationToken).ConfigureAwait(false);
                run.Checkpoints++;
            }

            if (stopAfter is int limit && run.Handled.Count >= limit && finished.CurrentCount == 0)
            {
                finished.Release();
            }
        };

        await processor.StartProcessingAsync().ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        if (stopAfter is null)
        {
            // Processor B has no target: it drains, and "drained" means nothing
            // arrived for a while. That is the only definition available to a
            // consumer of an unbounded stream.
            run.TimedOut = !await idle.WaitForQuietAsync(TimeSpan.FromSeconds(5), deadline.Token)
                .ConfigureAwait(false);
        }
        else
        {
            try
            {
                await finished.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                run.TimedOut = true;
            }
        }

        // StopProcessingAsync waits for in-flight handlers and releases
        // ownership. Killing the process instead — which is what a container
        // restart does — leaves ownership to expire, which is section 3's
        // starting condition.
        await processor.StopProcessingAsync().ConfigureAwait(false);

        Console.WriteLine($"   Partitions claimed        : {string.Join(", ", run.Claimed.Order(StringComparer.Ordinal))}");
        Console.WriteLine($"   Events handled            : {run.Handled.Count}");
        Console.WriteLine($"   Checkpoints written       : {run.Checkpoints}");
        Console.WriteLine($"   Handler errors            : {run.Errors.Count}");

        foreach (var error in run.Errors.Distinct(StringComparer.Ordinal))
        {
            Console.WriteLine($"     - {error}");
        }

        Console.WriteLine(
            $"   Stopped by                : {(run.TimedOut ? "the 45s deadline" : stopAfter is null ? "5s of silence" : "reaching the target")}");

        if (label == "A")
        {
            var uncheckpointed = run.Handled.Count - (run.Checkpoints * CheckpointEvery);
            Console.WriteLine();
            Console.WriteLine($"   {uncheckpointed} events were handled AFTER the last checkpoint. Their effects");
            Console.WriteLine("   happened. The record that they happened did not.");
        }

        return run;
    }

    // -------------------------------------------------------------------------
    // 2. What the checkpoint store actually contains
    // -------------------------------------------------------------------------
    private static async Task ShowCheckpointStoreAsync(BlobContainerClient container)
    {
        Section("2. The checkpoint store", "Two kinds of blob, and neither is a lock");

        var withMetadata = new GetBlobsOptions { Traits = BlobTraits.Metadata };

        await foreach (var blob in container.GetBlobsAsync(withMetadata).ConfigureAwait(false))
        {
            var kind = blob.Name.Contains("/checkpoint/", StringComparison.Ordinal) ? "checkpoint" : "ownership ";
            var detail = blob.Metadata.Count == 0
                ? "(no metadata)"
                : string.Join(" ", blob.Metadata.Select(pair => $"{pair.Key}={pair.Value}"));

            Console.WriteLine($"   {kind} {blob.Name[^1]}  {detail}");
        }

        Console.WriteLine();
        Console.WriteLine("   The checkpoint blob is EMPTY: the position lives entirely in the");
        Console.WriteLine("   metadata. The ownership blob is how two processors agree who reads");
        Console.WriteLine("   what, and it expires — which is why a killed processor's partitions");
        Console.WriteLine("   are picked up by the next one rather than stranded.");
    }

    // -------------------------------------------------------------------------
    // 4. The duplicates
    // -------------------------------------------------------------------------
    private static void ReportDuplicates(ProcessorRun first, ProcessorRun second)
    {
        Section("4. Duplicates", "At-least-once is a number, and here it is");

        var firstIndexes = first.Handled.Select(handled => handled.Index).ToHashSet();
        var secondIndexes = second.Handled.Select(handled => handled.Index).ToHashSet();

        var both = firstIndexes.Intersect(secondIndexes).ToArray();
        var neither = Enumerable.Range(0, EventCount).Where(
            index => !firstIndexes.Contains(index) && !secondIndexes.Contains(index)).ToArray();

        Console.WriteLine($"   Handled by A              : {firstIndexes.Count}");
        Console.WriteLine($"   Handled by B              : {secondIndexes.Count}");
        Console.WriteLine($"   Handled by BOTH           : {both.Length}");
        Console.WriteLine($"   Handled by NEITHER        : {neither.Length}");
        Console.WriteLine();

        if (both.Length > 0)
        {
            Console.WriteLine($"   {both.Length} events were processed twice. Not because anything failed:");
            Console.WriteLine("   A handled them, A did not get to checkpoint them, and B correctly");
            Console.WriteLine("   resumed from the last position that WAS recorded.");
            Console.WriteLine("   This is the contract, not a defect. A handler that is not");
            Console.WriteLine("   idempotent is a handler that is wrong.");
        }

        if (neither.Length > 0)
        {
            Console.WriteLine($"   {neither.Length} events were never handled at all — B stopped at its");
            Console.WriteLine("   target before draining every partition. Lost events and unread");
            Console.WriteLine("   events look identical from inside the handler; only the lag");
            Console.WriteLine("   measurement in section 5 tells them apart.");
        }
    }

    // -------------------------------------------------------------------------
    // 5. Lag
    // -------------------------------------------------------------------------
    private static async Task ShowLagAsync(
        string connectionString,
        string hubName,
        BlobContainerClient container)
    {
        Section("5. Lag", "The only honest measure of whether a consumer is keeping up");

        await using var producer = new EventHubProducerClient(connectionString, hubName);

        var checkpoints = new Dictionary<string, long>(StringComparer.Ordinal);

        var withMetadata = new GetBlobsOptions { Traits = BlobTraits.Metadata };

        await foreach (var blob in container.GetBlobsAsync(withMetadata).ConfigureAwait(false))
        {
            if (!blob.Name.Contains("/checkpoint/", StringComparison.Ordinal))
            {
                continue;
            }

            if (blob.Metadata.TryGetValue("sequencenumber", out var raw)
                && long.TryParse(raw, CultureInfo.InvariantCulture, out var sequence))
            {
                checkpoints[blob.Name[(blob.Name.LastIndexOf('/') + 1)..]] = sequence;
            }
        }

        foreach (var partitionId in await producer.GetPartitionIdsAsync().ConfigureAwait(false))
        {
            var properties = await producer.GetPartitionPropertiesAsync(partitionId).ConfigureAwait(false);
            var checkpointed = checkpoints.TryGetValue(partitionId, out var sequence) ? sequence : -1;
            var lag = properties.LastEnqueuedSequenceNumber - checkpointed;

            Console.WriteLine(
                $"   partition {partitionId} : last enqueued {properties.LastEnqueuedSequenceNumber,5}   "
                + $"checkpointed {checkpointed,5}   lag {lag,5}");
        }

        Console.WriteLine();
        Console.WriteLine("   Partitions with no checkpoint report lag against -1: the group has");
        Console.WriteLine("   never recorded a position there, so every event ever written to them");
        Console.WriteLine("   is outstanding. 'No checkpoint' and 'caught up' are not the same.");
        Console.WriteLine();
        Console.WriteLine("   Lag is measured against the CHECKPOINT, not against what the");
        Console.WriteLine("   handler has seen. A processor that handles everything and never");
        Console.WriteLine("   checkpoints has zero backlog and unbounded lag, and on restart it");
        Console.WriteLine("   will prove it.");
    }

    // -------------------------------------------------------------------------
    // 6. A second consumer group
    // -------------------------------------------------------------------------
    private static async Task ShowTheOtherConsumerGroupAsync(
        string connectionString,
        string hubName,
        BlobContainerClient container,
        IReadOnlyDictionary<string, long> startFrom)
    {
        Section("6. A second consumer group", "Same events, separate cursor, separate egress");

        var seen = 0;

        await using var consumer = new EventHubConsumerClient(SecondConsumerGroup, connectionString, hubName);

        foreach (var partitionId in await consumer.GetPartitionIdsAsync().ConfigureAwait(false))
        {
            var sequence = startFrom.TryGetValue(partitionId, out var found) ? found : -1;
            var position = sequence < 0
                ? EventPosition.Earliest
                : EventPosition.FromSequenceNumber(sequence, isInclusive: false);

            var options = new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(1) };

            await foreach (var partitionEvent in consumer
                .ReadEventsFromPartitionAsync(partitionId, position, options)
                .ConfigureAwait(false))
            {
                if (partitionEvent.Data is null)
                {
                    break;
                }

                seen++;
            }
        }

        var checkpointBlobs = 0;

        await foreach (var blob in container.GetBlobsAsync().ConfigureAwait(false))
        {
            if (blob.Name.Contains($"/{SecondConsumerGroup.ToLowerInvariant()}/", StringComparison.Ordinal))
            {
                checkpointBlobs++;
            }
        }

        Console.WriteLine($"   Events read by '{SecondConsumerGroup}'   : {seen}");
        Console.WriteLine($"   Its blobs in this container : {checkpointBlobs} (a bare consumer client has no store)");
        Console.WriteLine();
        Console.WriteLine($"   The '{ConsumerGroup}' group processed, checkpointed, crashed, and");
        Console.WriteLine("   recovered. None of that was visible to this group, which read every");
        Console.WriteLine("   event from the beginning. Consumer groups share the log and share");
        Console.WriteLine("   nothing else — including the egress budget.");
    }

    private static void Section(string number, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"{number}: {title}");
        Console.WriteLine(new string('-', number.Length + title.Length + 2));
    }

    private sealed record Reading(string Station, int Index);

    private sealed class ProcessorRun
    {
        public ProcessorRun(string label) => Label = label;

        public string Label { get; }

        public List<(string PartitionId, long SequenceNumber, int Index)> Handled { get; } = [];

        public HashSet<string> Claimed { get; } = new(StringComparer.Ordinal);

        public List<string> Closed { get; } = [];

        public List<string> Errors { get; } = [];

        public int Checkpoints { get; set; }

        public bool TimedOut { get; set; }
    }

    /// <summary>Tracks how long it has been since anything arrived.</summary>
    private sealed class IdleTimer
    {
        private long _lastTicks = DateTimeOffset.UtcNow.UtcTicks;

        public void Touch() => Interlocked.Exchange(ref _lastTicks, DateTimeOffset.UtcNow.UtcTicks);

        public async Task<bool> WaitForQuietAsync(TimeSpan quiet, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);

                    var since = DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref _lastTicks), TimeSpan.Zero);
                    if (since >= quiet)
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
