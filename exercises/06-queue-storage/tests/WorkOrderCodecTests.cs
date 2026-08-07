using System.Text;
using System.Text.Json;

namespace LearningAzure.Exercises.QueueStorage.Tests;

public sealed class WorkOrderCodecTests
{
    private static readonly WorkOrder Sample = new("wo-1", "station-01/reading.json", "ingest");

    [Fact]
    public void AnEncodedOrderRoundTripsBackToAnEqualOrder()
    {
        var decoded = WorkOrderCodec.Decode(WorkOrderCodec.Encode(Sample));

        Assert.Equal(Sample, decoded);
    }

    [Fact]
    public void TheEncodedBodyIsValidBase64()
    {
        var body = WorkOrderCodec.Encode(Sample);

        Assert.True(Convert.TryFromBase64String(body, new byte[body.Length], out _));
    }

    [Fact]
    public void TheEncodedBodyIsLargerThanTheJsonItCarries()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(Sample, WorkOrderCodec.SerializerOptions);
        var body = WorkOrderCodec.Encode(Sample);

        Assert.True(
            Encoding.UTF8.GetByteCount(body) > json.Length,
            $"Base64 must inflate the payload, but {Encoding.UTF8.GetByteCount(body)} <= {json.Length}.");
    }

    [Fact]
    public void ThePublishedLimitIsSixtyFourKibibytes()
    {
        Assert.Equal(65536, WorkOrderCodec.MaxMessageBytes);
    }

    [Fact]
    public void AnOrderThatFitsOnceEncodedIsAccepted()
    {
        var order = new WorkOrder("wo-2", new string('a', 40 * 1024), "ingest");

        var body = WorkOrderCodec.Encode(order);

        Assert.True(Encoding.UTF8.GetByteCount(body) <= WorkOrderCodec.MaxMessageBytes);
    }

    [Fact]
    public void AnOrderThatOnlyFitsBeforeEncodingIsRejected()
    {
        // 60 KiB of JSON is under the limit; its Base64 form is 80 KiB and is not.
        var order = new WorkOrder("wo-3", new string('a', 60 * 1024), "ingest");

        var error = Assert.Throws<ArgumentException>(() => WorkOrderCodec.Encode(order));

        Assert.Equal("order", error.ParamName);
    }

    [Fact]
    public void TheRejectionSaysWhatToDoInstead()
    {
        var order = new WorkOrder("wo-4", new string('a', 60 * 1024), "ingest");

        var error = Assert.Throws<ArgumentException>(() => WorkOrderCodec.Encode(order));

        Assert.Contains("blob", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncodingRejectsANullOrder()
    {
        Assert.Throws<ArgumentNullException>(() => WorkOrderCodec.Encode(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(48 * 1024)]
    public void APayloadUpToFortyEightKibibytesFits(int payloadBytes)
    {
        Assert.True(WorkOrderCodec.Fits(payloadBytes));
    }

    [Theory]
    [InlineData(49 * 1024)]
    [InlineData(64 * 1024)]
    public void APayloadAboveFortyEightKibibytesDoesNotFit(int payloadBytes)
    {
        Assert.False(WorkOrderCodec.Fits(payloadBytes));
    }

    [Fact]
    public void TheLastFittingPayloadIsExactlyThreeQuartersOfTheLimit()
    {
        var limit = WorkOrderCodec.MaxMessageBytes / 4 * 3;

        Assert.True(WorkOrderCodec.Fits(limit));
        Assert.False(WorkOrderCodec.Fits(limit + 1));
    }

    [Fact]
    public void FitsRejectsANegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkOrderCodec.Fits(-1));
    }

    [Fact]
    public void DecodingRejectsABodyThatIsNotBase64()
    {
        Assert.Throws<FormatException>(() => WorkOrderCodec.Decode("not base64 at all!!"));
    }

    [Fact]
    public void DecodingRejectsBase64ThatIsNotAWorkOrder()
    {
        var body = Convert.ToBase64String("this is not json"u8.ToArray());

        Assert.ThrowsAny<JsonException>(() => WorkOrderCodec.Decode(body));
    }

    [Fact]
    public void DecodingRejectsAnEmptyBody()
    {
        Assert.ThrowsAny<ArgumentException>(() => WorkOrderCodec.Decode("   "));
    }
}
