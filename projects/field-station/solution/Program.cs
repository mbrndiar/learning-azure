using System.Globalization;
using System.Text;
using LearningAzure.Projects.FieldStation;

// Field Station — end-to-end run.
//
// Default target is Azurite, so the whole pipeline can be observed without an
// Azure subscription and without a cost. The run is deliberately noisy: it
// ingests a duplicate, injects a malformed message, fails one effect until its
// delivery budget is spent, and then tears everything down and checks that the
// teardown is complete.
//
//   docker compose up -d azurite
//   export AZURITE_CONNECTION_STRING="UseDevelopmentStorage=true"
//   dotnet run --project projects/field-station/solution
//
// A live run is opt-in, authenticates with Microsoft Entra ID, and creates
// billable resources. See projects/field-station/README.md#run-it.

var variables = Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);

var environment = string.Equals(
    Environment.GetEnvironmentVariable("FIELD_STATION_ENVIRONMENT"),
    "live",
    StringComparison.OrdinalIgnoreCase)
    ? StationEnvironment.LiveAzure
    : StationEnvironment.Emulator;

var stationId = Environment.GetEnvironmentVariable("FIELD_STATION_ID") ?? "ridge-camp";
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Ctrl+C asks the drain to stop between messages. Nothing is quarantined and
    // nothing is deleted, so the next run picks the backlog up where this one
    // left it.
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

var cancellationToken = lifetime.Token;
StationClients clients;
try
{
    clients = FieldStationClients.Create(environment, variables);
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

Console.WriteLine($"Field Station — {environment}, station '{stationId}'");
Console.WriteLine(new string('=', 64));

await clients.Artifacts.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
await clients.Work.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
await clients.Poison.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
await clients.Status.CreateIfNotExistsAsync(cancellationToken);

var store = new BlobArtifactStore(clients.Artifacts);
var queue = new QueueStorageBacklog(clients.Work, clients.Poison);
var index = new TableStationIndex(clients.Status);

var intake = new ArtifactIntake(store);
var dispatcher = new WorkDispatcher(queue);
var projector = new StationStatusProjector(index, TimeProvider.System);
var worker = new StationWorker(queue, projector, maxDequeueCount: 2);

Console.WriteLine();
Console.WriteLine("1. Intake — the third upload is a retry of the first");
var observations = new[] { "obs-0001", "obs-0002", "obs-0001" };
var results = new List<IntakeResult>();
foreach (var observation in observations)
{
    var key = new ArtifactKey(stationId, observation);
    var payload = Encoding.UTF8.GetBytes(
        $$"""{"station":"{{stationId}}","observation":"{{observation}}","temperatureC":-14.5}""");

    using var content = new MemoryStream(payload, writable: false);
    var result = await intake.PreserveAsync(key, content, "application/json", cancellationToken);
    results.Add(result);
    Console.WriteLine($"   {observation,-10} {result.Outcome}");
}

Console.WriteLine();
Console.WriteLine("2. Dispatch — a duplicate upload produces no second work order");
var dispatched = await dispatcher.DispatchStoredAsync(results, "checksum", cancellationToken);
Console.WriteLine($"   uploads: {results.Count}, work orders: {dispatched.Count}");

// A hand-written message the producer would never send. It is on the queue to
// prove the worker quarantines it on its first delivery instead of retrying a
// failure that is deterministic.
await clients.Work.SendMessageAsync("""{"workOrderId":"","operation":"checksum"}""", cancellationToken);
Console.WriteLine("   injected 1 malformed message");

Console.WriteLine();
Console.WriteLine("3. Drain — obs-0002 fails until its delivery budget is spent");
var applied = new List<string>();
Task Effect(WorkOrder order, CancellationToken token)
{
    token.ThrowIfCancellationRequested();

    if (order.ObservationId == "obs-0002")
    {
        throw new InvalidOperationException("Checksum tool exited non-zero.");
    }

    applied.Add(order.WorkOrderId);
    return Task.CompletedTask;
}

// Five seconds is long enough that one drain pass cannot outlive it and short
// enough to watch. Production values are minutes:
// the timeout must exceed how long the effect actually takes, or the work is
// handed to a second consumer while the first is still running it.
var visibility = TimeSpan.FromSeconds(5);
var report = await worker.DrainAsync(Effect, maxBatches: 8, visibility, cancellationToken);
Console.WriteLine(
    $"   pass 1: received {report.Received}, completed {report.Completed}, retried {report.Retried}, "
    + $"quarantined {report.Quarantined}, effects applied {report.EffectsApplied}");

// Nothing deleted the failed message, so the queue re-delivers it once the
// visibility timeout lapses. Waiting for that here is what makes the delivery
// budget observable: on its second delivery the message has spent its budget
// and is quarantined instead of retried forever.
await Task.Delay(visibility + TimeSpan.FromMilliseconds(500), cancellationToken);
var second = await worker.DrainAsync(Effect, maxBatches: 8, visibility, cancellationToken);
Console.WriteLine(
    $"   pass 2: received {second.Received}, completed {second.Completed}, retried {second.Retried}, "
    + $"quarantined {second.Quarantined}, effects applied {second.EffectsApplied}");

foreach (var poison in worker.Quarantined)
{
    Console.WriteLine($"   poison: delivery {poison.DequeueCount} — {poison.Reason}");
}

Console.WriteLine();
Console.WriteLine("4. Status index — one point-readable row per observation, plus the summary");
await foreach (var row in index.QueryStationAsync(stationId, cancellationToken))
{
    Console.WriteLine(
        $"   {row.RowKey,-12} {row.State,-12} count={row.ProcessedCount.ToString(CultureInfo.InvariantCulture)}");
}

Console.WriteLine();
Console.WriteLine("5. Cleanup — everything this run created is removed");
var cleanup = new FieldStationCleanup(store, index, queue);
var cleanupReport = await cleanup.RemoveStationAsync(stationId, cancellationToken);
Console.WriteLine(
    $"   artifacts deleted {cleanupReport.ArtifactsDeleted}, status rows deleted {cleanupReport.StatusRowsDeleted}, "
    + $"messages remaining {cleanupReport.MessagesRemaining}");

if (!cleanupReport.IsComplete)
{
    Console.Error.WriteLine("   cleanup left messages behind; drain the queue before you stop.");
    return 1;
}

// The container, queues, and table themselves are the run's own resources. A
// live run must remove them too, or the next `az group delete` is the only thing
// that ever will.
await clients.Work.DeleteIfExistsAsync(cancellationToken);
await clients.Poison.DeleteIfExistsAsync(cancellationToken);
await clients.Status.DeleteAsync(cancellationToken);
await clients.Artifacts.DeleteIfExistsAsync(cancellationToken: cancellationToken);
Console.WriteLine("   container, queues, and table deleted");

return 0;
