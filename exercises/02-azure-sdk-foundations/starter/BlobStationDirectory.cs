using System.Text.Json;
using Azure.Storage.Blobs;

namespace LearningAzure.Exercises.SdkFoundations;

/// <summary>Stores station records as JSON blobs.</summary>
/// <remarks>
/// This is the adapter: the only place in the application where an Azure SDK type
/// appears. Everything above it depends on <see cref="IStationDirectory"/>.
/// </remarks>
/// <param name="container">The container holding one blob per station.</param>
public sealed class BlobStationDirectory(BlobContainerClient container) : IStationDirectory
{
    /// <summary>Serializer settings, fixed so stored records are stable across runs.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>The container holding one blob per station.</summary>
    public BlobContainerClient Container { get; } =
        container ?? throw new ArgumentNullException(nameof(container));

    /// <summary>Returns the blob name a station record is stored under.</summary>
    /// <param name="stationId">The station identifier.</param>
    /// <returns><c>{stationId}.json</c>.</returns>
    public static string BlobName(string stationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        return $"{stationId}.json";
    }

    /// <inheritdoc />
    public Task<StationRecord?> TryGetAsync(string stationId, CancellationToken cancellationToken) =>
        // GAP 3 — Read a record, and classify the failure modes correctly.
        //
        //   * Download the blob and deserialize it with SerializerOptions.
        //   * A 404 is an ANSWER, not an error: the station has no record yet, so
        //     return null.
        //   * Every other RequestFailedException — 403, 409, 500 — is a real
        //     failure and must keep propagating. Catching them all and returning
        //     null turns a misconfigured permission into "no data", which is the
        //     single most expensive mistake in this module.
        //   * Do not catch OperationCanceledException. Cancellation is the
        //     caller's decision, and swallowing it produces a silent wrong answer.
        //
        // Pass cancellationToken to every awaited SDK call.
        throw new NotImplementedException(
            "GAP 3: implement BlobStationDirectory.TryGetAsync. See "
            + "lessons/02-azure-sdk-foundations/README.md#the-error-classification-seam.");

    /// <inheritdoc />
    public Task SaveAsync(StationRecord record, CancellationToken cancellationToken) =>
        // GAP 4 — Write a record.
        //
        // Serialize with SerializerOptions and upload to BlobName(record.StationId),
        // overwriting any existing record. Pass cancellationToken to the SDK call.
        //
        // (Conditional, non-overwriting writes are module 5. Here the record is a
        // directory entry with a single writer, and last write wins is correct.)
        throw new NotImplementedException(
            "GAP 4: implement BlobStationDirectory.SaveAsync. See "
            + "lessons/02-azure-sdk-foundations/README.md#the-cancellation-seam.");
}
