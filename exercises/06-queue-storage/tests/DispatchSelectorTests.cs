namespace LearningAzure.Exercises.QueueStorage.Tests;

public sealed class DispatchSelectorTests
{
    private static WorkloadShape Shape(
        bool order = false,
        bool replay = false,
        bool independent = true,
        int consumers = 1) => new(order, replay, independent, consumers);

    [Fact]
    public void IndependentSingleConsumerWorkIsAQueue()
    {
        Assert.Equal(DispatchModel.WorkQueue, DispatchSelector.Choose(Shape()));
    }

    [Fact]
    public void ReplayRequiresAStream()
    {
        Assert.Equal(DispatchModel.EventStream, DispatchSelector.Choose(Shape(replay: true)));
    }

    [Fact]
    public void PerKeyOrderRequiresAStream()
    {
        Assert.Equal(DispatchModel.EventStream, DispatchSelector.Choose(Shape(order: true)));
    }

    [Fact]
    public void FanOutToTwoConsumersRequiresAStream()
    {
        Assert.Equal(DispatchModel.EventStream, DispatchSelector.Choose(Shape(consumers: 2)));
    }

    [Fact]
    public void ReplayOutranksIndependentScaling()
    {
        var shape = Shape(replay: true, independent: true);

        Assert.Equal(DispatchModel.EventStream, DispatchSelector.Choose(shape));
    }

    [Fact]
    public void HighThroughputAloneDoesNotJustifyAStream()
    {
        // The most common wrong reason to reach for Event Hubs.
        var shape = Shape(independent: true, consumers: 1);

        Assert.Equal(DispatchModel.WorkQueue, DispatchSelector.Choose(shape));
    }

    [Fact]
    public void ANullShapeIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => DispatchSelector.Choose(null!));
    }

    [Fact]
    public void AConsumerCountBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DispatchSelector.Choose(Shape(consumers: 0)));
    }

    [Fact]
    public void TheJustificationNamesTheChosenModel()
    {
        Assert.StartsWith("Work queue:", DispatchSelector.Justify(Shape()), StringComparison.Ordinal);
        Assert.StartsWith("Event stream:", DispatchSelector.Justify(Shape(replay: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheJustificationForReplayNamesReplay()
    {
        var reason = DispatchSelector.Justify(Shape(replay: true));

        Assert.Contains("re-read", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheJustificationForOrderNamesOrder()
    {
        var reason = DispatchSelector.Justify(Shape(order: true));

        Assert.Contains("order", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheJustificationForFanOutNamesTheConsumerCount()
    {
        var reason = DispatchSelector.Justify(Shape(consumers: 4));

        Assert.Contains("4", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJustificationAgreesWithTheChoiceForEveryShape()
    {
        bool[] flags = [false, true];

        foreach (var order in flags)
        {
            foreach (var replay in flags)
            {
                foreach (var consumers in (int[])[1, 3])
                {
                    var shape = Shape(order: order, replay: replay, consumers: consumers);
                    var expected = DispatchSelector.Choose(shape) == DispatchModel.WorkQueue
                        ? "Work queue:"
                        : "Event stream:";

                    Assert.StartsWith(expected, DispatchSelector.Justify(shape), StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void JustifyRejectsANullShape()
    {
        Assert.Throws<ArgumentNullException>(() => DispatchSelector.Justify(null!));
    }
}
