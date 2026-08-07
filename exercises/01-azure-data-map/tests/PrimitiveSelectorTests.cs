using LearningAzure.Exercises.DataMap;

namespace LearningAzure.Exercises.DataMap.Tests;

/// <summary>Judges routing decisions against expedition workloads.</summary>
/// <remarks>
/// Every fixture is a workload the Cloud Expedition Field Journal actually has.
/// The assertions cover the chosen primitive, the option it was chosen over, the
/// deciding factor, the claim-check boundary, and rule precedence — because a
/// selector that gets the right answer for the wrong reason gets the next
/// workload wrong.
/// </remarks>
public sealed class PrimitiveSelectorTests
{
    private static Workload Workload(
        string name,
        long bytes,
        bool replayFanOut = false,
        bool singleWorker = false,
        bool nonKeyQueries = false,
        bool knownKeyLookups = false) =>
        new(name, bytes, replayFanOut, singleWorker, nonKeyQueries, knownKeyLookups);

    [Fact]
    public void Telemetry_read_by_independent_consumers_becomes_an_event_stream()
    {
        var decision = PrimitiveSelector.Select(
            Workload("sensor telemetry", 512, replayFanOut: true));

        Assert.Equal(Primitive.EventStream, decision.Chosen);
        Assert.Equal(Primitive.Queue, decision.RunnerUp);
        Assert.Equal(DecidingFactor.ReplayForIndependentConsumers, decision.Factor);
        Assert.False(decision.RequiresClaimCheck);
    }

    [Fact]
    public void Work_handed_to_one_worker_becomes_a_queue()
    {
        var decision = PrimitiveSelector.Select(
            Workload("artifact processing work order", 240, singleWorker: true));

        Assert.Equal(Primitive.Queue, decision.Chosen);
        Assert.Equal(Primitive.EventStream, decision.RunnerUp);
        Assert.Equal(DecidingFactor.CompetingConsumerHandoff, decision.Factor);
        Assert.False(decision.RequiresClaimCheck);
    }

    [Fact]
    public void Queries_on_non_key_fields_become_documents()
    {
        var decision = PrimitiveSelector.Select(
            Workload("expedition catalog search", 4_096, nonKeyQueries: true));

        Assert.Equal(Primitive.Document, decision.Chosen);
        Assert.Equal(Primitive.Table, decision.RunnerUp);
        Assert.Equal(DecidingFactor.ServerSideQueryOnNonKeyFields, decision.Factor);
    }

    [Fact]
    public void Lookups_by_known_key_become_table_entities()
    {
        var decision = PrimitiveSelector.Select(
            Workload("station status", 900, knownKeyLookups: true));

        Assert.Equal(Primitive.Table, decision.Chosen);
        Assert.Equal(Primitive.Document, decision.RunnerUp);
        Assert.Equal(DecidingFactor.PointLookupByKey, decision.Factor);
        Assert.False(decision.RequiresClaimCheck);
    }

    [Fact]
    public void Large_opaque_payloads_become_blobs()
    {
        var decision = PrimitiveSelector.Select(Workload("station photograph", 4_404_019));

        Assert.Equal(Primitive.Blob, decision.Chosen);
        Assert.Equal(Primitive.Document, decision.RunnerUp);
        Assert.Equal(DecidingFactor.OpaquePayloadSize, decision.Factor);
        Assert.False(decision.RequiresClaimCheck);
    }

    /// <summary>
    /// Rule order is a real claim, not an implementation detail: a workload can
    /// look like both a stream and a queue, and the replay requirement is the one
    /// a queue physically cannot satisfy.
    /// </summary>
    [Fact]
    public void Replay_beats_single_worker_handoff_when_a_workload_looks_like_both()
    {
        var decision = PrimitiveSelector.Select(
            Workload("telemetry with a single archiver", 1_024, replayFanOut: true, singleWorker: true));

        Assert.Equal(Primitive.EventStream, decision.Chosen);
        Assert.Equal(DecidingFactor.ReplayForIndependentConsumers, decision.Factor);
    }

    /// <summary>
    /// A known key is not enough on its own. An item larger than a table entity
    /// has to fall through to a blob, whatever the lookup pattern is.
    /// </summary>
    [Fact]
    public void A_known_key_does_not_rescue_an_item_larger_than_a_table_entity()
    {
        var decision = PrimitiveSelector.Select(
            Workload("scanned survey sheet", 2_000_000, knownKeyLookups: true));

        Assert.Equal(Primitive.Blob, decision.Chosen);
        Assert.Equal(DecidingFactor.OpaquePayloadSize, decision.Factor);
    }

    [Fact]
    public void A_payload_at_the_queue_ceiling_still_fits_in_a_message()
    {
        var decision = PrimitiveSelector.Select(
            Workload("batched work order", PrimitiveCharacteristics.MaxQueueMessagePayloadBytes, singleWorker: true));

        Assert.Equal(Primitive.Queue, decision.Chosen);
        Assert.False(decision.RequiresClaimCheck);
    }

    [Fact]
    public void A_payload_one_byte_over_the_queue_ceiling_needs_a_claim_check()
    {
        var decision = PrimitiveSelector.Select(
            Workload(
                "oversized work order",
                PrimitiveCharacteristics.MaxQueueMessagePayloadBytes + 1,
                singleWorker: true));

        Assert.Equal(Primitive.Queue, decision.Chosen);
        Assert.True(
            decision.RequiresClaimCheck,
            "A payload over the message ceiling has to live in a blob, with only its name in the message.");
    }

    [Fact]
    public void An_event_larger_than_the_stream_ceiling_needs_a_claim_check()
    {
        var decision = PrimitiveSelector.Select(
            Workload(
                "burst telemetry batch",
                PrimitiveCharacteristics.MaxEventBytes + 1,
                replayFanOut: true));

        Assert.Equal(Primitive.EventStream, decision.Chosen);
        Assert.True(decision.RequiresClaimCheck);
    }

    /// <summary>
    /// The outcome is to justify a choice *against the adjacent service*, so the
    /// justification has to name the runner-up and say something. A decision that
    /// cannot be defended is a guess with a type.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFixture))]
    public void Every_decision_justifies_itself_against_its_runner_up(Workload workload)
    {
        var decision = PrimitiveSelector.Select(workload);

        Assert.False(string.IsNullOrWhiteSpace(decision.Justification));
        Assert.Contains(
            decision.RunnerUp.ToString(),
            decision.Justification,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            decision.Justification.Length >= 40,
            $"'{decision.Justification}' does not explain why {decision.RunnerUp} lost.");
        Assert.NotEqual(decision.Chosen, decision.RunnerUp);
    }

    [Fact]
    public void A_missing_workload_is_rejected_before_any_rule_runs()
    {
        Assert.Throws<ArgumentNullException>(() => PrimitiveSelector.Select(null!));
    }

    /// <summary>One fixture per routing rule, reused by the justification contract.</summary>
    public static TheoryData<Workload> EveryFixture =>
    [
        Workload("sensor telemetry", 512, replayFanOut: true),
        Workload("artifact processing work order", 240, singleWorker: true),
        Workload("expedition catalog search", 4_096, nonKeyQueries: true),
        Workload("station status", 900, knownKeyLookups: true),
        Workload("station photograph", 4_404_019),
    ];
}
