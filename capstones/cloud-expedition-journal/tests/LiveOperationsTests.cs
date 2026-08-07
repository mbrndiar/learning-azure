namespace LearningAzure.Capstones.CloudExpeditionJournal.Tests;

/// <summary>
/// Milestone 4 — the operational boundary. Judges whether identity is the only
/// way in, whether the retry budget is bounded, and whether teardown proves it
/// finished rather than assuming it.
/// </summary>
[Trait("Milestone", "cosmos-projection")]
public sealed class LiveOperationsTests
{
    private static readonly Dictionary<string, string?> LiveVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        [ExpeditionEnvironmentFactory.StorageAccountVariable] = "stexpedition",
        [ExpeditionEnvironmentFactory.EventHubsNamespaceVariable] = "evhns-expedition",
        [ExpeditionEnvironmentFactory.CosmosEndpointVariable] = "https://cosmos-expedition.documents.azure.com:443/",
    };

    [Fact]
    public void ALiveRunRefusesAConnectionStringInsteadOfPreferringTheCredential()
    {
        // "Use Entra ID if it works, otherwise the key" succeeds on the day the
        // role assignment is missing, which is the exact day it should fail.
        var variables = new Dictionary<string, string?>(LiveVariables, StringComparer.OrdinalIgnoreCase)
        {
            ["AZURITE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExpeditionEnvironmentFactory.Create(ExpeditionEnvironment.LiveAzure, variables));

        Assert.Contains("DefaultAzureCredential", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EXPEDITION_ACCOUNT_KEY")]
    [InlineData("EVENTHUBS_SHARED_KEY")]
    [InlineData("COSMOS_PRIMARY_KEY")]
    [InlineData("STORAGE_SAS_TOKEN")]
    public void EveryShapeOfAmbientSecretIsRefused(string name)
    {
        var variables = new Dictionary<string, string?>(LiveVariables, StringComparer.OrdinalIgnoreCase)
        {
            [name] = "redacted",
        };

        Assert.Throws<InvalidOperationException>(() =>
            ExpeditionEnvironmentFactory.RejectAmbientSecrets(variables));
    }

    [Fact]
    public void AnEnvironmentWithNoSecretsIsAccepted() =>
        ExpeditionEnvironmentFactory.RejectAmbientSecrets(LiveVariables);

    [Fact]
    public void AMissingVariableFailsWithACommandTheLearnerCanRun()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExpeditionEnvironmentFactory.Create(
                ExpeditionEnvironment.Emulator,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));

        Assert.Contains("README.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRequiredRoleIsADataPlaneRoleAndNoneIsContributor()
    {
        // An identity that can read telemetry has no reason to be able to delete
        // the namespace that carries it.
        Assert.NotEmpty(ExpeditionEnvironmentFactory.RequiredRoles);
        Assert.All(ExpeditionEnvironmentFactory.RequiredRoles, role =>
        {
            Assert.Contains("Data", role.RoleName, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(role.Reason));
        });

        Assert.DoesNotContain(
            ExpeditionEnvironmentFactory.RequiredRoles,
            role => role.RoleName is "Contributor" or "Owner");
    }

    [Fact]
    public void AllFiveServicesAreCoveredByTheRoleCatalogue()
    {
        var services = ExpeditionEnvironmentFactory.RequiredRoles
            .Select(role => role.Service)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("Storage", services);
        Assert.Contains("Event Hubs", services);
        Assert.Contains("Cosmos DB", services);
    }

    [Fact]
    public void RetryBudgetsAreBoundedRatherThanInfinite()
    {
        // An unbounded client retry hides a failing dependency until the request
        // that is waiting on it times out somewhere far less informative.
        Assert.Equal(3, ExpeditionEnvironmentFactory.BlobOptions().Retry.MaxRetries);
        Assert.Equal(3, ExpeditionEnvironmentFactory.QueueOptions().Retry.MaxRetries);
        Assert.Equal(3, ExpeditionEnvironmentFactory.TableOptions().Retry.MaxRetries);
        Assert.True(ExpeditionEnvironmentFactory.BlobOptions().Retry.NetworkTimeout > TimeSpan.Zero);
    }

    [Fact]
    public void TheCosmosClientLetsThrottlingReachTheApplication()
    {
        // The SDK would retry 429 silently. The capstone owns that policy, so the
        // throttle has to arrive where its cost can be counted.
        var options = ExpeditionEnvironmentFactory.CosmosOptions(allowEmulatorCertificate: false);

        Assert.Equal(0, options.MaxRetryAttemptsOnRateLimitedRequests);
        Assert.Equal(Microsoft.Azure.Cosmos.ConnectionMode.Gateway, options.ConnectionMode);
    }

    [Fact]
    public void OnlyTheEmulatorIsAllowedToSkipCertificateValidation()
    {
        Assert.Null(ExpeditionEnvironmentFactory
            .CosmosOptions(allowEmulatorCertificate: false)
            .ServerCertificateCustomValidationCallback);

        Assert.NotNull(ExpeditionEnvironmentFactory
            .CosmosOptions(allowEmulatorCertificate: true)
            .ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public async Task TeardownRemovesEveryServicesShareOfOneRun()
    {
        var journal = new Journal();
        await journal.PublishAsync(Fixture.Reading("obs-0001"), Fixture.Reading("obs-0002", Fixture.OtherStation));
        await journal.ProcessAsync();
        await journal.DrainAsync();
        await journal.ProjectHandledAsync();

        var report = await journal.Cleanup.RemoveAsync(
            [Fixture.Station, Fixture.OtherStation],
            pageSize: 1,
            TestContext.Current.CancellationToken);

        Assert.True(report.IsComplete);
        Assert.Equal(2, report.ReportsDeleted);
        Assert.Equal(2, report.JournalEntriesDeleted);
        Assert.True(report.CheckpointsDeleted > 0);
        Assert.Equal(0, journal.Vault.Count);
        Assert.Empty(journal.Projection.Entries);
        Assert.Empty(journal.Registry.Rows);
    }

    [Fact]
    public async Task TeardownPagesThroughTheJournalRatherThanTakingTheFirstPage()
    {
        // A one-page teardown against a paged store leaves documents behind and
        // reports success, which is the cleanup failure nobody notices until the
        // invoice arrives.
        var journal = new Journal();
        for (var index = 0; index < 5; index++)
        {
            journal.Projection.Seed(Fixture.Entry($"obs-{index:0000}", index));
        }

        var report = await journal.Cleanup.RemoveAsync(
            [Fixture.Station],
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, report.JournalEntriesDeleted);
        Assert.Empty(journal.Projection.Entries);
    }

    [Fact]
    public async Task TeardownStopsWhenItIsCancelled()
    {
        var journal = new Journal();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            journal.Cleanup.RemoveAsync([Fixture.Station], pageSize: 10, cancelled.Token));
    }

    [Fact]
    public async Task TheWholePipelineRunsEndToEndOverAllFiveServices()
    {
        // The integration this capstone exists for: telemetry in, reports and
        // checkpoints in Blob, work through the Queue, state in Table, and the
        // journal in Cosmos — with a duplicate and a poison message in the mix.
        var journal = new Journal();
        journal.Backlog.SendRaw("""{"workOrderId":"","operation":"summarize"}""");

        await journal.PublishAsync(
            Fixture.Reading("obs-0001"),
            Fixture.Reading("obs-0002", Fixture.OtherStation),
            Fixture.Reading("obs-0001"));

        var processed = await journal.ProcessAsync();
        var drained = await journal.DrainAsync();
        var projected = await journal.ProjectHandledAsync();

        Assert.Equal(3, processed.EventsHandled);
        Assert.Equal(2, journal.Vault.Count);
        Assert.Equal(2, journal.Effect.Applied.Count);
        Assert.Equal(1, drained.Quarantined);
        // The repeated reading is a genuinely later event, so the journal
        // updates the entry rather than adding a second one. Deduplication has
        // already happened where it is cheapest: the report was not rewritten and
        // no second work order was ever dispatched.
        Assert.Equal(3, projected.Written);
        Assert.Equal(2, journal.Projection.Entries.Count);
        Assert.Equal(0, journal.Backlog.Depth);
    }
}
