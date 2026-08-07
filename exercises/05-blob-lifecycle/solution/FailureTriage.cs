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
    public static RecoveryAction Classify(RequestFailedException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // GAP 6 — Decide on the STATUS, not the message.
        //
        // Error messages are prose, are localized, and change without notice.
        // Status codes are the contract. Matching on message text is a bug that
        // ships green and breaks on a service update nobody told you about.
        return error.Status switch
        {
            // The caller's copy is stale. Re-read and re-apply; retrying the same
            // bytes would either fail forever or, worse, eventually succeed.
            412 or 409 => RecoveryAction.RereadAndRetry,

            // The service explicitly asked to be called again later.
            429 or 500 or 503 => RecoveryAction.BackOffAndRetry,

            // The artifact is gone. Whether that is an error is the caller's
            // business, not the triage function's.
            404 => RecoveryAction.TreatAsAbsent,

            // 400, 401, 403 and everything else: the request is wrong, or the
            // identity is. Repeating it byte for byte cannot change the answer.
            _ => RecoveryAction.Abort,
        };
    }

    /// <summary>Maps a status code to what the conditional write actually meant.</summary>
    /// <param name="status">HTTP status the service returned.</param>
    /// <returns>The outcome, or <c>null</c> when the status is not a precondition result.</returns>
    public static PreconditionOutcome? Interpret(int status) =>
        // GAP 7 — Only these four statuses are precondition results. Everything
        // else must return null so the caller escalates instead of guessing.
        status switch
        {
            200 or 201 => PreconditionOutcome.Written,
            412 => PreconditionOutcome.Stale,
            409 => PreconditionOutcome.AlreadyExists,
            404 => PreconditionOutcome.Absent,
            _ => null,
        };
}
