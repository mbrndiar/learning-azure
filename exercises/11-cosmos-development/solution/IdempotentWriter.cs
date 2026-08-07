namespace LearningAzure.Exercises.CosmosDevelopment;

/// <summary>An operation against the store.</summary>
public enum StoreOperation
{
    /// <summary>Insert, failing if the id already exists.</summary>
    Create,

    /// <summary>Insert or overwrite identical immutable content, keyed on the id.</summary>
    ImmutableUpsert,

    /// <summary>Insert or overwrite mutable content, keyed on the id.</summary>
    MutableUpsert,

    /// <summary>Overwrite only if the ETag still matches.</summary>
    ConditionalReplace,

    /// <summary>Overwrite whatever is there.</summary>
    UnconditionalReplace,

    /// <summary>Set a field to a fixed value.</summary>
    PatchSet,

    /// <summary>Add to a field's current value.</summary>
    PatchIncrement,

    /// <summary>Remove the document.</summary>
    Delete,
}

/// <summary>Why an operation did not return an answer.</summary>
public enum InterruptionKind
{
    /// <summary>
    /// The service refused the request outright. Nothing was applied, and the
    /// client knows it.
    /// </summary>
    Throttled,

    /// <summary>
    /// The client stopped waiting. Whether the service applied the write is
    /// unknown and unknowable from here.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Decides how to send a write so that sending it twice is survivable, because
/// a distributed system will eventually send it twice.
/// </summary>
public sealed class IdempotentWriter
{
    /// <summary>Builds an id a retry will produce again.</summary>
    /// <param name="source">What produced the document, such as a station id.</param>
    /// <param name="sequence">Its position in that source's series.</param>
    /// <returns>An id derived only from its inputs.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/> is negative.</exception>
    public static string DeterministicId(string source, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        // GAP 9: derived from the inputs, and from nothing else.
        //
        // A Guid or a timestamp makes the retry a different document, which is
        // precisely how duplicates are born: the first attempt committed, the
        // response was lost, and the retry created a second copy under a second
        // id that nothing will ever reconcile. The zero padding is not
        // cosmetic — ids are strings, so "station-1-10" sorts before
        // "station-1-9" without it, and range queries on id quietly break.
        // See lessons/11-cosmos-development/README.md#retrying-safely-means-retrying-idempotently
        return $"{source.Trim().ToLowerInvariant()}-{sequence:0000000000}";
    }

    /// <summary>Decides whether an interrupted operation may simply be sent again.</summary>
    /// <param name="operation">What was being attempted.</param>
    /// <param name="interruption">Why no answer came back.</param>
    /// <returns>Whether a plain retry is safe.</returns>
    public static RetrySafety Classify(StoreOperation operation, InterruptionKind interruption)
    {
        // GAP 10: the question is not "did it fail?", it is "do I know?".
        //
        // A 429 is a refusal: the service says, in so many words, that it did
        // not do the work. Every operation is safe to send again. A
        // cancellation or a timeout says nothing at all — the write may be
        // committed and only the response lost — so safety now depends entirely
        // on whether a second application would have a second effect.
        // See lessons/11-cosmos-development/README.md#retrying-safely-means-retrying-idempotently
        if (interruption == InterruptionKind.Throttled)
        {
            return RetrySafety.Safe;
        }

        return operation switch
        {
            // Applying these twice reaches the same state as applying them once.
            StoreOperation.ImmutableUpsert or StoreOperation.PatchSet or StoreOperation.Delete =>
                RetrySafety.Safe,

            // The ETag makes the second attempt fail rather than duplicate.
            StoreOperation.ConditionalReplace => RetrySafety.Safe,

            // A mutable upsert or blind replace can overwrite a newer value.
            // A second create is a 409 at best and a duplicate at worst; a
            // second increment is a wrong number.
            _ => RetrySafety.Unsafe,
        };
    }

    /// <summary>Rewrites an operation into one that survives being sent twice.</summary>
    /// <param name="operation">The operation the caller wanted.</param>
    /// <returns>The operation to send instead.</returns>
    public static StoreOperation MakeSafe(StoreOperation operation)
    {
        // GAP 11: every unsafe operation has a safe sibling that expresses the
        // same intent.
        //
        // This is the practical half of the previous decision. "Create this
        // reading" becomes "upsert it under an id I can compute again". "Add
        // one to the counter" becomes "read it, add one, write it back if
        // nobody else did" — slower, and correct. Refusing to retry at all is
        // the third option and it is a real one, but it is a decision to lose
        // data, so it should be made deliberately rather than by omission.
        // See lessons/11-cosmos-development/README.md#retrying-safely-means-retrying-idempotently
        return operation switch
        {
            StoreOperation.Create => StoreOperation.ImmutableUpsert,
            StoreOperation.MutableUpsert or StoreOperation.UnconditionalReplace or StoreOperation.PatchIncrement =>
                StoreOperation.ConditionalReplace,
            _ => operation,
        };
    }
}
