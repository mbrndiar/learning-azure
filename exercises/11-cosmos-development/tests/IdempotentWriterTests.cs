using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// Checks the decisions that make a retry survivable: an id a retry can
/// reproduce, and an honest answer about which operations may be sent twice.
/// </summary>
public sealed class IdempotentWriterTests
{
    [Fact]
    public void DeterministicId_IsTheSameForTheSameInputs()
    {
        Assert.Equal(
            IdempotentWriter.DeterministicId("station-05", 42),
            IdempotentWriter.DeterministicId("station-05", 42));
    }

    [Fact]
    public void DeterministicId_DiffersForDifferentSequences()
    {
        Assert.NotEqual(
            IdempotentWriter.DeterministicId("station-05", 42),
            IdempotentWriter.DeterministicId("station-05", 43));
    }

    [Fact]
    public void DeterministicId_DiffersForDifferentSources()
    {
        Assert.NotEqual(
            IdempotentWriter.DeterministicId("station-05", 42),
            IdempotentWriter.DeterministicId("station-06", 42));
    }

    [Fact]
    public void DeterministicId_SortsInSequenceOrderAsAString()
    {
        // Ids are strings. Without padding, "-10" sorts before "-9".
        var sequences = new long[] { 9L, 10L, 100L };
        var ids = sequences
            .Select(sequence => IdempotentWriter.DeterministicId("station-05", sequence))
            .ToList();

        Assert.Equal(ids, [.. ids.OrderBy(id => id, StringComparer.Ordinal)]);
    }

    [Fact]
    public void DeterministicId_IgnoresSurroundingWhitespaceAndCase()
    {
        Assert.Equal(
            IdempotentWriter.DeterministicId("station-05", 1),
            IdempotentWriter.DeterministicId("  Station-05 ", 1));
    }

    [Fact]
    public void DeterministicId_RejectsANegativeSequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IdempotentWriter.DeterministicId("station-05", -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeterministicId_RejectsABlankSource(string source)
    {
        Assert.Throws<ArgumentException>(() => IdempotentWriter.DeterministicId(source, 1));
    }

    [Theory]
    [InlineData(StoreOperation.Create)]
    [InlineData(StoreOperation.Upsert)]
    [InlineData(StoreOperation.ConditionalReplace)]
    [InlineData(StoreOperation.UnconditionalReplace)]
    [InlineData(StoreOperation.PatchSet)]
    [InlineData(StoreOperation.PatchIncrement)]
    [InlineData(StoreOperation.Delete)]
    public void Classify_CallsEverythingSafeAfterAnOutrightRefusal(StoreOperation operation)
    {
        // A 429 is the service saying it did not do the work. There is nothing
        // to be undone, so anything may be sent again.
        Assert.Equal(
            RetrySafety.Safe,
            IdempotentWriter.Classify(operation, InterruptionKind.Throttled));
    }

    [Theory]
    [InlineData(StoreOperation.Upsert)]
    [InlineData(StoreOperation.PatchSet)]
    [InlineData(StoreOperation.Delete)]
    [InlineData(StoreOperation.ConditionalReplace)]
    public void Classify_AllowsRetryingOperationsWithNoSecondEffect(StoreOperation operation)
    {
        Assert.Equal(
            RetrySafety.Safe,
            IdempotentWriter.Classify(operation, InterruptionKind.Cancelled));
    }

    [Theory]
    [InlineData(StoreOperation.Create)]
    [InlineData(StoreOperation.PatchIncrement)]
    [InlineData(StoreOperation.UnconditionalReplace)]
    public void Classify_RefusesToRetryOperationsThatWouldApplyTwice(StoreOperation operation)
    {
        Assert.Equal(
            RetrySafety.Unsafe,
            IdempotentWriter.Classify(operation, InterruptionKind.Cancelled));
    }

    [Fact]
    public void Classify_DistinguishesTheTwoInterruptions()
    {
        // The whole point: the same operation is safe after a refusal and
        // unsafe after a cancellation, because only one of them is an answer.
        Assert.Equal(
            RetrySafety.Safe,
            IdempotentWriter.Classify(StoreOperation.Create, InterruptionKind.Throttled));
        Assert.Equal(
            RetrySafety.Unsafe,
            IdempotentWriter.Classify(StoreOperation.Create, InterruptionKind.Cancelled));
    }

    [Fact]
    public void MakeSafe_TurnsACreateIntoAnUpsert()
    {
        Assert.Equal(StoreOperation.Upsert, IdempotentWriter.MakeSafe(StoreOperation.Create));
    }

    [Fact]
    public void MakeSafe_TurnsAnIncrementIntoAConditionalReplace()
    {
        Assert.Equal(
            StoreOperation.ConditionalReplace,
            IdempotentWriter.MakeSafe(StoreOperation.PatchIncrement));
    }

    [Fact]
    public void MakeSafe_TurnsABlindReplaceIntoAConditionalOne()
    {
        Assert.Equal(
            StoreOperation.ConditionalReplace,
            IdempotentWriter.MakeSafe(StoreOperation.UnconditionalReplace));
    }

    [Theory]
    [InlineData(StoreOperation.Upsert)]
    [InlineData(StoreOperation.PatchSet)]
    [InlineData(StoreOperation.Delete)]
    [InlineData(StoreOperation.ConditionalReplace)]
    public void MakeSafe_LeavesAlreadySafeOperationsAlone(StoreOperation operation)
    {
        Assert.Equal(operation, IdempotentWriter.MakeSafe(operation));
    }

    [Theory]
    [InlineData(StoreOperation.Create)]
    [InlineData(StoreOperation.PatchIncrement)]
    [InlineData(StoreOperation.UnconditionalReplace)]
    [InlineData(StoreOperation.Upsert)]
    [InlineData(StoreOperation.PatchSet)]
    [InlineData(StoreOperation.Delete)]
    [InlineData(StoreOperation.ConditionalReplace)]
    public void MakeSafe_AlwaysProducesSomethingRetryable(StoreOperation operation)
    {
        var rewritten = IdempotentWriter.MakeSafe(operation);

        Assert.Equal(
            RetrySafety.Safe,
            IdempotentWriter.Classify(rewritten, InterruptionKind.Cancelled));
    }
}
