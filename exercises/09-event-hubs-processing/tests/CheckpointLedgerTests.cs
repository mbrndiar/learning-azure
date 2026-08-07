namespace LearningAzure.Exercises.EventHubsProcessing.Tests;

/// <summary>
/// The ledger is the only memory a restarted processor has. These checks pin
/// what it must refuse as tightly as what it must record.
/// </summary>
public sealed class CheckpointLedgerTests
{
    [Fact]
    public void APartitionIdIsRequired()
    {
        var ledger = new CheckpointLedger();

        Assert.Throws<ArgumentException>(() => ledger.Record(" ", 1));
        Assert.Throws<ArgumentNullException>(() => ledger.Record(null!, 1));
        Assert.Throws<ArgumentException>(() => ledger.ResumeFrom(string.Empty));
    }

    [Fact]
    public void ANegativeSequenceNumberIsRefused()
    {
        var ledger = new CheckpointLedger();

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Record(Fixtures.PartitionZero, -1));
    }

    [Fact]
    public void AFirstCheckpointIsRecorded()
    {
        var ledger = new CheckpointLedger();

        Assert.True(ledger.Record(Fixtures.PartitionZero, 40));
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(40, sequence);
        Assert.Equal(1, ledger.Writes);
    }

    [Fact]
    public void ZeroIsARealPosition()
    {
        var ledger = new CheckpointLedger();

        Assert.True(ledger.Record(Fixtures.PartitionZero, 0));
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(0, sequence);

        var resume = ledger.ResumeFrom(Fixtures.PartitionZero);

        Assert.False(resume.IsFromStart);
        Assert.Equal(0, resume.SequenceNumber);
    }

    [Fact]
    public void ACheckpointNeverMovesBackwards()
    {
        var ledger = new CheckpointLedger();

        ledger.Record(Fixtures.PartitionZero, 100);

        Assert.False(ledger.Record(Fixtures.PartitionZero, 60));
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionZero, out var sequence));
        Assert.Equal(100, sequence);
        Assert.Equal(1, ledger.RejectedRewinds);
    }

    [Fact]
    public void RerecordingTheSamePositionIsNotProgress()
    {
        var ledger = new CheckpointLedger();

        ledger.Record(Fixtures.PartitionZero, 100);

        Assert.False(ledger.Record(Fixtures.PartitionZero, 100));
        Assert.Equal(1, ledger.Writes);
        Assert.Equal(1, ledger.RejectedRewinds);
    }

    [Fact]
    public void PartitionsAreIndependent()
    {
        var ledger = new CheckpointLedger();

        ledger.Record(Fixtures.PartitionZero, 100);

        Assert.True(ledger.Record(Fixtures.PartitionOne, 3));
        Assert.True(ledger.TryGetCheckpoint(Fixtures.PartitionOne, out var sequence));
        Assert.Equal(3, sequence);
    }

    [Fact]
    public void ResumeStartsAfterTheCheckpointedEvent()
    {
        var ledger = new CheckpointLedger();

        ledger.Record(Fixtures.PartitionZero, 75);

        var resume = ledger.ResumeFrom(Fixtures.PartitionZero);

        Assert.Equal(75, resume.SequenceNumber);
        Assert.False(resume.IsInclusive);
        Assert.False(resume.IsFromStart);
    }

    [Fact]
    public void AnUnknownPartitionHasNoPositionAtAll()
    {
        var ledger = new CheckpointLedger();

        var resume = ledger.ResumeFrom("7");

        Assert.True(resume.IsFromStart);
        Assert.Equal(-1, resume.SequenceNumber);
        Assert.False(ledger.TryGetCheckpoint("7", out _));
    }

    [Fact]
    public void TheSnapshotIsACopy()
    {
        var ledger = new CheckpointLedger();

        ledger.Record(Fixtures.PartitionZero, 5);
        var snapshot = ledger.Snapshot();
        ledger.Record(Fixtures.PartitionZero, 9);

        Assert.Equal(5, snapshot[Fixtures.PartitionZero]);
    }
}
