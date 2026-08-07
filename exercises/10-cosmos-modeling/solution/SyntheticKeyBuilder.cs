using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LearningAzure.Exercises.CosmosModeling;

/// <summary>
/// Builds partition key values when no property in the document is a good key on
/// its own — by combining properties, or by adding one that does not exist.
/// </summary>
public sealed class SyntheticKeyBuilder
{
    /// <summary>The maximum size of a partition key value, in bytes.</summary>
    public const int MaximumKeyBytes = 2048;

    private readonly int _buckets;

    /// <summary>Initialises a new instance of the <see cref="SyntheticKeyBuilder"/> class.</summary>
    /// <param name="buckets">How many buckets a hot key is spread across.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="buckets"/> is not positive.</exception>
    public SyntheticKeyBuilder(int buckets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);

        _buckets = buckets;
    }

    /// <summary>Gets how many buckets this builder spreads a hot key across.</summary>
    public int Buckets => _buckets;

    /// <summary>Joins several document properties into one partition key value.</summary>
    /// <param name="parts">The property values, in a fixed order.</param>
    /// <returns>The composite key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> is null.</exception>
    /// <exception cref="ArgumentException">No parts were supplied, or a part is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The result exceeds the key size limit.</exception>
    public static string Compose(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Length == 0)
        {
            throw new ArgumentException("A composite key needs at least one part.", nameof(parts));
        }

        // GAP 4: a composite key is a string, and its order is permanent.
        //
        // Every part must be present, because a key that is sometimes
        // "tenant|device" and sometimes "tenant" puts the same entity in two
        // logical partitions and no query will find both. The separator must
        // never occur inside a part for the same reason. And the order is fixed
        // forever: a query can filter on the whole key or nothing, so putting
        // the coarse value first is what makes prefix reasoning possible for a
        // human even though Cosmos itself treats the value as opaque.
        // See lessons/10-cosmos-modeling/README.md#when-no-natural-key-works-make-one
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                throw new ArgumentException(
                    "Every part of a composite key must be present: an absent part silently "
                    + "creates a second logical partition for the same entity.",
                    nameof(parts));
            }

            if (part.Contains('|', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A part may not contain the separator: 'a|b' and 'a' + '|b' would collide.",
                    nameof(parts));
            }
        }

        var composed = string.Join('|', parts);

        if (Encoding.UTF8.GetByteCount(composed) > MaximumKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parts),
                $"A partition key value may not exceed {MaximumKeyBytes} bytes.");
        }

        return composed;
    }

    /// <summary>
    /// Spreads a key that would otherwise be hot across a fixed number of
    /// buckets, deterministically.
    /// </summary>
    /// <param name="hotKey">The value that concentrates too much traffic.</param>
    /// <param name="documentId">The document being placed.</param>
    /// <returns>The bucketed partition key value.</returns>
    /// <exception cref="ArgumentException">An argument is null or empty.</exception>
    public string Spread(string hotKey, string documentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(hotKey);
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        // GAP 5: the suffix must be derived from the document, not random.
        //
        // A random suffix spreads writes just as well and makes the document
        // unreadable: a point read needs the partition key, and nothing in the
        // document says which bucket it went to. Hashing the id means the same
        // document always maps to the same bucket, so a reader can recompute it.
        // The cost is paid on reads that span the whole key: they must be issued
        // once per bucket, which is exactly the trade being made.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(documentId));
        var bucket = (int)(BitConverter.ToUInt32(hash, 0) % (uint)_buckets);

        return string.Create(CultureInfo.InvariantCulture, $"{hotKey}-{bucket:000}");
    }

    /// <summary>
    /// Lists every partition key value a query against a spread key must be
    /// issued for.
    /// </summary>
    /// <param name="hotKey">The value that was spread.</param>
    /// <returns>One key per bucket, in bucket order.</returns>
    /// <exception cref="ArgumentException"><paramref name="hotKey"/> is null or empty.</exception>
    public IReadOnlyList<string> FanOutKeys(string hotKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hotKey);

        var keys = new string[_buckets];

        for (var bucket = 0; bucket < _buckets; bucket++)
        {
            keys[bucket] = string.Create(CultureInfo.InvariantCulture, $"{hotKey}-{bucket:000}");
        }

        return keys;
    }
}
