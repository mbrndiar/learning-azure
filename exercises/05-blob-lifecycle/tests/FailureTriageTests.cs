using Azure;

namespace LearningAzure.Exercises.BlobLifecycle.Tests;

/// <summary>Asserts that a failure becomes a decision, and the right one.</summary>
public sealed class FailureTriageTests
{
    private static RequestFailedException Error(int status, string code) =>
        new(status, "message", code, innerException: null);

    [Theory]
    [InlineData(412)]
    [InlineData(409)]
    public void AConflictMeansReReadAndRetry(int status)
    {
        Assert.Equal(RecoveryAction.RereadAndRetry, FailureTriage.Classify(Error(status, "ConditionNotMet")));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void AServiceSideFailureMeansBackOffAndRetry(int status)
    {
        Assert.Equal(RecoveryAction.BackOffAndRetry, FailureTriage.Classify(Error(status, "ServerBusy")));
    }

    [Fact]
    public void ANotFoundMeansTreatAsAbsent()
    {
        Assert.Equal(RecoveryAction.TreatAsAbsent, FailureTriage.Classify(Error(404, "BlobNotFound")));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(405)]
    public void ACallerSideFailureMeansAbort(int status)
    {
        // Retrying a 403 is a denial-of-service attack on your own token
        // endpoint, and the answer never changes.
        Assert.Equal(RecoveryAction.Abort, FailureTriage.Classify(Error(status, "AuthorizationPermissionMismatch")));
    }

    [Fact]
    public void APreconditionFailureIsNeverBackedOffAndRetried()
    {
        // A blind retry of a 412 either fails forever or, on the attempt where
        // the competing writer pauses, succeeds and destroys their work.
        Assert.NotEqual(RecoveryAction.BackOffAndRetry, FailureTriage.Classify(Error(412, "ConditionNotMet")));
    }

    [Fact]
    public void TheDecisionIgnoresTheErrorMessage()
    {
        // Messages are prose and change without notice; the status is the
        // contract. Two identical statuses must classify identically whatever
        // the text says.
        var a = new RequestFailedException(503, "The server is busy.", "ServerBusy", null);
        var b = new RequestFailedException(503, "Le serveur est occupe.", "ServerBusy", null);

        Assert.Equal(FailureTriage.Classify(a), FailureTriage.Classify(b));
    }

    [Fact]
    public void TheDecisionIgnoresTheErrorCode()
    {
        var a = new RequestFailedException(409, "conflict", "BlobAlreadyExists", null);
        var b = new RequestFailedException(409, "conflict", "ContainerAlreadyExists", null);

        Assert.Equal(FailureTriage.Classify(a), FailureTriage.Classify(b));
    }

    [Fact]
    public void ANullErrorIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => FailureTriage.Classify(null!));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void ASuccessInterpretsAsWritten(int status)
    {
        Assert.Equal(PreconditionOutcome.Written, FailureTriage.Interpret(status));
    }

    [Fact]
    public void A412InterpretsAsStale()
    {
        Assert.Equal(PreconditionOutcome.Stale, FailureTriage.Interpret(412));
    }

    [Fact]
    public void A409InterpretsAsAlreadyExists()
    {
        Assert.Equal(PreconditionOutcome.AlreadyExists, FailureTriage.Interpret(409));
    }

    [Fact]
    public void A404InterpretsAsAbsent()
    {
        Assert.Equal(PreconditionOutcome.Absent, FailureTriage.Interpret(404));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void AnythingElseInterpretsAsNothing(int status)
    {
        // Guessing here is how a throttled request gets recorded as "the other
        // writer won" and the real problem stays invisible.
        Assert.Null(FailureTriage.Interpret(status));
    }
}
