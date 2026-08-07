namespace LearningAzure.Exercises.SdkFoundations;

/// <summary>Where a client is pointed: the local emulator, or a real Azure account.</summary>
public enum DeploymentEnvironment
{
    /// <summary>Azurite, running from <c>compose.yaml</c> on this machine.</summary>
    LocalEmulator,

    /// <summary>A real Azure Storage account reached over the public endpoint.</summary>
    LiveAzure,
}

/// <summary>How a client proves who it is.</summary>
public enum AuthenticationMode
{
    /// <summary>The emulator's well-known, public development key. Local only.</summary>
    EmulatorSharedKey,

    /// <summary>Microsoft Entra ID, resolved through <c>DefaultAzureCredential</c>.</summary>
    EntraDefaultAzureCredential,
}

/// <summary>Everything needed to construct a client, and no secret value.</summary>
/// <param name="BlobServiceUri">The endpoint the client will talk to.</param>
/// <param name="Authentication">How the client will prove its identity.</param>
/// <param name="SecretVariableName">
/// The name of the environment variable holding the emulator connection string,
/// or <c>null</c> for a live deployment. This is a *name*, never a value: a
/// resolver that returns the secret itself has already put it somewhere it can
/// be logged.
/// </param>
public sealed record StorageConnection(
    Uri BlobServiceUri,
    AuthenticationMode Authentication,
    string? SecretVariableName);

/// <summary>One field station's directory record.</summary>
/// <param name="StationId">Stable station identifier, used as the blob name.</param>
/// <param name="DisplayName">Human-readable station name.</param>
/// <param name="Region">Azure region the station's data is stored in.</param>
public sealed record StationRecord(string StationId, string DisplayName, string Region);

/// <summary>
/// The application's own view of station storage — the seam that keeps every
/// later module testable without a live service.
/// </summary>
/// <remarks>
/// No Azure SDK type appears in this contract. That is the entire point: callers
/// depend on this interface, so they can be verified against a fake, while the
/// adapter that implements it is verified against a scripted transport.
/// </remarks>
public interface IStationDirectory
{
    /// <summary>Reads one station record.</summary>
    /// <param name="stationId">The station to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <c>null</c> when the station has no record yet.</returns>
    Task<StationRecord?> TryGetAsync(string stationId, CancellationToken cancellationToken);

    /// <summary>Writes one station record, replacing any existing one.</summary>
    /// <param name="record">The record to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveAsync(StationRecord record, CancellationToken cancellationToken);
}
