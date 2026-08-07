namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>One fully wired in-memory expedition.</summary>
/// <remarks>
/// The whole journal is composed from the ports, so a suite can drive ingress,
/// processing, intake, dispatch, the worker, the projector, and teardown without
/// a broker, a container, an account, or a clock. Every collaborator is exposed
/// so a test can inspect what actually happened rather than only what was
/// returned.
/// </remarks>
internal sealed class Journal
{
    /// <summary>Creates a journal that quarantines after two deliveries.</summary>
    public Journal() : this(maxDeliveryCount: 2)
    {
    }

    /// <summary>Creates a journal with an explicit delivery budget.</summary>
    /// <param name="maxDeliveryCount">Deliveries allowed before quarantine.</param>
    /// <param name="checkpointEvery">Events handled between checkpoints.</param>
    public Journal(int maxDeliveryCount, int checkpointEvery = 2)
    {
        Checkpoints = new InMemoryCheckpointStore(Clock, LeaseDuration);
        Ingress = new TelemetryIngress(Feed, maxEventsPerBatch: 2);
        Intake = new ReportIntake(Vault, Clock);
        Dispatcher = new WorkDispatcher(Backlog);
        Ledger = new StationLedger(Registry, Clock);
        Worker = new ArtifactWorker(Backlog, Ledger, maxDeliveryCount);
        Processor = new TelemetryProcessor(Feed, Checkpoints, OwnerId, checkpointEvery);
        Projector = new JournalProjector(Projection);
        Cleanup = new ExpeditionCleanup(Vault, Checkpoints, Registry, Backlog, Projection);
    }

    /// <summary>How long a partition claim survives a silent owner.</summary>
    public static TimeSpan LeaseDuration { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The identity this journal's processor claims partitions with.</summary>
    public const string OwnerId = "host-a";

    /// <summary>The telemetry feed behind the journal.</summary>
    public InMemoryFeed Feed { get; } = new();

    /// <summary>The artifact vault behind the journal.</summary>
    public InMemoryVault Vault { get; } = new();

    /// <summary>The checkpoint store behind the journal.</summary>
    public InMemoryCheckpointStore Checkpoints { get; }

    /// <summary>The queue behind the journal.</summary>
    public InMemoryBacklog Backlog { get; } = new();

    /// <summary>The station registry behind the journal.</summary>
    public InMemoryRegistry Registry { get; } = new();

    /// <summary>The Cosmos-shaped projection behind the journal.</summary>
    public InMemoryProjection Projection { get; } = new();

    /// <summary>The effect the worker applies.</summary>
    public RecordingEffect Effect { get; } = new();

    /// <summary>The clock every row and lease is stamped with.</summary>
    public ManualClock Clock { get; } = new(Fixture.Start);

    /// <summary>The publish stage.</summary>
    public TelemetryIngress Ingress { get; }

    /// <summary>The intake stage.</summary>
    public ReportIntake Intake { get; }

    /// <summary>The dispatch stage.</summary>
    public WorkDispatcher Dispatcher { get; }

    /// <summary>The ledger.</summary>
    public StationLedger Ledger { get; }

    /// <summary>The queue consumer.</summary>
    public ArtifactWorker Worker { get; }

    /// <summary>The stream consumer.</summary>
    public TelemetryProcessor Processor { get; }

    /// <summary>The journal projector.</summary>
    public JournalProjector Projector { get; }

    /// <summary>The teardown pass.</summary>
    public ExpeditionCleanup Cleanup { get; }

    /// <summary>Events the processor handled, in the order it handled them.</summary>
    public List<StreamEvent> Handled { get; } = [];

    /// <summary>A second processor over the same state, as a restarted host would be.</summary>
    /// <param name="ownerId">The identity the replacement claims partitions with.</param>
    /// <param name="checkpointEvery">Events handled between checkpoints.</param>
    /// <returns>A fresh processor sharing this journal's feed and checkpoints.</returns>
    public TelemetryProcessor Restart(string ownerId = OwnerId, int checkpointEvery = 2) =>
        new(Feed, Checkpoints, ownerId, checkpointEvery);

    /// <summary>Publishes readings through the ingress stage.</summary>
    /// <param name="readings">The readings to publish.</param>
    /// <returns>What was sent.</returns>
    public Task<PublishReceipt> PublishAsync(params TelemetryReading[] readings) =>
        Ingress.PublishAsync(readings, TestContext.Current.CancellationToken);

    /// <summary>Runs the processor, preserving and dispatching every event it handles.</summary>
    /// <param name="processor">The processor to run, or this journal's own.</param>
    /// <returns>What the pass did.</returns>
    public Task<ProcessorReport> ProcessAsync(TelemetryProcessor? processor = null) =>
        (processor ?? Processor).RunAsync(HandleAsync, TestContext.Current.CancellationToken);

    /// <summary>Drains the backlog with the recording effect.</summary>
    /// <param name="maxBatches">Receive rounds allowed.</param>
    /// <returns>What the pass did.</returns>
    public Task<DrainReport> DrainAsync(int maxBatches = 4) => Worker.DrainAsync(
        Effect.ApplyAsync,
        maxBatches,
        TimeSpan.FromSeconds(30),
        TestContext.Current.CancellationToken);

    /// <summary>Projects every handled event into the journal.</summary>
    /// <returns>What the pass wrote and what it cost.</returns>
    public async Task<ProjectionReport> ProjectHandledAsync()
    {
        int written = 0, superseded = 0, concurrency = 0, throttles = 0;
        var charge = 0.0;

        foreach (var streamEvent in Handled)
        {
            var report = await Projector.ProjectAsync(
                EntryFor(streamEvent),
                delay: null,
                TestContext.Current.CancellationToken);

            written += report.Written;
            superseded += report.Superseded;
            concurrency += report.ConcurrencyRetries;
            throttles += report.ThrottleRetries;
            charge += report.RequestCharge;
        }

        return new ProjectionReport(written, superseded, concurrency, throttles, charge);
    }

    /// <summary>The journal entry one stream event projects to.</summary>
    /// <param name="streamEvent">The handled event.</param>
    /// <returns>The entry.</returns>
    public static JournalEntry EntryFor(StreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var key = streamEvent.Reading.Key;
        return new JournalEntry(
            ExpeditionNaming.JournalItemId(key),
            key.StationId,
            key.ObservationId,
            streamEvent.PartitionId,
            streamEvent.SequenceNumber,
            streamEvent.Reading.Celsius,
            ExpeditionNaming.ArtifactName(key),
            streamEvent.Reading.ObservedUtc);
    }

    /// <summary>The station row for one observation, or <c>null</c>.</summary>
    /// <param name="observation">The observation id.</param>
    /// <returns>The stored row.</returns>
    public Task<StationState?> RowAsync(string observation = Fixture.Observation) =>
        Registry.TryGetAsync(Fixture.Station, observation, TestContext.Current.CancellationToken);

    /// <summary>The station's watermark row, or <c>null</c>.</summary>
    /// <param name="station">The station id.</param>
    /// <returns>The stored watermark row.</returns>
    public Task<StationState?> WatermarkAsync(string station = Fixture.Station) =>
        Registry.TryGetAsync(station, ExpeditionNaming.WatermarkRowKey, TestContext.Current.CancellationToken);

    private async Task HandleAsync(StreamEvent streamEvent, CancellationToken cancellationToken)
    {
        Handled.Add(streamEvent);

        var intake = await Intake.PreserveAsync(streamEvent.Reading, cancellationToken);
        await Dispatcher.DispatchAsync(intake, WorkOperations.Summarize, cancellationToken);
    }
}
