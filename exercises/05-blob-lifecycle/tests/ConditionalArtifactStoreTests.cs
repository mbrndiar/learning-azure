using System.Net;
using System.Text;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Exercises.BlobLifecycle.Tests;

/// <summary>
/// Drives a real <c>BlobClient</c> over a scripted transport, so the assertions
/// are about what the SDK actually puts on the wire.
/// </summary>
public sealed class ConditionalArtifactStoreTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("field note v2");

    [Fact]
    public async Task AReadReturnsTheBytesAndTheEtagTogether()
    {
        var handler = new ScriptedHandler(_ => ScriptedClient.Download("field note v1", "\"0x1\""));
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        var revision = await store.TryReadAsync("note.txt", TestContext.Current.CancellationToken);

        Assert.NotNull(revision);
        Assert.Equal("field note v1", Encoding.UTF8.GetString(revision.Content));
        Assert.Equal("\"0x1\"", revision.ETag);
    }

    [Fact]
    public async Task TheEtagFromAReadIsUsableAsAnIfMatchWithoutEditing()
    {
        // The ETag the SDK exposes is unquoted by default and the service
        // rejects an unquoted If-Match. Reading and writing must therefore agree
        // on the HTTP form, or the round trip fails only against a real service.
        var handler = new ScriptedHandler(
            _ => ScriptedClient.Download("field note v1", "\"0x1\""),
            _ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        var revision = await store.TryReadAsync("note.txt", TestContext.Current.CancellationToken);
        await store.WriteIfUnchangedAsync(
            "note.txt", Payload, revision!.ETag, TestContext.Current.CancellationToken);

        Assert.Equal("\"0x1\"", handler.Requests[1].Header("If-Match"));
    }

    [Fact]
    public async Task AMissingArtifactReadsAsNullRatherThanThrowing()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.NotFound());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        Assert.Null(await store.TryReadAsync("note.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AForbiddenReadStillThrows()
    {
        // Catching by exception TYPE rather than status turns a missing role
        // assignment into "there is no data", which is a silent, wrong answer.
        var handler = ScriptedHandler.Always(
            _ => StorageResponses.Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch", "Forbidden."));
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        var error = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => store.TryReadAsync("note.txt", TestContext.Current.CancellationToken));

        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task AConditionalWritePutsTheIfMatchHeaderOnTheWire()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("\"0x1\"", request.Header("If-Match"));
    }

    [Fact]
    public async Task AConditionalWriteSendsNoIfNoneMatchHeader()
    {
        // If-Match and If-None-Match answer opposite questions. Sending both is
        // a contradiction the service resolves in a way nobody predicts.
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Requests).Header("If-None-Match"));
    }

    [Fact]
    public async Task AConditionalWriteSendsTheContent()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken);

        Assert.Equal(Payload, Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task AWrittenWriteReportsWritten()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        Assert.Equal(
            PreconditionOutcome.Written,
            await store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALostRaceReportsStaleInsteadOfThrowing()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.PreconditionFailed());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        Assert.Equal(
            PreconditionOutcome.Stale,
            await store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALostRaceIsNotRetriedByTheSdk()
    {
        // 412 is not transient. A retry policy that treats it as one turns a
        // detected conflict into a hidden one.
        var handler = ScriptedHandler.Always(_ => StorageResponses.PreconditionFailed());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task AnUnexpectedFailureStillPropagates()
    {
        var handler = ScriptedHandler.Always(
            _ => StorageResponses.Error(HttpStatusCode.BadRequest, "InvalidHeaderValue", "Bad header."));
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        var error = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", TestContext.Current.CancellationToken));

        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task ACreateSendsIfNoneMatchStar()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await store.CreateIfAbsentAsync("note.txt", Payload, TestContext.Current.CancellationToken);

        Assert.Equal("*", Assert.Single(handler.Requests).Header("If-None-Match"));
    }

    [Fact]
    public async Task ACreateThatLosesReportsAlreadyExists()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Conflict());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        Assert.Equal(
            PreconditionOutcome.AlreadyExists,
            await store.CreateIfAbsentAsync("note.txt", Payload, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACreateRejectedWithAPreconditionAlsoReportsAlreadyExists()
    {
        // The service uses 409 or 412 depending on which check tripped. To the
        // caller they are the same fact, so both must map to one outcome.
        var handler = new ScriptedHandler(_ => StorageResponses.PreconditionFailed());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        Assert.Equal(
            PreconditionOutcome.AlreadyExists,
            await store.CreateIfAbsentAsync("note.txt", Payload, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACreateDoesNotSendAnIfMatchHeader()
    {
        var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await store.CreateIfAbsentAsync("note.txt", Payload, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Requests).Header("If-Match"));
    }

    [Fact]
    public async Task ACancelledTokenReachesTheTransport()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = ScriptedHandler.Always(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteIfUnchangedAsync("note.txt", Payload, "\"0x1\"", cts.Token));
    }

    [Fact]
    public async Task ANamelessArtifactIsRejectedBeforeAnyRequest()
    {
        var handler = ScriptedHandler.Always(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteIfUnchangedAsync(" ", Payload, "\"0x1\"", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.AttemptCount);
    }

    [Fact]
    public async Task AnEmptyIfMatchIsRejectedBeforeAnyRequest()
    {
        // An empty ETag would send no precondition at all, which is precisely
        // the unconditional overwrite this class exists to prevent.
        var handler = ScriptedHandler.Always(_ => StorageResponses.Created());
        var store = new ConditionalArtifactStore(ScriptedClient.Container(handler));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteIfUnchangedAsync("note.txt", Payload, "", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.AttemptCount);
    }

    [Fact]
    public void AStoreNeedsAContainer()
    {
        Assert.Throws<ArgumentNullException>(() => new ConditionalArtifactStore(null!));
    }
}
