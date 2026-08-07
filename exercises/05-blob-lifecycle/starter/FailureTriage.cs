using Azure;

namespace LearningAzure.Exercises.BlobLifecycle;

/// <summary>Turns a service failure into a decision, once, in one place.</summary>
/// <remarks>
/// Retrying a 412 unchanged is a lost update. Retrying a 403 is a denial of
/// service against your own auth endpoint. Not retrying a 503 is an outage you
/// caused. The difference is entirely in the status code, so the classification
/// belongs in one function that can be read and tested.
/// </remarks>
public static class FailureTriage
{
    /// <summary>Decides what to do about <paramref name="error"/>.</summary>
    /// <param name="error">The failure the SDK surfaced.</param>
    /// <returns>The action the caller should take.</returns>
    public static RecoveryAction Classify(RequestFailedException error) =>
        // GAP 6 — Decide on the STATUS, not the message.
        //
        // Error messages are prose, are localized, and change without notice.
        // Status codes are the contract. Matching on message text is a bug that
        // ships green and breaks on a service update nobody told you about.
        //
        //   412, 409           -> RereadAndRetry  (your copy is stale)
        //   429, 500, 503      -> BackOffAndRetry (the service asked for it)
        //   404                -> TreatAsAbsent   (the caller decides if that is an error)
        //   everything else    -> Abort           (repeating it cannot change the answer)
        throw new NotImplementedException(
            "GAP 6: implement FailureTriage.Classify. See "
            + "lessons/05-blob-lifecycle/README.md#a-412-is-an-answer-not-an-error.");

    /// <summary>Maps a status code to what the conditional write actually meant.</summary>
    /// <param name="status">HTTP status the service returned.</param>
    /// <returns>The outcome, or <c>null</c> when the status is not a precondition result.</returns>
    public static PreconditionOutcome? Interpret(int status) =>
        // GAP 7 — Only 200/201, 412, 409 and 404 are precondition results.
        // Everything else must return null so the caller escalates instead of
        // guessing.
        throw new NotImplementedException(
            "GAP 7: implement FailureTriage.Interpret. See "
            + "lessons/05-blob-lifecycle/README.md#a-412-is-an-answer-not-an-error.");
}
