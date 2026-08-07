using System.Text.Json;
using Azure;
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
    public async Task<StationRecord?> TryGetAsync(string stationId, CancellationToken cancellationToken)
    {
        var blob = Container.GetBlobClient(BlobName(stationId));

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return response.Value.Content.ToObjectFromJson<StationRecord>(SerializerOptions);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            // 404 is the answer to "does this station have a record yet?", not a
            // failure. It is caught by STATUS, so a 403 from a missing role
            // assignment still propagates instead of masquerading as "no data".
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(StationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var blob = Container.GetBlobClient(BlobName(record.StationId));
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);

        // The directory entry has a single writer, so last-write-wins is correct
        // here. Module 5 replaces this with a conditional write for artifacts that
        // several field uploads can touch at once.
        await blob.UploadAsync(BinaryData.FromBytes(payload), overwrite: true, cancellationToken)
            .ConfigureAwait(false);
    }
}
