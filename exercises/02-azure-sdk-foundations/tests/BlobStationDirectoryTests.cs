using System.Net;
using System.Text;
using Azure;
using LearningAzure.Support.AzureFakes;

namespace LearningAzure.Exercises.SdkFoundations.Tests;

/// <summary>
/// Verifies the retry, cancellation, and error-classification seams by driving a
/// real Azure SDK client over a scripted transport.
/// </summary>
public sealed class BlobStationDirectoryTests
{
    private static readonly StationRecord Bravo = new("station-bravo", "Bravo Ridge", "westeurope");

    [Fact]
    public async Task ARecordIsReadBackFromTheService()
    {
        using var handler = new ScriptedHandler(_ => ScriptedClient.StationBody(Bravo));
        var directory = ScriptedClient.Directory(handler);

        var record = await directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.Equal(Bravo, record);
    }

    [Fact]
    public async Task AMissingRecordIsAnAnswerNotAFailure()
    {
        using var handler = ScriptedHandler.Always(_ => StorageResponses.NotFound());
        var directory = ScriptedClient.Directory(handler);

        var record = await directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.Null(record);
    }

    [Fact]
    public async Task AMissingRecordIsNotRetried()
    {
        using var handler = ScriptedHandler.Always(_ => StorageResponses.NotFound());
        var directory = ScriptedClient.Directory(handler, maxRetries: 3);

        await directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.AttemptCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")]
    [InlineData(HttpStatusCode.Unauthorized, "NoAuthenticationInformation")]
    [InlineData(HttpStatusCode.Conflict, "ContainerBeingDeleted")]
    public async Task ARealFailureKeepsPropagating(HttpStatusCode status, string errorCode)
    {
        using var handler = ScriptedHandler.Always(
            _ => ScriptedClient.Failure(status, errorCode, "scripted failure"));
        var directory = ScriptedClient.Directory(handler);

        var error = await Assert.ThrowsAsync<RequestFailedException>(
            () => directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken));

        Assert.Equal((int)status, error.Status);
    }

    [Fact]
    public async Task AForbiddenResponseIsNotReportedAsMissingData()
    {
        using var handler = ScriptedHandler.Always(
            _ => ScriptedClient.Failure(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch", "no role"));
        var directory = ScriptedClient.Directory(handler);

        // The whole point of classifying by status: a missing role assignment must
        // never be indistinguishable from "this station has no record".
        await Assert.ThrowsAsync<RequestFailedException>(
            () => directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATransientFailureIsRetriedAndThenSucceeds()
    {
        using var handler = new ScriptedHandler(
            _ => StorageResponses.ServerBusy(),
            _ => StorageResponses.ServerBusy(),
            _ => ScriptedClient.StationBody(Bravo));
        var directory = ScriptedClient.Directory(handler, maxRetries: 3);

        var record = await directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.Equal(Bravo, record);
    }

    [Fact]
    public async Task ARetriedOperationCostsMoreThanOneAttempt()
    {
        using var handler = new ScriptedHandler(
            _ => StorageResponses.ServerBusy(),
            _ => StorageResponses.ServerBusy(),
            _ => ScriptedClient.StationBody(Bravo));
        var directory = ScriptedClient.Directory(handler, maxRetries: 3);

        await directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.AttemptCount);
    }

    [Fact]
    public async Task TheRetryBudgetIsBounded()
    {
        using var handler = ScriptedHandler.Always(_ => StorageResponses.ServerBusy());
        var directory = ScriptedClient.Directory(handler, maxRetries: 2);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.AttemptCount);
    }

    [Fact]
    public async Task ZeroRetriesMeansExactlyOneAttempt()
    {
        using var handler = ScriptedHandler.Always(_ => StorageResponses.ServerBusy());
        var directory = ScriptedClient.Directory(handler, maxRetries: 0);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task AnExhaustedRetryBudgetSurfacesTheServiceStatus()
    {
        using var handler = ScriptedHandler.Always(_ => StorageResponses.ServerBusy());
        var directory = ScriptedClient.Directory(handler, maxRetries: 1);

        var error = await Assert.ThrowsAsync<RequestFailedException>(
            () => directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken));

        Assert.Equal(503, error.Status);
    }

    [Fact]
    public async Task ACancelledTokenStopsTheOperation()
    {
        using var handler = ScriptedHandler.Always(_ => ScriptedClient.StationBody(Bravo));
        var directory = ScriptedClient.Directory(handler, maxRetries: 3);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.TryGetAsync("station-bravo", cancellation.Token));
    }

    [Fact]
    public async Task ACancelledTokenSpendsNoRetryBudget()
    {
        using var handler = ScriptedHandler.Always(_ => ScriptedClient.StationBody(Bravo));
        var directory = ScriptedClient.Directory(handler, maxRetries: 3);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.TryGetAsync("station-bravo", cancellation.Token));

        Assert.Equal(0, handler.AttemptCount);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoAMissingRecord()
    {
        using var handler = ScriptedHandler.Always(_ => StorageResponses.NotFound());
        var directory = ScriptedClient.Directory(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // A catch-all that returns null would swallow this and answer "no record",
        // which is a silent wrong answer rather than a visible failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.TryGetAsync("station-bravo", cancellation.Token));
    }

    [Fact]
    public async Task AReadTargetsTheStationsBlobName()
    {
        using var handler = new ScriptedHandler(_ => ScriptedClient.StationBody(Bravo));
        var directory = ScriptedClient.Directory(handler);

        await directory.TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.EndsWith("/stations/station-bravo.json", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASaveSendsAPut()
    {
        using var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var directory = ScriptedClient.Directory(handler);

        await directory.SaveAsync(Bravo, TestContext.Current.CancellationToken);

        Assert.Equal("PUT", handler.Requests[0].Method);
    }

    [Fact]
    public async Task ASaveSendsTheRecordAsItsBody()
    {
        using var handler = new ScriptedHandler(_ => StorageResponses.Created());
        var directory = ScriptedClient.Directory(handler);

        await directory.SaveAsync(Bravo, TestContext.Current.CancellationToken);

        var body = Encoding.UTF8.GetString(handler.Requests[0].Body);
        Assert.Contains("station-bravo", body, StringComparison.Ordinal);
        Assert.Contains("Bravo Ridge", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASaveRoundTripsThroughTheReadPath()
    {
        using var writeHandler = new ScriptedHandler(_ => StorageResponses.Created());
        await ScriptedClient.Directory(writeHandler).SaveAsync(Bravo, TestContext.Current.CancellationToken);
        var written = writeHandler.Requests[0].Body;

        using var readHandler = new ScriptedHandler(
            _ => StorageResponses.OkWithBody(written, "application/json"));
        var record = await ScriptedClient.Directory(readHandler)
            .TryGetAsync("station-bravo", TestContext.Current.CancellationToken);

        Assert.Equal(Bravo, record);
    }

    [Fact]
    public async Task ASaveFailureIsNotSwallowed()
    {
        using var handler = ScriptedHandler.Always(
            _ => ScriptedClient.Failure(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch", "no role"));
        var directory = ScriptedClient.Directory(handler);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => directory.SaveAsync(Bravo, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheBlobNameIsDerivedFromTheStationId()
    {
        Assert.Equal("station-bravo.json", BlobStationDirectory.BlobName("station-bravo"));
    }

    [Fact]
    public void ABlankStationIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => BlobStationDirectory.BlobName("  "));
    }
}
