namespace LearningAzure.Exercises.EventHubsModel.Tests;

public sealed class StreamOrQueueSelectorTests
{
    private static WorkloadRequirement Baseline(string name) =>
        new(name,
            RequiresPerKeyOrdering: false,
            RequiresReplay: false,
            IndependentReaderCount: 1,
            RequiresPerItemAcknowledgement: false,
            ItemDurationSpread: WorkDurationSpread.Uniform);

    [Fact]
    public void ReplayForcesAStream()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("replay") with { RequiresReplay = true });

        Assert.Equal(DispatchPrimitive.EventStream, choice.Primitive);
    }

    [Fact]
    public void TheReplayReasonSaysWhyAQueueCannotDoIt()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("replay") with { RequiresReplay = true });

        Assert.Contains("delete", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoIndependentReadersForceAStream()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("fan-out") with { IndependentReaderCount = 2 });

        Assert.Equal(DispatchPrimitive.EventStream, choice.Primitive);
    }

    [Fact]
    public void OneReaderIsNotFanOut()
    {
        var choice = StreamOrQueueSelector.Choose(
            Baseline("single") with { IndependentReaderCount = 1, RequiresPerItemAcknowledgement = true });

        Assert.Equal(DispatchPrimitive.WorkQueue, choice.Primitive);
    }

    [Fact]
    public void PerKeyOrderingForcesAStream()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("ordering") with { RequiresPerKeyOrdering = true });

        Assert.Equal(DispatchPrimitive.EventStream, choice.Primitive);
    }

    [Fact]
    public void PerItemAcknowledgementForcesAQueue()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("ack") with { RequiresPerItemAcknowledgement = true });

        Assert.Equal(DispatchPrimitive.WorkQueue, choice.Primitive);
    }

    [Fact]
    public void TheAcknowledgementReasonNamesTheCursor()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("ack") with { RequiresPerItemAcknowledgement = true });

        Assert.Contains("cursor", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WidelyVaryingWorkForcesAQueue()
    {
        var choice = StreamOrQueueSelector.Choose(
            Baseline("spread") with { ItemDurationSpread = WorkDurationSpread.Wide });

        Assert.Equal(DispatchPrimitive.WorkQueue, choice.Primitive);
    }

    [Fact]
    public void ReplayOutranksPerItemAcknowledgement()
    {
        // Both are present. Only one of them is structurally impossible for the
        // other primitive: a queue cannot be configured into replay, while a
        // stream consumer can maintain its own per-item bookkeeping.
        var choice = StreamOrQueueSelector.Choose(
            Baseline("both") with { RequiresReplay = true, RequiresPerItemAcknowledgement = true });

        Assert.Equal(DispatchPrimitive.EventStream, choice.Primitive);
    }

    [Fact]
    public void FanOutOutranksWidelyVaryingWork()
    {
        var choice = StreamOrQueueSelector.Choose(
            Baseline("both") with { IndependentReaderCount = 3, ItemDurationSpread = WorkDurationSpread.Wide });

        Assert.Equal(DispatchPrimitive.EventStream, choice.Primitive);
    }

    [Fact]
    public void PlainWorkGetsTheCheaperPrimitive()
    {
        var choice = StreamOrQueueSelector.Choose(Baseline("plain"));

        Assert.Equal(DispatchPrimitive.WorkQueue, choice.Primitive);
    }

    [Fact]
    public void TheExpeditionTelemetryStreamIsAStream()
    {
        // Sensor telemetry: ordered per station, re-readable for a
        // reprocessing pass, and consumed by both a live dashboard and the
        // Cosmos projection.
        var telemetry = new WorkloadRequirement(
            "expedition telemetry",
            RequiresPerKeyOrdering: true,
            RequiresReplay: true,
            IndependentReaderCount: 2,
            RequiresPerItemAcknowledgement: false,
            ItemDurationSpread: WorkDurationSpread.Uniform);

        Assert.Equal(DispatchPrimitive.EventStream, StreamOrQueueSelector.Choose(telemetry).Primitive);
    }

    [Fact]
    public void TheArtifactProcessingWorkloadIsStillAQueue()
    {
        // Module 6's work orders: one consumer, one retry per item, and a
        // handling cost that varies from milliseconds to minutes.
        var artifacts = new WorkloadRequirement(
            "artifact processing",
            RequiresPerKeyOrdering: false,
            RequiresReplay: false,
            IndependentReaderCount: 1,
            RequiresPerItemAcknowledgement: true,
            ItemDurationSpread: WorkDurationSpread.Wide);

        Assert.Equal(DispatchPrimitive.WorkQueue, StreamOrQueueSelector.Choose(artifacts).Primitive);
    }

    [Fact]
    public void EveryChoiceCarriesAReason()
    {
        var requirements = new[]
        {
            Baseline("a") with { RequiresReplay = true },
            Baseline("b") with { IndependentReaderCount = 4 },
            Baseline("c") with { RequiresPerKeyOrdering = true },
            Baseline("d") with { RequiresPerItemAcknowledgement = true },
            Baseline("e") with { ItemDurationSpread = WorkDurationSpread.Wide },
            Baseline("f"),
        };

        Assert.All(
            requirements,
            requirement => Assert.False(string.IsNullOrWhiteSpace(StreamOrQueueSelector.Choose(requirement).Reason)));
    }

    [Fact]
    public void ARequirementIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => StreamOrQueueSelector.Choose(null!));
    }
}
