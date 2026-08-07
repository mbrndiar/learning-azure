using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace LearningAzure.Projects.FieldStation;

/// <summary>Where the field station is running.</summary>
public enum StationEnvironment
{
    /// <summary>Azurite, reached with the well-known development connection string.</summary>
    Emulator,

    /// <summary>A real storage account, reached with Microsoft Entra ID.</summary>
    LiveAzure,
}

/// <summary>The three clients one field-station run needs.</summary>
/// <param name="Artifacts">The container artifacts are preserved in.</param>
/// <param name="Work">The dispatch queue.</param>
/// <param name="Poison">The quarantine queue.</param>
/// <param name="Status">The station status table.</param>
public sealed record StationClients(
    BlobContainerClient Artifacts,
    QueueClient Work,
    QueueClient Poison,
    TableClient Status);

/// <summary>Builds the Azure clients for one field-station run.</summary>
/// <remarks>
/// <para>
/// Authentication is decided here, once, and it is decided differently for the
/// emulator and for Azure. Azurite has no Entra ID, so the emulator path uses its
/// well-known development shared key, read from an environment variable rather
/// than embedded in source. A live run authenticates with
/// <see cref="DefaultAzureCredential"/> and no key of any kind.
/// </para>
/// <para>
/// A live run that finds a shared key in the environment fails instead of
/// preferring it. Silently falling back to a key is how a course-shaped
/// deployment ends up with a credential in an environment variable and no
/// audit trail of who used it.
/// </para>
/// </remarks>
public static class FieldStationClients
{
    /// <summary>The variable Azurite's development connection string is read from.</summary>
    public const string EmulatorConnectionVariable = "AZURITE_CONNECTION_STRING";

    /// <summary>The variable naming the live storage account.</summary>
    public const string AccountVariable = "FIELD_STATION_ACCOUNT";

    /// <summary>The container artifacts are preserved in.</summary>
    public const string ArtifactContainerName = "expedition-artifacts";

    /// <summary>The dispatch queue name.</summary>
    public const string WorkQueueName = "artifact-work";

    /// <summary>The quarantine queue name.</summary>
    public const string PoisonQueueName = "artifact-work-poison";

    /// <summary>The station status table name.</summary>
    public const string StatusTableName = "stationstatus";

    /// <summary>Environment variable name fragments that carry a storage shared key.</summary>
    private static readonly string[] SharedKeyMarkers = ["AccountKey", "SAS_TOKEN", "SHARED_KEY"];

    /// <summary>Builds the clients for <paramref name="environment"/>.</summary>
    /// <param name="environment">Emulator or live Azure.</param>
    /// <param name="variables">The ambient environment variables.</param>
    /// <returns>The clients one run needs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The emulator connection string is missing, the account name is missing, or a
    /// live run was handed a shared key.
    /// </exception>
    public static StationClients Create(
        StationEnvironment environment,
        IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        return environment switch
        {
            StationEnvironment.Emulator => CreateEmulatorClients(RequireVariable(variables, EmulatorConnectionVariable)),
            StationEnvironment.LiveAzure => CreateLiveClients(RequireVariable(variables, AccountVariable), variables),
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown environment."),
        };
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

    /// <summary>Fails a live run that carries a storage shared key or SAS token.</summary>
    /// <param name="variables">The ambient environment variables.</param>
    /// <exception cref="InvalidOperationException">A key-bearing variable is present.</exception>
    public static void RejectSharedKeys(IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        foreach (var name in variables.Keys)
        {
            var carriesKey = SharedKeyMarkers.Any(marker =>
                name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || string.Equals(name, EmulatorConnectionVariable, StringComparison.OrdinalIgnoreCase);

            if (carriesKey)
            {
                throw new InvalidOperationException(
                    $"'{name}' supplies a storage shared key or SAS token, but a live field station "
                    + "authenticates with Microsoft Entra ID through DefaultAzureCredential. Remove the "
                    + "variable and grant the identity the data-plane roles listed in "
                    + "projects/field-station/README.md instead.");
            }
        }
    }

    private static StationClients CreateEmulatorClients(string connectionString) => new(
        new BlobContainerClient(connectionString, ArtifactContainerName, BlobOptions()),
        new QueueClient(connectionString, WorkQueueName, QueueOptions()),
        new QueueClient(connectionString, PoisonQueueName, QueueOptions()),
        new TableClient(connectionString, StatusTableName, TableOptions()));

    private static StationClients CreateLiveClients(
        string account,
        IReadOnlyDictionary<string, string?> variables)
    {
        RejectSharedKeys(variables);

        // One credential instance, shared by every client, so the token cache is
        // shared too and a run does not fetch four tokens for one identity.
        var credential = new DefaultAzureCredential();

        return new StationClients(
            new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), credential, BlobOptions())
                .GetBlobContainerClient(ArtifactContainerName),
            new QueueClient(
                new Uri($"https://{account}.queue.core.windows.net/{WorkQueueName}"), credential, QueueOptions()),
            new QueueClient(
                new Uri($"https://{account}.queue.core.windows.net/{PoisonQueueName}"), credential, QueueOptions()),
            new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), credential, TableOptions())
                .GetTableClient(StatusTableName));
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

    private static string RequireVariable(IReadOnlyDictionary<string, string?> variables, string name)
    {
        if (!variables.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{name} is not set. See projects/field-station/README.md#run-it for the exact "
                + "commands that set it.");
        }

        return value;
    }
}
