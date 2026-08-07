using Azure.Core;
using Azure.Storage.Blobs;

namespace LearningAzure.Exercises.SdkFoundations;

/// <summary>Decides where a client points and how it authenticates.</summary>
/// <remarks>
/// Resolution is a pure function of the target environment and the ambient
/// environment variables, so it can be tested exhaustively without a service and
/// without ever putting a secret in source.
/// </remarks>
public static class StorageConnectionResolver
{
    /// <summary>The variable Azurite's connection string is read from.</summary>
    public const string EmulatorSecretVariable = "AZURITE_CONNECTION_STRING";

    /// <summary>Azurite's fixed blob endpoint for the well-known development account.</summary>
    public static Uri EmulatorBlobServiceUri { get; } = new("http://127.0.0.1:10000/devstoreaccount1");

    /// <summary>Resolves the connection for <paramref name="environment"/>.</summary>
    /// <param name="environment">Emulator or live Azure.</param>
    /// <param name="accountName">Storage account name; ignored for the emulator.</param>
    /// <param name="variables">The ambient environment variables.</param>
    /// <returns>The endpoint, the authentication mode, and the secret's variable name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The emulator secret is missing, or a live deployment was handed a shared key.
    /// </exception>
    public static StorageConnection Resolve(
        DeploymentEnvironment environment,
        string accountName,
        IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        switch (environment)
        {
            case DeploymentEnvironment.LiveAzure:
                ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
                RejectSharedKeys(variables);
                return new StorageConnection(
                    new Uri($"https://{accountName}.blob.core.windows.net/"),
                    AuthenticationMode.EntraDefaultAzureCredential,
                    SecretVariableName: null);

            case DeploymentEnvironment.LocalEmulator:
                if (!variables.TryGetValue(EmulatorSecretVariable, out var secret)
                    || string.IsNullOrWhiteSpace(secret))
                {
                    throw new InvalidOperationException(
                        $"{EmulatorSecretVariable} is not set. Start Azurite with "
                        + "'docker compose up -d azurite' and export the development connection "
                        + "string documented in docs/SETUP.md.");
                }

                // The resolved record deliberately carries the variable *name*.
                // Returning the value would put a secret into every log line and
                // every test failure message that prints the connection.
                return new StorageConnection(
                    EmulatorBlobServiceUri,
                    AuthenticationMode.EmulatorSharedKey,
                    EmulatorSecretVariable);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(environment),
                    environment,
                    "Unknown deployment environment.");
        }
    }

    /// <summary>Builds client options with a bounded retry budget and a real timeout.</summary>
    /// <param name="maxRetries">Retries after the first attempt; 0 disables retrying.</param>
    /// <param name="delay">Base delay between retries; pass <see cref="TimeSpan.Zero"/> in tests.</param>
    /// <returns>Options a <see cref="BlobContainerClient"/> can be constructed with.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetries"/> is negative.</exception>
    public static BlobClientOptions CreateClientOptions(int maxRetries, TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        var options = new BlobClientOptions();
        options.Retry.MaxRetries = maxRetries;
        options.Retry.Mode = RetryMode.Exponential;
        options.Retry.Delay = delay;
        options.Retry.MaxDelay = delay * 8;

        // A network timeout is what stops a stalled connection from consuming the
        // caller's whole budget on one attempt that will never answer.
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        return options;
    }

    /// <summary>
    /// Fails a live deployment that carries a shared key, rather than quietly
    /// preferring it over Entra ID.
    /// </summary>
    private static void RejectSharedKeys(IReadOnlyDictionary<string, string?> variables)
    {
        foreach (var name in variables.Keys)
        {
            var carriesKey = name.Contains("AccountKey", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, EmulatorSecretVariable, StringComparison.OrdinalIgnoreCase);
            if (carriesKey)
            {
                throw new InvalidOperationException(
                    $"'{name}' supplies a storage shared key, but a live deployment authenticates "
                    + "with Microsoft Entra ID through DefaultAzureCredential. Remove the variable "
                    + "and grant the identity a data-plane role instead.");
            }
        }
    }
}
