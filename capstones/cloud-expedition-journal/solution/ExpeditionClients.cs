using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Cosmos;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>Where one expedition run is executing.</summary>
public enum ExpeditionEnvironment
{
    /// <summary>Azurite, the Event Hubs emulator, and the Cosmos DB emulator.</summary>
    Emulator,

    /// <summary>Real Azure resources, reached with Microsoft Entra ID.</summary>
    LiveAzure,
}

/// <summary>One data-plane role a live run needs, and why.</summary>
/// <param name="Service">The service the role is scoped to.</param>
/// <param name="RoleName">The built-in role name, or the Cosmos data-plane role.</param>
/// <param name="Reason">The operation that would fail without it.</param>
public sealed record DataPlaneRole(string Service, string RoleName, string Reason);

/// <summary>The clients one expedition run needs.</summary>
/// <param name="Journal">The blob container holding reports and checkpoints.</param>
/// <param name="Work">The dispatch queue.</param>
/// <param name="Poison">The quarantine queue.</param>
/// <param name="Stations">The station state table.</param>
/// <param name="Producer">The telemetry producer.</param>
/// <param name="Consumer">The telemetry consumer.</param>
/// <param name="Cosmos">The Cosmos client owning the journal container.</param>
public sealed record ExpeditionClients(
    BlobContainerClient Journal,
    QueueClient Work,
    QueueClient Poison,
    TableClient Stations,
    EventHubProducerClient Producer,
    EventHubConsumerClient Consumer,
    CosmosClient Cosmos) : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Producer.DisposeAsync().ConfigureAwait(false);
        await Consumer.DisposeAsync().ConfigureAwait(false);
        Cosmos.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Builds the Azure clients for one expedition run.</summary>
/// <remarks>
/// <para>
/// Milestone 5. Authentication is decided here, once, and differently for the
/// emulator and for Azure. Emulators have no Entra ID, so the emulator path uses
/// well-known development credentials read from environment variables rather than
/// embedded in source. A live run authenticates with
/// <see cref="DefaultAzureCredential"/> and no key of any kind.
/// </para>
/// <para>
/// A live run that finds a key in the environment fails instead of preferring it.
/// Silently falling back to a key is how a course-shaped deployment ends up with
/// a credential in an environment variable and no audit trail of who used it.
/// </para>
/// </remarks>
public static class ExpeditionEnvironmentFactory
{
    /// <summary>The variable Azurite's development connection string is read from.</summary>
    public const string AzuriteConnectionVariable = "AZURITE_CONNECTION_STRING";

    /// <summary>The variable the Event Hubs emulator connection string is read from.</summary>
    public const string EventHubsEmulatorVariable = "EVENTHUBS_EMULATOR_CONNECTION_STRING";

    /// <summary>The variable the Cosmos emulator gateway address is read from.</summary>
    public const string CosmosEmulatorEndpointVariable = "COSMOS_EMULATOR_ENDPOINT";

    /// <summary>The variable the Cosmos emulator's well-known development key is read from.</summary>
    public const string CosmosEmulatorKeyVariable = "COSMOS_EMULATOR_KEY";

    /// <summary>The variable naming the live storage account.</summary>
    public const string StorageAccountVariable = "EXPEDITION_STORAGE_ACCOUNT";

    /// <summary>The variable naming the live Event Hubs namespace.</summary>
    public const string EventHubsNamespaceVariable = "EXPEDITION_EVENTHUBS_NAMESPACE";

    /// <summary>The variable naming the live Cosmos account endpoint.</summary>
    public const string CosmosEndpointVariable = "EXPEDITION_COSMOS_ENDPOINT";

    /// <summary>The container holding reports and checkpoints.</summary>
    public const string JournalContainerName = "expedition-journal";

    /// <summary>The dispatch queue name.</summary>
    public const string WorkQueueName = "journal-work";

    /// <summary>The quarantine queue name.</summary>
    public const string PoisonQueueName = "journal-work-poison";

    /// <summary>The station state table name.</summary>
    public const string StationTableName = "expeditionstations";

    /// <summary>The event hub telemetry is published to.</summary>
    public const string EventHubName = "telemetry";

    /// <summary>The consumer group the journal reads with.</summary>
    public const string ConsumerGroup = "field-journal";

    /// <summary>The Cosmos database name.</summary>
    public const string DatabaseName = "expedition";

    /// <summary>The Cosmos container name.</summary>
    public const string ContainerName = "journal";

    private static readonly string[] SecretMarkers =
    [
        "AccountKey",
        "ACCOUNT_KEY",
        "SharedAccessKey",
        "SAS_TOKEN",
        "SHARED_KEY",
        "CONNECTION_STRING",
        "PRIMARY_KEY",
    ];

    /// <summary>The data-plane roles a live run needs, and nothing wider.</summary>
    /// <remarks>
    /// Every entry is a data-plane role. None of them is Contributor, and none of
    /// them is scoped above the individual resource: an identity that can read
    /// telemetry has no reason to be able to delete the namespace that carries it.
    /// </remarks>
    public static IReadOnlyList<DataPlaneRole> RequiredRoles { get; } =
    [
        new("Storage", "Storage Blob Data Contributor", "Create reports and write checkpoint blobs under a lease."),
        new("Storage", "Storage Queue Data Contributor", "Send, receive, and delete artifact work."),
        new("Storage", "Storage Table Data Contributor", "Insert and conditionally replace station rows."),
        new("Event Hubs", "Azure Event Hubs Data Sender", "Publish telemetry batches."),
        new("Event Hubs", "Azure Event Hubs Data Receiver", "Read partitions from a checkpointed position."),
        new("Cosmos DB", "Cosmos DB Built-in Data Contributor", "Write and query journal documents."),
    ];

    /// <summary>Builds the clients for <paramref name="environment"/>.</summary>
    /// <param name="environment">Emulator or live Azure.</param>
    /// <param name="variables">The ambient environment variables.</param>
    /// <returns>The clients one run needs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">A required variable is missing, or a live run was handed a key.</exception>
    public static ExpeditionClients Create(
        ExpeditionEnvironment environment,
        IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        return environment switch
        {
            ExpeditionEnvironment.Emulator => CreateEmulatorClients(variables),
            ExpeditionEnvironment.LiveAzure => CreateLiveClients(variables),
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown environment."),
        };
    }

    /// <summary>Fails a live run that carries any key, connection string, or SAS token.</summary>
    /// <param name="variables">The ambient environment variables.</param>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">A secret-bearing variable is present.</exception>
    public static void RejectAmbientSecrets(IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        // GAP 24 — Refuse the key, do not merely prefer the credential.
        //
        // "Use Entra ID if it works, otherwise the key" is a fallback that
        // succeeds on the day the role assignment is missing, which is the exact
        // day it should fail. Refusing outright is what makes the identity path
        // the only path.
        foreach (var name in variables.Keys)
        {
            if (!SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"'{name}' supplies a key, connection string, or SAS token, but a live expedition "
                + "authenticates with Microsoft Entra ID through DefaultAzureCredential. Remove the variable "
                + "and grant the identity the data-plane roles listed in "
                + "capstones/cloud-expedition-journal/README.md#run-it-against-live-azure instead.");
        }
    }

    /// <summary>Builds blob client options with a bounded retry budget.</summary>
    /// <param name="maxRetries">Retries after the first attempt.</param>
    /// <returns>Options a container client can be constructed with.</returns>
    public static BlobClientOptions BlobOptions(int maxRetries = 3)
    {
        var options = new BlobClientOptions();
        Configure(options.Retry, maxRetries);
        return options;
    }

    /// <summary>Builds queue client options with a bounded retry budget.</summary>
    /// <param name="maxRetries">Retries after the first attempt.</param>
    /// <returns>Options a queue client can be constructed with.</returns>
    public static QueueClientOptions QueueOptions(int maxRetries = 3)
    {
        var options = new QueueClientOptions();
        Configure(options.Retry, maxRetries);
        return options;
    }

    /// <summary>Builds table client options with a bounded retry budget.</summary>
    /// <param name="maxRetries">Retries after the first attempt.</param>
    /// <returns>Options a table client can be constructed with.</returns>
    public static TableClientOptions TableOptions(int maxRetries = 3)
    {
        var options = new TableClientOptions();
        Configure(options.Retry, maxRetries);
        return options;
    }

    /// <summary>Builds Cosmos options that surface throttling instead of hiding it.</summary>
    /// <param name="allowEmulatorCertificate">Trust the emulator's self-signed certificate.</param>
    /// <returns>Options a Cosmos client can be constructed with.</returns>
    public static CosmosClientOptions CosmosOptions(bool allowEmulatorCertificate)
    {
        var options = new CosmosClientOptions
        {
            // Gateway mode is what makes the emulator reachable through a single
            // published port; direct mode needs a range the container does not map.
            ConnectionMode = ConnectionMode.Gateway,

            // The SDK retries 429 internally by default. The capstone owns that
            // policy, so the built-in budget is set to zero and the throttle
            // reaches JournalProjector where the cost of it can be counted.
            MaxRetryAttemptsOnRateLimitedRequests = 0,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.Zero,
            RequestTimeout = TimeSpan.FromSeconds(30),
        };

        if (allowEmulatorCertificate)
        {
            // Emulator only. A live client that skips certificate validation has
            // no transport security worth the name.
            options.ServerCertificateCustomValidationCallback = static (_, _, _) => true;
        }

        return options;
    }

    private static ExpeditionClients CreateEmulatorClients(IReadOnlyDictionary<string, string?> variables)
    {
        var storage = RequireVariable(variables, AzuriteConnectionVariable, "run-it-locally");
        var eventHubs = RequireVariable(variables, EventHubsEmulatorVariable, "run-it-locally");
        var cosmosEndpoint = RequireVariable(variables, CosmosEmulatorEndpointVariable, "run-it-locally");
        var cosmosKey = RequireVariable(variables, CosmosEmulatorKeyVariable, "run-it-locally");

        return new ExpeditionClients(
            new BlobContainerClient(storage, JournalContainerName, BlobOptions()),
            new QueueClient(storage, WorkQueueName, QueueOptions()),
            new QueueClient(storage, PoisonQueueName, QueueOptions()),
            new TableClient(storage, StationTableName, TableOptions()),
            new EventHubProducerClient(eventHubs, EventHubName),
            new EventHubConsumerClient(ConsumerGroup, eventHubs, EventHubName),
            new CosmosClient(cosmosEndpoint, cosmosKey, CosmosOptions(allowEmulatorCertificate: true)));
    }

    private static ExpeditionClients CreateLiveClients(IReadOnlyDictionary<string, string?> variables)
    {
        var account = RequireVariable(variables, StorageAccountVariable, "run-it-against-live-azure");
        var eventHubsNamespace = RequireVariable(variables, EventHubsNamespaceVariable, "run-it-against-live-azure");
        var cosmosEndpoint = RequireVariable(variables, CosmosEndpointVariable, "run-it-against-live-azure");

        RejectAmbientSecrets(variables);

        // One credential instance, shared by every client, so the token cache is
        // shared too and a run does not fetch six tokens for one identity.
        var credential = new DefaultAzureCredential();
        var fullyQualifiedNamespace = $"{eventHubsNamespace}.servicebus.windows.net";

        return new ExpeditionClients(
            new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), credential, BlobOptions())
                .GetBlobContainerClient(JournalContainerName),
            new QueueClient(
                new Uri($"https://{account}.queue.core.windows.net/{WorkQueueName}"), credential, QueueOptions()),
            new QueueClient(
                new Uri($"https://{account}.queue.core.windows.net/{PoisonQueueName}"), credential, QueueOptions()),
            new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), credential, TableOptions())
                .GetTableClient(StationTableName),
            new EventHubProducerClient(fullyQualifiedNamespace, EventHubName, credential),
            new EventHubConsumerClient(ConsumerGroup, fullyQualifiedNamespace, EventHubName, credential),
            new CosmosClient(cosmosEndpoint, credential, CosmosOptions(allowEmulatorCertificate: false)));
    }

    private static void Configure(RetryOptions retry, int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        retry.MaxRetries = maxRetries;
        retry.Mode = RetryMode.Exponential;
        retry.Delay = TimeSpan.FromMilliseconds(200);
        retry.MaxDelay = TimeSpan.FromSeconds(4);

        // A network timeout is what stops one stalled connection from consuming
        // the caller's entire budget on an attempt that will never answer.
        retry.NetworkTimeout = TimeSpan.FromSeconds(30);
    }

    private static string RequireVariable(
        IReadOnlyDictionary<string, string?> variables,
        string name,
        string section)
    {
        if (!variables.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{name} is not set. See capstones/cloud-expedition-journal/README.md#{section} for the "
                + "exact commands that set it.");
        }

        return value;
    }
}
