using System.Globalization;
using LearningAzure.Capstones.CloudExpeditionJournal;
using Microsoft.Azure.Cosmos;

// Cloud Expedition Journal — end-to-end run.
//
// Default target is the local emulators, so the whole pipeline can be observed
// without an Azure subscription and without a cost. The run is deliberately
// noisy: it publishes a duplicate reading, replays a partition that is already
// checkpointed, injects a malformed work order, fails one effect until its
// delivery budget is spent, projects the same entry twice, and then tears
// everything down and checks that the teardown is complete.
//
//   docker compose up -d azurite eventhubs cosmos
//   source capstones/cloud-expedition-journal/emulator.env
//   dotnet run --project capstones/cloud-expedition-journal/solution
//
// A live run is opt-in, authenticates with Microsoft Entra ID, and creates
// billable resources. See capstones/cloud-expedition-journal/README.md.

var variables = Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);

var environment = string.Equals(
    Environment.GetEnvironmentVariable("EXPEDITION_ENVIRONMENT"),
    "live",
    StringComparison.OrdinalIgnoreCase)
    ? ExpeditionEnvironment.LiveAzure
    : ExpeditionEnvironment.Emulator;

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Ctrl+C asks each stage to stop between items. Nothing is quarantined and
    // nothing is checkpointed mid-batch, so the next run resumes from the last
    // position that was fully handled.
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

var cancellationToken = lifetime.Token;
ExpeditionClients clients;
try
{
    clients = ExpeditionEnvironmentFactory.Create(environment, variables);
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

await using var owned = clients;

var stations = new[] { "ridge-camp", "delta-camp" };
Console.WriteLine($"Cloud Expedition Journal — {environment}");
Console.WriteLine(new string('=', 68));

await clients.Journal.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
await clients.Work.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
await clients.Poison.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
await clients.Stations.CreateIfNotExistsAsync(cancellationToken);

var database = await clients.Cosmos.CreateDatabaseIfNotExistsAsync(
    ExpeditionEnvironmentFactory.DatabaseName,
    cancellationToken: cancellationToken);
var container = await database.Database.CreateContainerIfNotExistsAsync(
    new ContainerProperties(
        ExpeditionEnvironmentFactory.ContainerName,
        CosmosJournalProjection.PartitionKeyPath),
    throughput: 400,
    cancellationToken: cancellationToken);

var clock = TimeProvider.System;
var stream = new EventHubsTelemetryFeed(clients.Producer, clients.Consumer, TimeSpan.FromSeconds(5));
var vault = new BlobArtifactVault(clients.Journal);
var checkpoints = new BlobCheckpointVault(clients.Journal, clock, TimeSpan.FromSeconds(30));
var queue = new QueueWorkDispatch(clients.Work, clients.Poison);
var registry = new TableStationRegistry(clients.Stations);
var projection = new CosmosJournalProjection(container.Container);

var ingress = new TelemetryIngress(stream, maxEventsPerBatch: 4);
var intake = new ReportIntake(vault, clock);
var dispatcher = new WorkDispatcher(queue);
var ledger = new StationLedger(registry, clock);
var worker = new ArtifactWorker(queue, ledger, maxDeliveryCount: 2);
var projector = new JournalProjector(projection);
var cleanup = new ExpeditionCleanup(vault, checkpoints, registry, queue, projection);

Console.WriteLine();
Console.WriteLine("1. Ingress — readings are batched by partition key; the last one is a duplicate");
var observed = new DateTimeOffset(2024, 3, 14, 9, 0, 0, TimeSpan.Zero);
var readings = new List<TelemetryReading>
{
    new("ridge-camp", "obs-0001", -14.5, observed),
    new("ridge-camp", "obs-0002", -13.25, observed.AddMinutes(5)),
    new("delta-camp", "obs-0001", -8.75, observed.AddMinutes(7)),
    new("ridge-camp", "obs-0001", -14.5, observed),
};

foreach (var batch in ingress.Plan(readings))
{
    Console.WriteLine($"   batch key={batch.PartitionKey,-12} readings={batch.Readings.Count}");
}

var receipt = await ingress.PublishAsync(readings, cancellationToken);
Console.WriteLine($"   published {receipt.ReadingCount} readings in {receipt.BatchCount} batches");

Console.WriteLine();
Console.WriteLine("2. Processing — partitions are claimed, read in order, then checkpointed");
var handled = new List<(StreamEvent Event, IntakeResult Intake)>();

async Task<bool> HandleAsync(StreamEvent streamEvent, CancellationToken token)
{
    var result = await intake.PreserveAsync(streamEvent.Reading, token);
    handled.Add((streamEvent, result));

    if (result.Outcome == IntakeOutcome.Stored)
    {
        await dispatcher.DispatchAsync(result, WorkOperations.Summarize, token);
    }

    return true;
}

var processor = new TelemetryProcessor(stream, checkpoints, ownerId: "host-a", checkpointEvery: 2);
var first = await processor.RunAsync(HandleAsync, cancellationToken);
Console.WriteLine(
    $"   owned {first.PartitionsOwned}, read {first.EventsRead}, handled {first.EventsHandled}, "
    + $"replays skipped {first.ReplaysSkipped}, checkpoints {first.CheckpointsWritten}");

var stored = handled.Count(item => item.Intake.Outcome == IntakeOutcome.Stored);
Console.WriteLine($"   reports stored {stored}, duplicates absorbed {handled.Count - stored}");

Console.WriteLine();
Console.WriteLine("3. Replay — a second pass resumes from the checkpoint instead of re-reading");
var replay = await processor.RunAsync(HandleAsync, cancellationToken);
Console.WriteLine(
    $"   owned {replay.PartitionsOwned}, read {replay.EventsRead}, handled {replay.EventsHandled}, "
    + $"replays skipped {replay.ReplaysSkipped}");

Console.WriteLine();
Console.WriteLine("4. Work — one malformed order, and one effect that fails until its budget is spent");

// A hand-written message the dispatcher would never send. It is on the queue to
// prove the worker quarantines it on its first delivery instead of retrying a
// failure that is deterministic.
await clients.Work.SendMessageAsync("""{"workOrderId":"","operation":"summarize"}""", cancellationToken);

var summarized = new List<string>();
Task Effect(ArtifactWorkOrder order, CancellationToken token)
{
    token.ThrowIfCancellationRequested();

    if (order.ObservationId == "obs-0002")
    {
        throw new InvalidOperationException("Summary tool exited non-zero.");
    }

    summarized.Add(order.WorkOrderId);
    return Task.CompletedTask;
}

// Five seconds is long enough that one drain pass cannot outlive it and short
// enough to watch. Production values are minutes: the timeout must exceed how
// long the effect actually takes, or the work is handed to a second consumer
// while the first is still running it.
var visibility = TimeSpan.FromSeconds(5);
var pass1 = await worker.DrainAsync(Effect, maxBatches: 8, visibility, cancellationToken);
Console.WriteLine(
    $"   pass 1: received {pass1.Received}, completed {pass1.Completed}, retried {pass1.Retried}, "
    + $"quarantined {pass1.Quarantined}");

await Task.Delay(visibility + TimeSpan.FromMilliseconds(500), cancellationToken);
var pass2 = await worker.DrainAsync(Effect, maxBatches: 8, visibility, cancellationToken);
Console.WriteLine(
    $"   pass 2: received {pass2.Received}, completed {pass2.Completed}, retried {pass2.Retried}, "
    + $"quarantined {pass2.Quarantined}");

foreach (var poison in worker.Quarantined)
{
    Console.WriteLine($"   poison: delivery {poison.DeliveryCount} — {poison.Reason}");
}

Console.WriteLine();
Console.WriteLine("5. Projection — the journal converges, and re-running it changes nothing");

async Task<(int Written, int Superseded, double Charge)> ProjectAllAsync()
{
    var written = 0;
    var superseded = 0;
    var charge = 0.0;

    foreach (var (streamEvent, result) in handled)
    {
        var entry = new JournalEntry(
            ExpeditionNaming.JournalItemId(streamEvent.Reading.Key),
            streamEvent.Reading.StationId,
            streamEvent.Reading.ObservationId,
            streamEvent.PartitionId,
            streamEvent.SequenceNumber,
            streamEvent.Reading.Celsius,
            result.ArtifactName,
            streamEvent.Reading.ObservedUtc);

        var report = await projector.ProjectAsync(entry, Task.Delay, cancellationToken);
        written += report.Written;
        superseded += report.Superseded;
        charge += report.RequestCharge;
    }

    return (written, superseded, charge);
}

var projected = await ProjectAllAsync();
Console.WriteLine(
    $"   pass 1: written {projected.Written}, superseded {projected.Superseded}, "
    + $"request units {projected.Charge.ToString("F2", CultureInfo.InvariantCulture)}");

// The same work again, as a crashed run would repeat it. Nothing is written:
// every entry is already at or past the stream position being offered.
var reprojected = await ProjectAllAsync();
Console.WriteLine(
    $"   pass 2: written {reprojected.Written}, superseded {reprojected.Superseded}, "
    + $"request units {reprojected.Charge.ToString("F2", CultureInfo.InvariantCulture)}");

Console.WriteLine();
Console.WriteLine("6. Query — a single-partition read, paged to the continuation token's end");
foreach (var stationId in stations)
{
    var (entries, readCharge, pages) = await projector.ReadStationAsync(stationId, 1, Task.Delay, cancellationToken);
    Console.WriteLine(
        $"   {stationId,-12} entries {entries.Count} over {pages} pages, "
        + $"{readCharge.ToString("F2", CultureInfo.InvariantCulture)} RU");

    foreach (var entry in entries)
    {
        Console.WriteLine(
            $"      {entry.Id,-12} seq={entry.SequenceNumber.ToString(CultureInfo.InvariantCulture),-4} "
            + $"{entry.Celsius.ToString("F2", CultureInfo.InvariantCulture)}C {entry.ArtifactName}");
    }
}

Console.WriteLine();
Console.WriteLine("7. Teardown — everything this run created is removed");
var teardown = await cleanup.RemoveAsync(stations, pageSize: 10, cancellationToken);
Console.WriteLine(
    $"   reports {teardown.ReportsDeleted}, checkpoints {teardown.CheckpointsDeleted}, "
    + $"station rows {teardown.StationRowsDeleted}, journal entries {teardown.JournalEntriesDeleted}, "
    + $"messages remaining {teardown.MessagesRemaining}");

if (!teardown.IsComplete)
{
    Console.Error.WriteLine("   teardown left messages behind; drain the queue before you stop.");
    return 1;
}

// The container, queues, table, and Cosmos database are the run's own resources.
// A live run must remove them too, or the next `az group delete` is the only
// thing that ever will. The event hub is not deleted: it is seeded configuration
// on the emulator, and a live run removes it with the resource group.
await clients.Work.DeleteIfExistsAsync(cancellationToken);
await clients.Poison.DeleteIfExistsAsync(cancellationToken);
await clients.Stations.DeleteAsync(cancellationToken);
await clients.Journal.DeleteIfExistsAsync(cancellationToken: cancellationToken);
await database.Database.DeleteAsync(cancellationToken: cancellationToken);
Console.WriteLine("   container, queues, table, and Cosmos database deleted");

return 0;
