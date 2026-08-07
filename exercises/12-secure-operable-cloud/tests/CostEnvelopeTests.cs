using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// Checks the two numbers a live checkpoint has to publish: what the run costs,
/// and what the same resources cost per day if nobody deletes them.
/// </summary>
public sealed class CostEnvelopeTests
{
    private static readonly BilledResource CosmosThroughput =
        new("Cosmos DB, 400 RU/s provisioned", BillingShape.Provisioned, 0.032m);

    private static readonly BilledResource EventHubsNamespace =
        new("Event Hubs, Basic tier", BillingShape.Provisioned, 0.015m);

    private static readonly BilledResource BlobStorage =
        new("Blob storage, 1 GiB", BillingShape.Storage, 0.00003m);

    private static readonly BilledResource Transactions =
        new("Storage transactions", BillingShape.Consumption, 0.40m);

    private static readonly BilledResource LogIngestion =
        new("Log Analytics ingestion", BillingShape.Consumption, 0.20m);

    [Fact]
    public void Estimate_ChargesEveryResourceForTheDurationOfTheRun()
    {
        var estimate = CostEnvelope.Estimate(
            [CosmosThroughput, Transactions],
            TimeSpan.FromHours(2),
            budgetUsd: 5m);

        Assert.Equal((0.032m + 0.40m) * 2m, estimate.RunCostUsd);
    }

    [Fact]
    public void Estimate_ChargesAPartialHourAsAPartialHour()
    {
        var estimate = CostEnvelope.Estimate([CosmosThroughput], TimeSpan.FromMinutes(30), budgetUsd: 5m);

        Assert.Equal(0.016m, estimate.RunCostUsd);
    }

    [Fact]
    public void Estimate_ExcludesConsumptionResourcesFromTheIdleCost()
    {
        // Nobody is calling the API, so there are no transactions to bill. The
        // provisioned throughput does not care.
        var estimate = CostEnvelope.Estimate(
            [CosmosThroughput, Transactions, LogIngestion],
            TimeSpan.FromHours(1),
            budgetUsd: 5m);

        Assert.Equal(0.032m * 24m, estimate.IdleCostPerDayUsd);
    }

    [Fact]
    public void Estimate_KeepsStorageInTheIdleCost()
    {
        // Bytes are billed for existing, exactly like provisioned throughput.
        var estimate = CostEnvelope.Estimate([BlobStorage, Transactions], TimeSpan.FromHours(1), budgetUsd: 5m);

        Assert.Equal(0.00003m * 24m, estimate.IdleCostPerDayUsd);
    }

    [Fact]
    public void Estimate_SumsEveryProvisionedResourceIntoTheIdleCost()
    {
        var estimate = CostEnvelope.Estimate(
            [CosmosThroughput, EventHubsNamespace, BlobStorage],
            TimeSpan.FromHours(1),
            budgetUsd: 5m);

        Assert.Equal((0.032m + 0.015m + 0.00003m) * 24m, estimate.IdleCostPerDayUsd);
    }

    [Fact]
    public void Estimate_NamesTheResourceThatDominatesTheIdleCost()
    {
        var estimate = CostEnvelope.Estimate(
            [EventHubsNamespace, CosmosThroughput, BlobStorage],
            TimeSpan.FromHours(1),
            budgetUsd: 5m);

        Assert.Equal(CosmosThroughput.Name, estimate.Dominant);
    }

    [Fact]
    public void Estimate_DoesNotNameAConsumptionResourceAsDominantJustBecauseItIsExpensive()
    {
        // The transactions line is the biggest number on the run bill and
        // contributes nothing to the bill for forgetting.
        var estimate = CostEnvelope.Estimate(
            [Transactions, CosmosThroughput],
            TimeSpan.FromHours(1),
            budgetUsd: 5m);

        Assert.Equal(CosmosThroughput.Name, estimate.Dominant);
    }

    [Fact]
    public void Estimate_SaysSoWhenNothingIsBilledForExisting()
    {
        var estimate = CostEnvelope.Estimate([Transactions, LogIngestion], TimeSpan.FromHours(1), budgetUsd: 5m);

        Assert.Equal(0m, estimate.IdleCostPerDayUsd);
        Assert.Contains("nothing", estimate.Dominant, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_BreaksATieOnNameSoTheOutputIsStable()
    {
        var first = new BilledResource("aaa", BillingShape.Provisioned, 0.01m);
        var second = new BilledResource("bbb", BillingShape.Provisioned, 0.01m);

        Assert.Equal("aaa", CostEnvelope.Estimate([second, first], TimeSpan.FromHours(1), 5m).Dominant);
        Assert.Equal("aaa", CostEnvelope.Estimate([first, second], TimeSpan.FromHours(1), 5m).Dominant);
    }

    [Fact]
    public void Estimate_TreatsARunEqualToTheBudgetAsWithinIt()
    {
        var estimate = CostEnvelope.Estimate([CosmosThroughput], TimeSpan.FromHours(1), budgetUsd: 0.032m);

        Assert.True(estimate.WithinBudget);
    }

    [Fact]
    public void Estimate_ReportsARunOverTheBudget()
    {
        var estimate = CostEnvelope.Estimate([Transactions], TimeSpan.FromHours(10), budgetUsd: 1m);

        Assert.False(estimate.WithinBudget);
    }

    [Fact]
    public void Estimate_JudgesTheBudgetAgainstTheRunNotTheIdleCost()
    {
        // A cheap run whose leftovers are expensive is still a cheap run; the
        // idle number is a separate warning, not a budget failure.
        var estimate = CostEnvelope.Estimate([CosmosThroughput], TimeSpan.FromMinutes(6), budgetUsd: 0.10m);

        Assert.True(estimate.WithinBudget);
        Assert.True(estimate.IdleCostPerDayUsd > estimate.RunCostUsd);
    }

    [Fact]
    public void Estimate_RefusesAnEmptyArchitecture()
    {
        Assert.Throws<ArgumentException>(() => CostEnvelope.Estimate([], TimeSpan.FromHours(1), 5m));
    }

    [Fact]
    public void Estimate_RefusesANonPositiveRunLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CostEnvelope.Estimate([CosmosThroughput], TimeSpan.Zero, 5m));
    }

    [Fact]
    public void Estimate_RefusesANegativeBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CostEnvelope.Estimate([CosmosThroughput], TimeSpan.FromHours(1), -1m));
    }
}
