namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>One fully wired in-memory field station.</summary>
/// <remarks>
/// The whole pipeline is composed from the ports, so a suite can drive intake,
/// dispatch, the worker, the ledger, and cleanup without a service, a container,
/// or a clock. Every collaborator is exposed so a test can inspect what actually
/// happened rather than only what was returned.
/// </remarks>
internal sealed class Pipeline
{
    /// <summary>Creates a pipeline that quarantines after three deliveries.</summary>
    public Pipeline() : this(maxDequeueCount: 3)
    {
    }

    /// <summary>Creates a pipeline with an explicit delivery budget.</summary>
    /// <param name="maxDequeueCount">Deliveries allowed before quarantine.</param>
    public Pipeline(int maxDequeueCount)
    {
        Projector = new StationStatusProjector(Index, Clock);
        Intake = new ArtifactIntake(Store);
        Dispatcher = new WorkDispatcher(Backlog);
        Worker = new StationWorker(Backlog, Projector, maxDequeueCount);
        Cleanup = new FieldStationCleanup(Store, Index, Backlog);
    }

    /// <summary>The artifact store behind the pipeline.</summary>
    public InMemoryArtifactStore Store { get; } = new();

    /// <summary>The queue behind the pipeline.</summary>
    public InMemoryBacklog Backlog { get; } = new();

    /// <summary>The status index behind the pipeline.</summary>
    public InMemoryStatusIndex Index { get; } = new();

    /// <summary>The effect the worker applies.</summary>
    public RecordingEffect Effect { get; } = new();

    /// <summary>The clock every row is stamped with.</summary>
    public ManualClock Clock { get; } = new(Fixture.Start);

    /// <summary>The intake stage.</summary>
    public ArtifactIntake Intake { get; }

    /// <summary>The dispatch stage.</summary>
    public WorkDispatcher Dispatcher { get; }

    /// <summary>The ledger.</summary>
    public StationStatusProjector Projector { get; }

    /// <summary>The consumer.</summary>
    public StationWorker Worker { get; }

    /// <summary>The teardown pass.</summary>
    public FieldStationCleanup Cleanup { get; }

    /// <summary>A second worker over the same state, as a restarted host would be.</summary>
    /// <param name="maxDequeueCount">Deliveries allowed before quarantine.</param>
    /// <returns>A fresh worker sharing this pipeline's queue and ledger.</returns>
    public StationWorker Restart(int maxDequeueCount = 3) =>
        new(Backlog, new StationStatusProjector(Index, Clock), maxDequeueCount);

    /// <summary>Drains the backlog with the recording effect.</summary>
    /// <param name="maxBatches">Receive rounds allowed.</param>
    /// <returns>What the pass did.</returns>
    public Task<DrainReport> DrainAsync(int maxBatches = 4) => Worker.DrainAsync(
        Effect.ApplyAsync,
        maxBatches,
        TimeSpan.FromSeconds(30),
        TestContext.Current.CancellationToken);

    /// <summary>The status row for one observation, or <c>null</c>.</summary>
    /// <param name="observation">The observation id.</param>
    /// <returns>The stored row.</returns>
    public Task<StationStatus?> RowAsync(string observation = Fixture.Observation) =>
        Index.TryGetAsync(Fixture.Station, observation, TestContext.Current.CancellationToken);

    /// <summary>The station's summary row, or <c>null</c>.</summary>
    /// <returns>The stored summary row.</returns>
    public Task<StationStatus?> SummaryAsync() =>
        Index.TryGetAsync(Fixture.Station, StationNaming.SummaryRowKey, TestContext.Current.CancellationToken);
}
