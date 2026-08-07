using System.Net;
using System.Text;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Projects.FieldStation.Tests;

/// <summary>
/// Milestone 2 — preserving artifacts. Judges the intake's idempotency and its
/// conditional amendment, and the Blob adapter that puts the preconditions on the
/// wire.
/// </summary>
[Trait("Milestone", "artifact-storage")]
public sealed class ArtifactStorageTests
{
    private static readonly string ArtifactName = StationNaming.ArtifactName(Fixture.Key);

    [Fact]
    public async Task AFirstUploadIsStored()
    {
        var store = new InMemoryArtifactStore();
        var intake = new ArtifactIntake(store);

        using var content = Fixture.Content("""{"temperatureC":-14.5}""");
        var result = await intake.PreserveAsync(
            Fixture.Key, content, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal(IntakeOutcome.Stored, result.Outcome);
        Assert.Equal(ArtifactName, result.ArtifactName);
        Assert.NotNull(result.ETag);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task ARepeatedUploadOfTheSameObservationIsADuplicateRatherThanASecondArtifact()
    {
        // This is the retrying field laptop. It must not double the expedition's
        // storage bill or its observation count.
        var store = new InMemoryArtifactStore();
        var intake = new ArtifactIntake(store);

        using var first = Fixture.Content("""{"temperatureC":-14.5}""");
        using var second = Fixture.Content("""{"temperatureC":-14.5}""");
        await intake.PreserveAsync(Fixture.Key, first, "application/json", TestContext.Current.CancellationToken);
        var result = await intake.PreserveAsync(
            Fixture.Key, second, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal(IntakeOutcome.Duplicate, result.Outcome);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task ADuplicateUploadDoesNotOverwriteTheOriginalBytes()
    {
        // "Already there" must mean the first artifact survives. An intake that
        // overwrites on a retry loses the original observation the moment the
        // retry carries a truncated body.
        var store = new InMemoryArtifactStore();
        var intake = new ArtifactIntake(store);

        using var original = Fixture.Content("""{"temperatureC":-14.5}""");
        using var truncated = Fixture.Content("{");
        await intake.PreserveAsync(Fixture.Key, original, "application/json", TestContext.Current.CancellationToken);
        await intake.PreserveAsync(Fixture.Key, truncated, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal("""{"temperatureC":-14.5}""", Encoding.UTF8.GetString(store[ArtifactName]));
    }

    [Fact]
    public async Task AnAmendmentUnderTheCurrentVersionIsApplied()
    {
        var store = new InMemoryArtifactStore();
        var intake = new ArtifactIntake(store);
        store.Seed(ArtifactName, """{"temperatureC":-14.5}""");

        var revision = await intake.ReadAsync(Fixture.Key, TestContext.Current.CancellationToken);
        using var amended = Fixture.Content("""{"temperatureC":-14.7}""");
        var result = await intake.AmendAsync(
            Fixture.Key, amended, "application/json", revision!.ETag, TestContext.Current.CancellationToken);

        Assert.Equal(IntakeOutcome.Amended, result.Outcome);
        Assert.Equal("""{"temperatureC":-14.7}""", Encoding.UTF8.GetString(store[ArtifactName]));
    }

    [Fact]
    public async Task AnAmendmentThatLostTheRaceIsAConflictRatherThanASilentOverwrite()
    {
        // The competing writer lands between the read and the write, which is
        // exactly the window a precondition exists to close.
        var store = new InMemoryArtifactStore();
        var intake = new ArtifactIntake(store);
        store.Seed(ArtifactName, """{"temperatureC":-14.5}""");
        var revision = await intake.ReadAsync(Fixture.Key, TestContext.Current.CancellationToken);
        store.BeforeReplace = name => store.StealRace(name);

        using var amended = Fixture.Content("""{"temperatureC":-14.7}""");
        var result = await intake.AmendAsync(
            Fixture.Key, amended, "application/json", revision!.ETag, TestContext.Current.CancellationToken);

        Assert.Equal(IntakeOutcome.Conflict, result.Outcome);
        Assert.Equal("""{"temperatureC":-14.5}""", Encoding.UTF8.GetString(store[ArtifactName]));
    }

    [Fact]
    public async Task IntakeHonoursCancellationBeforeItWrites()
    {
        var store = new InMemoryArtifactStore();
        var intake = new ArtifactIntake(store);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var content = Fixture.Content("{}");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => intake.PreserveAsync(Fixture.Key, content, "application/json", cancelled.Token));

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task TheAdapterSendsIfNoneMatchOnACreate()
    {
        // The one header that turns "create only if absent" from a race into a
        // service decision.
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        using var content = Fixture.Content("{}");
        await store.CreateIfAbsentAsync(ArtifactName, content, "application/json", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("*", request.Header("If-None-Match"));
        Assert.Null(request.Header("If-Match"));
    }

    [Fact]
    public async Task TheAdapterReportsALostCreateRatherThanThrowing()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Conflict());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        using var content = Fixture.Content("{}");
        var result = await store.CreateIfAbsentAsync(
            ArtifactName, content, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal(WriteOutcome.AlreadyExists, result.Outcome);
    }

    [Fact]
    public async Task TheAdapterTreatsA412OnACreateAsALostCreateToo()
    {
        // Storage answers a failed If-None-Match with 409 or 412 depending on the
        // path taken. Both mean "somebody got there first".
        var handler = new ScriptedHandler(_ => StorageResponses.PreconditionFailed());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        using var content = Fixture.Content("{}");
        var result = await store.CreateIfAbsentAsync(
            ArtifactName, content, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal(WriteOutcome.AlreadyExists, result.Outcome);
    }

    [Fact]
    public async Task TheEtagFromAReadIsUsableAsAnIfMatchWithoutEditing()
    {
        // The SDK's ETag.ToString() drops the quotes and the service rejects an
        // unquoted If-Match, so a round trip that looks right in memory fails
        // only against a real service.
        var handler = new ScriptedHandler(
            _ => ScriptedClients.Download("""{"temperatureC":-14.5}""", "\"0x1\""),
            _ => StorageResponses.Created());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        var revision = await store.TryReadAsync(ArtifactName, TestContext.Current.CancellationToken);
        using var content = Fixture.Content("{}");
        await store.ReplaceIfUnchangedAsync(
            ArtifactName, content, "application/json", revision!.ETag, TestContext.Current.CancellationToken);

        Assert.Equal("\"0x1\"", revision.ETag);
        Assert.Equal("\"0x1\"", handler.Requests[1].Header("If-Match"));
        Assert.Null(handler.Requests[1].Header("If-None-Match"));
    }

    [Fact]
    public async Task AStaleAmendmentIsReportedRatherThanThrown()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.PreconditionFailed());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        using var content = Fixture.Content("{}");
        var result = await store.ReplaceIfUnchangedAsync(
            ArtifactName, content, "application/json", "\"0x1\"", TestContext.Current.CancellationToken);

        Assert.Equal(WriteOutcome.Stale, result.Outcome);
    }

    [Fact]
    public async Task AMissingArtifactReadsAsNullAndAForbiddenOneStillThrows()
    {
        // Catching by exception type rather than status turns a missing role
        // assignment into "there is no data", which is a silent, wrong answer.
        var missing = new ScriptedHandler(_ => StorageResponses.NotFound());
        Assert.Null(await new BlobArtifactStore(ScriptedClients.Container(missing))
            .TryReadAsync(ArtifactName, TestContext.Current.CancellationToken));

        var forbidden = ScriptedHandler.Always(
            _ => StorageResponses.Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch", "Forbidden."));
        var error = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => new BlobArtifactStore(ScriptedClients.Container(forbidden))
                .TryReadAsync(ArtifactName, TestContext.Current.CancellationToken));

        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task AThrottledWriteIsRetriedWithinTheClientBudget()
    {
        // 503 ServerBusy is Storage's throttling classification and the SDK's
        // retry policy owns it. The adapter must not add a second retry loop on
        // top, or the effective budget becomes the product of the two.
        var handler = new ScriptedHandler(
            _ => StorageResponses.ServerBusy(),
            _ => StorageResponses.Created());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        using var content = Fixture.Content("{}");
        var result = await store.CreateIfAbsentAsync(
            ArtifactName, content, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task AnUploadCarriesTheContentTypeSoTheArtifactIsReadableLater()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));

        using var content = Fixture.Content("{}");
        await store.CreateIfAbsentAsync(ArtifactName, content, "application/json", TestContext.Current.CancellationToken);

        Assert.Equal("application/json", Assert.Single(handler.Requests).Header("x-ms-blob-content-type"));
    }

    [Fact]
    public async Task TheAdapterHonoursCancellationBeforeTheRequestLeaves()
    {
        var handler = ScriptedHandler.Always(_ => StorageResponses.Created());
        var store = new BlobArtifactStore(ScriptedClients.Container(handler));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var content = Fixture.Content("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CreateIfAbsentAsync(ArtifactName, content, "application/json", cancelled.Token));

        Assert.Equal(0, handler.AttemptCount);
    }
}
