using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LearningAzure.Capstones.CloudExpeditionJournal;

/// <summary>
/// Implements <see cref="ICheckpointStore"/> as one blob per stream partition,
/// where the blob's ETag <em>is</em> the lease.
/// </summary>
/// <remarks>
/// <para>
/// Milestone 3. This is the same arrangement the Event Hubs processor library
/// uses, written out so the mechanism is visible instead of configured: a
/// processor's claim on a partition is a conditional write it keeps winning, and
/// it discovers it has lost the partition by having one refused.
/// </para>
/// <para>
/// Two conditional writes carry the whole design:
/// </para>
/// <list type="bullet">
/// <item><b>Claiming</b> a free partition is <c>If-None-Match: *</c> — the same
/// create-only-if-absent that intake uses — and taking over an expired lease is
/// <c>If-Match</c> against the ETag the reader just saw.</item>
/// <item><b>Checkpointing</b> is <c>If-Match</c> against the ETag the claim
/// returned. A processor whose lease was stolen while it was working therefore
/// cannot write a position for a partition it no longer owns.</item>
/// </list>
/// <para>
/// The lease has a duration because a processor that dies never releases
/// anything. Nothing may touch the partition until the duration elapses, which is
/// why a restart is never instant: it is always at least one lease late.
/// </para>
/// </remarks>
/// <param name="container">The container holding the checkpoint blobs.</param>
/// <param name="clock">The clock lease expiry is measured against.</param>
/// <param name="leaseDuration">How long a claim survives an owner that stopped answering.</param>
public sealed class BlobCheckpointVault(
    BlobContainerClient container,
    TimeProvider clock,
    TimeSpan leaseDuration) : ICheckpointStore
{
    /// <summary>The metadata key naming the current owner.</summary>
    public const string OwnerMetadataKey = "owner";

    /// <summary>The metadata key carrying when the claim was last renewed.</summary>
    public const string ClaimedAtMetadataKey = "claimedat";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The container holding the checkpoint blobs.</summary>
    public BlobContainerClient Container { get; } =
        container ?? throw new ArgumentNullException(nameof(container));

    /// <summary>The clock lease expiry is measured against.</summary>
    public TimeProvider Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>How long a claim survives an owner that stopped answering.</summary>
    public TimeSpan LeaseDuration { get; } = leaseDuration > TimeSpan.Zero
        ? leaseDuration
        : throw new ArgumentOutOfRangeException(
            nameof(leaseDuration),
            leaseDuration,
            "A zero lease would be taken over by every reader, which is no lease at all.");

    /// <inheritdoc />
    public Task<PartitionOwnership?> TryClaimAsync(
        string partitionId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        // Read the lease blob's properties. A 404 means the partition is free.
        //
        // GAP 19 — A free partition is claimed with If-None-Match: *.
        //
        // Two processors starting together both find no blob. Only a conditional
        // create lets the service decide which of them owns the partition; a plain
        // upload lets both believe they do. Put the owner and the claim instant in
        // blob metadata (Claim(ownerId, now) builds it), and read the 409 or 412 the
        // loser gets back as "somebody else owns it".
        //
        // When the blob exists, decide whether the lease is still live: it is
        // expired when the claim instant is missing or older than LeaseDuration.
        // Somebody else holding a live lease means this instance does not own the
        // partition — taking it anyway is how two processors end up handling the
        // same partition and checkpointing over each other.
        //
        // GAP 20 — Taking over is If-Match against the version just read.
        //
        // Without the precondition, two processors that both observe the same
        // expired lease both take it, which is precisely the race the lease was
        // supposed to settle. A 412 is the losing takeover, not an error to raise.
        //
        // Return the ETag with ToString("H"): that is the quoted form the service
        // expects back on the next conditional write.
        throw new NotImplementedException(
            "GAP 19: claim or take over one partition lease conditionally. See "
            + "capstones/cloud-expedition-journal/README.md#milestone-3-the-telemetry-pipeline.");
    }

    /// <inheritdoc />
    public async Task<Checkpoint?> TryReadCheckpointAsync(string partitionId, CancellationToken cancellationToken)
    {
        var blob = Container.GetBlobClient(ExpeditionNaming.CheckpointName(partitionId));

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<CheckpointRecord>(
                response.Value.Content.ToMemory().Span,
                Options);

            return record?.Offset is null
                ? null
                : new Checkpoint(partitionId, record.SequenceNumber, record.Offset);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
        catch (JsonException)
        {
            // A checkpoint blob that will not parse is not "position zero": that
            // reading would silently replay the whole partition. It is a
            // corrupted record, and having no checkpoint is the honest answer.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PartitionOwnership?> TryWriteCheckpointAsync(
        Checkpoint checkpoint,
        PartitionOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(ownership);

        var blob = Container.GetBlobClient(ExpeditionNaming.CheckpointName(checkpoint.PartitionId));
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new CheckpointRecord(checkpoint.SequenceNumber, checkpoint.Offset),
            Options);

        try
        {
            using var content = new MemoryStream(body, writable: false);
            var response = await blob.UploadAsync(
                content,
                new BlobUploadOptions
                {
                    // The claim's ETag is the lease. Writing under it means a
                    // processor that lost the partition while it was working
                    // cannot record a position for it.
                    Conditions = new BlobRequestConditions { IfMatch = new ETag(ownership.ETag) },
                    Metadata = Claim(ownership.OwnerId, Clock.GetUtcNow()),
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                    TransferOptions = BlobArtifactVault.TransferOptions,
                },
                cancellationToken).ConfigureAwait(false);

            // The write renews the claim, so the caller carries a fresh ETag into
            // the next checkpoint instead of one that is already stale.
            return ownership with { ETag = response.Value.ETag.ToString("H") };
        }
        catch (RequestFailedException error) when (error.Status is 412 or 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        var removed = 0;

        await foreach (var item in Container
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, ExpeditionNaming.CheckpointPrefix, cancellationToken)
            .ConfigureAwait(false))
        {
            var response = await Container.GetBlobClient(item.Name)
                .DeleteIfExistsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response.Value)
            {
                removed++;
            }
        }

        return removed;
    }

    private static Dictionary<string, string> Claim(string ownerId, DateTimeOffset now) => new(StringComparer.Ordinal)
    {
        [OwnerMetadataKey] = ownerId,
        [ClaimedAtMetadataKey] = ExpeditionNaming.FormatInstant(now),
    };

    private static DateTimeOffset? ParseInstant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private sealed record CheckpointRecord(long SequenceNumber, string? Offset);
}
