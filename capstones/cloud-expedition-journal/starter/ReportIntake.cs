namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>What an intake attempt did to the vault.</summary>
public enum IntakeOutcome
{
    /// <summary>The report was new and the bytes were streamed in.</summary>
    Stored,

    /// <summary>The same observation was already preserved; nothing was written.</summary>
    Duplicate,
}

/// <summary>The result of one intake attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Key">The observation the attempt addressed.</param>
/// <param name="ArtifactName">The derived name the attempt addressed.</param>
/// <param name="ETag">The current version, when the attempt wrote one.</param>
public sealed record IntakeResult(IntakeOutcome Outcome, ObservationKey Key, string ArtifactName, string? ETag);

/// <summary>Preserves incoming readings as durable reports, exactly once.</summary>
/// <remarks>
/// Milestone 2. Intake is the pipeline's first idempotency boundary. A station
/// that retries an upload after a timeout, and a stream that replays after a
/// restart, both arrive here — and neither may create a second report.
/// </remarks>
/// <param name="vault">The vault reports are written to.</param>
/// <param name="clock">The clock the rendered report is stamped with.</param>
public sealed class ReportIntake(IArtifactVault vault, TimeProvider clock)
{
    private readonly IArtifactVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The content type every preserved report records.</summary>
    public const string ReportContentType = "application/json";

    /// <summary>Preserves one reading, or reports that it was already preserved.</summary>
    /// <param name="reading">The reading to preserve.</param>
    /// <param name="cancellationToken">Cancels the intake.</param>
    /// <returns>What happened, and the artifact name it happened to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is <c>null</c>.</exception>
    public Task<IntakeResult> PreserveAsync(TelemetryReading reading, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var key = reading.Key;
        var name = ExpeditionNaming.ArtifactName(key);
        var body = JournalCodec.RenderArtifact(
            new ArtifactWorkOrder(
                ExpeditionNaming.WorkOrderId(key, WorkOperations.Summarize),
                key.StationId,
                key.ObservationId,
                name,
                WorkOperations.Summarize),
            reading,
            _clock.GetUtcNow());

        // GAP 9 — "Only if it is not there yet" is a precondition, not a lookup.
        //
        // TryReadAsync followed by a write is two round trips with a race between
        // them, and the caller most likely to be inside that window is precisely
        // the retrying uploader this is meant to handle. CreateIfAbsentAsync puts
        // If-None-Match: * on the wire and lets the service arbitrate.
        //
        // The stream is deliberately not buffered into a byte[] by the adapter:
        // that is a memory cost proportional to the report, paid on a machine
        // sized for the metadata.
        // Wrap `body` in a MemoryStream and call IArtifactVault.CreateIfAbsentAsync
        // with ReportContentType. Map WriteOutcome.Written to IntakeOutcome.Stored
        // and carry the ETag; map AlreadyExists to IntakeOutcome.Duplicate. A
        // create can never return Stale, so treat that as a defect rather than a
        // silently accepted third case.
        throw new NotImplementedException(
            "GAP 9: preserve one report exactly once. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-2-the-storage-workflow.");
    }
}

/// <summary>The operations a work order may ask for.</summary>
public static class WorkOperations
{
    /// <summary>Derive the station summary from a preserved report.</summary>
    public const string Summarize = "summarize";
}

/// <summary>Turns a durable report into queued work.</summary>
/// <remarks>
/// Milestone 2. Dispatch happens <b>after</b> the report is durable. A message
/// pointing at a blob nobody wrote is a guaranteed consumer failure that the
/// consumer cannot tell apart from a transient read error, so it retries until
/// its budget is gone and then quarantines work that was never wrong.
/// </remarks>
/// <param name="queue">The queue work is dispatched to.</param>
public sealed class WorkDispatcher(IWorkBacklog queue)
{
    private readonly IWorkBacklog _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>Dispatches work for one intake result, when there is work to do.</summary>
    /// <param name="intake">What intake did.</param>
    /// <param name="operation">The operation to request.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><c>true</c> when a work order was sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="intake"/> is <c>null</c>.</exception>
    public Task<bool> DispatchAsync(
        IntakeResult intake,
        string operation,
        CancellationToken cancellationToken)
    {
        // GAP 10 — Only a write that actually happened produces a work order.
        //
        // A duplicate upload must not produce a second message. The consumer is
        // idempotent, so the extra message would not corrupt anything — it would
        // pay for a receive, a claim, and a delete to discover it has nothing to
        // do, on every retry the uploader makes.
        // Send a work order built from ExpeditionNaming.WorkOrderId only when the
        // report was actually stored, and answer whether anything was sent.
        throw new NotImplementedException(
            "GAP 10: dispatch work only for a report this call wrote. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-2-the-storage-workflow.");
    }
}
