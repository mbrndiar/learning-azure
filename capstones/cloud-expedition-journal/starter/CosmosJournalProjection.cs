using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>The document shape the journal container stores.</summary>
/// <remarks>
/// A boundary type, like the table entity. Cosmos requires a lowercase <c>id</c>
/// and owns <c>_etag</c>, and neither belongs in a domain record that is also
/// used by an in-memory projection with no service behind it.
/// </remarks>
public sealed class JournalDocument
{
    /// <summary>The document id, unique within the partition.</summary>
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The station; the partition key path is <c>/stationId</c>.</summary>
    [JsonProperty("stationId")]
    public string StationId { get; set; } = string.Empty;

    /// <summary>The observation this entry journals.</summary>
    [JsonProperty("observationId")]
    public string ObservationId { get; set; } = string.Empty;

    /// <summary>The stream partition the entry was read from.</summary>
    [JsonProperty("partitionId")]
    public string PartitionId { get; set; } = string.Empty;

    /// <summary>The stream position the entry was projected from.</summary>
    [JsonProperty("sequenceNumber")]
    public long SequenceNumber { get; set; }

    /// <summary>The observed temperature.</summary>
    [JsonProperty("celsius")]
    public double Celsius { get; set; }

    /// <summary>The blob the full report was preserved as.</summary>
    [JsonProperty("artifactName")]
    public string ArtifactName { get; set; } = string.Empty;

    /// <summary>When the reading was taken, in round-trip form.</summary>
    [JsonProperty("observedUtc")]
    public string ObservedUtc { get; set; } = string.Empty;

    /// <summary>The service-managed version, used for every conditional write.</summary>
    [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)]
    public string? ETag { get; set; }

    /// <summary>Converts a domain entry into the stored document.</summary>
    /// <param name="entry">The entry to store.</param>
    /// <returns>The document.</returns>
    public static JournalDocument From(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new JournalDocument
        {
            Id = entry.Id,
            StationId = entry.StationId,
            ObservationId = entry.ObservationId,
            PartitionId = entry.PartitionId,
            SequenceNumber = entry.SequenceNumber,
            Celsius = entry.Celsius,
            ArtifactName = entry.ArtifactName,
            ObservedUtc = ExpeditionNaming.FormatInstant(entry.ObservedUtc),
        };
    }

    /// <summary>Converts the stored document back into a domain entry.</summary>
    /// <returns>The entry.</returns>
    public JournalEntry ToEntry() => new(
        Id,
        StationId,
        ObservationId,
        PartitionId,
        SequenceNumber,
        Celsius,
        ArtifactName,
        DateTimeOffset.TryParse(ObservedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var observed)
            ? observed
            : default,
        ETag ?? string.Empty);
}

/// <summary>Turns a Cosmos failure into an answer the application can act on.</summary>
/// <remarks>
/// <para>
/// Milestone 4. Cosmos reports several ordinary, expected outcomes as exceptions,
/// and the difference between a usable application and a fragile one is largely
/// which of them it re-reads as data:
/// </para>
/// <list type="table">
/// <item><term>404</term><description>The document is not there. An answer, not a fault.</description></item>
/// <item><term>409</term><description>Something else created it first.</description></item>
/// <item><term>412</term><description>The caller's version is stale.</description></item>
/// <item><term>429</term><description>Rate limited, with a wait time attached.</description></item>
/// </list>
/// <para>
/// Everything else — 401, 403, 503 — is a real failure and must keep travelling.
/// A catch-all that swallows them turns a missing role assignment into an empty
/// journal.
/// </para>
/// </remarks>
public static class CosmosOutcomes
{
    /// <summary>Reads a failure as a throttle, or <c>null</c> when it is not one.</summary>
    /// <param name="error">The failure to classify.</param>
    /// <returns>A throttle carrying the service's wait and charge, or <c>null</c>.</returns>
    public static ThrottledException? AsThrottle(CosmosException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // GAP 22 — 429 is rate limiting, not an outage.
        //
        // It is the service enforcing the throughput that was provisioned, and it
        // arrives with the wait it wants. Retrying immediately makes the pressure
        // worse; failing the operation makes a healthy, correctly sized workload
        // look broken.
        // Only HttpStatusCode.TooManyRequests is a throttle; everything else must
        // keep travelling. Build a ThrottledException carrying error.RetryAfter and
        // error.RequestCharge — a refused request is still billed. RetryAfter is
        // absent on some responses, so apply a small floor rather than retrying
        // instantly against a service already asking for room.
        throw new NotImplementedException(
            "GAP 22: classify a Cosmos failure as a throttle, or not at all. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-4-the-journal-projection.");
    }
}

/// <summary>Implements <see cref="IJournalProjection"/> over a real Cosmos container.</summary>
/// <param name="container">The journal container, partitioned on <c>/stationId</c>.</param>
public sealed class CosmosJournalProjection(Container container) : IJournalProjection
{
    /// <summary>The partition key path the container must be created with.</summary>
    public const string PartitionKeyPath = "/stationId";

    /// <summary>The journal container.</summary>
    public Container Container { get; } = container ?? throw new ArgumentNullException(nameof(container));

    /// <inheritdoc />
    public Task<ProjectionResult> WriteAsync(
        JournalEntry entry,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var document = JournalDocument.From(entry);

        // GAP 23 — Name the partition key on every operation.
        //
        // Cosmos will accept a write without one and route it by reading the
        // document, but a read or delete without one fans out across every
        // physical partition. The cost of a point operation is then set by the
        // size of the container rather than by the size of the answer.
        // A null ifMatch means "this caller believes the document does not exist":
        // CreateItemAsync, whose 409 Conflict is the answer that somebody else got
        // there first. A non-null ifMatch is a conditional ReplaceItemAsync through
        // ItemRequestOptions.IfMatchEtag, whose 412 PreconditionFailed means the
        // caller's version is stale. Map those to Superseded and Stale, let
        // CosmosOutcomes.AsThrottle re-raise a 429 as a ThrottledException, and let
        // 401, 403, and 503 keep travelling — a catch-all that swallows them turns
        // a missing role assignment into an empty journal.
        //
        // Carry ETag and RequestCharge out on every path, including the failures.
        throw new NotImplementedException(
            "GAP 23: write one journal entry under the right precondition. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-4-the-journal-projection.");
    }

    /// <inheritdoc />
    public async Task<JournalEntry?> TryReadAsync(string stationId, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            var response = await Container
                .ReadItemAsync<JournalDocument>(id, new PartitionKey(stationId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var entry = response.Resource.ToEntry();
            return entry with { ETag = response.ETag ?? entry.ETag };
        }
        catch (CosmosException error) when (CosmosOutcomes.AsThrottle(error) is { } throttle)
        {
            throw throttle;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<JournalPage> QueryStationAsync(
        string stationId,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.stationId = @stationId ORDER BY c.sequenceNumber");
        query.WithParameter("@stationId", stationId);

        using var iterator = Container.GetItemQueryIterator<JournalDocument>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                // Scoping the query to one partition key is what keeps it a
                // single-partition query. Without it the same SQL is a fan-out
                // that happens to filter afterwards.
                PartitionKey = new PartitionKey(stationId),
                MaxItemCount = pageSize,
            });

        if (!iterator.HasMoreResults)
        {
            return new JournalPage([], null, 0);
        }

        try
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

            return new JournalPage(
                [.. response.Select(document => document.ToEntry())],
                response.ContinuationToken,
                response.RequestCharge);
        }
        catch (CosmosException error) when (CosmosOutcomes.AsThrottle(error) is { } throttle)
        {
            throw throttle;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string stationId, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            await Container
                .DeleteItemAsync<JournalDocument>(id, new PartitionKey(stationId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
