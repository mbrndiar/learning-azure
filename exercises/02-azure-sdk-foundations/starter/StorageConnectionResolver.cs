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
        IReadOnlyDictionary<string, string?> variables) =>
        // GAP 1 — Resolve the connection without ever returning a secret value.
        //
        // LiveAzure:
        //   * https://{accountName}.blob.core.windows.net/ with
        //     AuthenticationMode.EntraDefaultAzureCredential and no secret variable.
        //   * If any variable name contains "AccountKey" or equals
        //     EmulatorSecretVariable, throw InvalidOperationException. A live
        //     deployment carrying a shared key is a security defect, not a
        //     fallback, and it must fail before a client is built.
        //
        // LocalEmulator:
        //   * EmulatorBlobServiceUri with AuthenticationMode.EmulatorSharedKey and
        //     SecretVariableName = EmulatorSecretVariable.
        //   * If that variable is missing or blank, throw
        //     InvalidOperationException naming the variable and the command that
        //     sets it — an error that does not say what to run is a second defect.
        throw new NotImplementedException(
            "GAP 1: implement StorageConnectionResolver.Resolve. See "
            + "lessons/02-azure-sdk-foundations/README.md#the-credential-seam.");

    /// <summary>Builds client options with a bounded retry budget and a real timeout.</summary>
    /// <param name="maxRetries">Retries after the first attempt; 0 disables retrying.</param>
    /// <param name="delay">Base delay between retries; pass <see cref="TimeSpan.Zero"/> in tests.</param>
    /// <returns>Options a <see cref="BlobContainerClient"/> can be constructed with.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetries"/> is negative.</exception>
    public static BlobClientOptions CreateClientOptions(int maxRetries, TimeSpan delay) =>
        // GAP 2 — Configure the retry seam.
        //
        // Set Retry.MaxRetries, Retry.Mode = RetryMode.Exponential, Retry.Delay,
        // Retry.MaxDelay (delay * 8 is a reasonable ceiling), and
        // Retry.NetworkTimeout = 10 seconds.
        //
        // A negative maxRetries is an ArgumentOutOfRangeException. "Retry forever"
        // is not a configuration this course offers: an unbounded retry loop turns
        // a throttled dependency into an outage that never resolves.
        throw new NotImplementedException(
            "GAP 2: implement StorageConnectionResolver.CreateClientOptions. See "
            + "lessons/02-azure-sdk-foundations/README.md#the-retry-seam.");
}
