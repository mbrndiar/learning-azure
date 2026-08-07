using Azure;

namespace LearningAzure.Exercises.TableStorage;

/// <summary>Updates observations without losing a concurrent writer's change.</summary>
/// <remarks>
/// The entity ETag is module 5's blob ETag in a different service. The mechanism
/// is identical — bet on the version you read, and be told when you lose — but
/// the API is not: a table update takes the ETag as an argument rather than as
/// a header you set yourself.
/// </remarks>
public sealed class ObservationUpdater(IObservationTable table)
{
    /// <summary>The table this updater writes to.</summary>
    public IObservationTable Table { get; } = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>Applies <paramref name="change"/> to one observation, once, safely.</summary>
    /// <param name="partitionKey">The observation's partition.</param>
    /// <param name="rowKey">The observation's row.</param>
    /// <param name="change">The change to apply to the entity that was read.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>What happened.</returns>
    public async Task<UpdateOutcome> TryUpdateAsync(
        string partitionKey,
        string rowKey,
        Action<ObservationEntity> change,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        ArgumentNullException.ThrowIfNull(change);

        var entity = await Table.TryGetAsync(partitionKey, rowKey, cancellationToken).ConfigureAwait(false);

        if (entity is null)
        {
            return UpdateOutcome.Missing;
        }

        // GAP 8 — Bet on the ETag that came back from THIS read.
        //
        // Passing ETag.All means "overwrite whatever is there", which is the
        // last-write-wins default module 5 spent a whole module removing. The
        // only safe argument is the version the change was computed from.
        var read = entity.ETag;
        change(entity);

        return await Table.TryReplaceAsync(entity, read, cancellationToken).ConfigureAwait(false)
            ? UpdateOutcome.Applied
            : UpdateOutcome.Stale;
    }

    /// <summary>Applies <paramref name="change"/>, re-reading when a competitor wins.</summary>
    /// <param name="partitionKey">The observation's partition.</param>
    /// <param name="rowKey">The observation's row.</param>
    /// <param name="change">The change to apply to the entity that was read.</param>
    /// <param name="maxAttempts">How many times to try before giving up.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>What happened on the last attempt.</returns>
    public async Task<UpdateOutcome> UpdateWithRetryAsync(
        string partitionKey,
        string rowKey,
        Action<ObservationEntity> change,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var outcome = UpdateOutcome.Stale;

        // GAP 9 — The RE-READ must be inside the loop.
        //
        // A retry that re-sends the same stale ETag fails identically forever,
        // and a retry that re-sends a fresh ETag with STALE DATA silently
        // reintroduces the lost update the ETag was there to prevent. Both
        // failure modes look like a working retry from the outside.
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            outcome = await TryUpdateAsync(partitionKey, rowKey, change, cancellationToken)
                .ConfigureAwait(false);

            if (outcome != UpdateOutcome.Stale)
            {
                return outcome;
            }
        }

        return outcome;
    }
}

/// <summary>The table operations this module needs, and no others.</summary>
public interface IObservationTable
{
    /// <summary>Reads one entity by both keys, or <c>null</c> when it does not exist.</summary>
    /// <param name="partitionKey">The partition.</param>
    /// <param name="rowKey">The row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entity, or <c>null</c>.</returns>
    Task<ObservationEntity?> TryGetAsync(string partitionKey, string rowKey, CancellationToken cancellationToken);

    /// <summary>Replaces an entity only if its stored version is still <paramref name="ifMatch"/>.</summary>
    /// <param name="entity">The entity to store.</param>
    /// <param name="ifMatch">The version the change was computed from.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><c>true</c> when the write landed; <c>false</c> when the version was stale.</returns>
    Task<bool> TryReplaceAsync(ObservationEntity entity, ETag ifMatch, CancellationToken cancellationToken);
}
