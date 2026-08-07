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

        // GAP 4: join the parts with '|', after refusing the two inputs that
        // would silently corrupt the model.
        //
        // Refuse a part that is null or empty: skipping it would put the same
        // entity into a different logical partition from every complete
        // document, and no query would find both. Refuse a part that already
        // contains the separator, because 'a|b' + 'c' and 'a' + 'b|c' would
        // compose to the same key. Then check the size: a partition key value
        // may not exceed MaximumKeyBytes, measured in UTF-8 BYTES rather than
        // characters, and throw ArgumentOutOfRangeException when it does.
        // See lessons/10-cosmos-modeling/README.md#when-no-natural-key-works-make-one
        throw new NotImplementedException(
            "GAP 4: implement SyntheticKeyBuilder.Compose. "
            + "See lessons/10-cosmos-modeling/README.md#when-no-natural-key-works-make-one.");
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

        // GAP 5: append a bucket derived from the DOCUMENT, not from a random
        // number, formatted as "{hotKey}-{bucket:000}".
        //
        // A random suffix spreads writes just as well and makes the document
        // unreadable: a point read needs the partition key, and nothing in the
        // document would say which bucket it went to. Hash the document id
        // (SHA256.HashData over the UTF-8 bytes is enough) and take it modulo
        // the bucket count, so any reader holding the id can recompute the key.
        // See lessons/10-cosmos-modeling/README.md#when-no-natural-key-works-make-one
        throw new NotImplementedException(
            "GAP 5: implement SyntheticKeyBuilder.Spread. "
            + "See lessons/10-cosmos-modeling/README.md#when-no-natural-key-works-make-one.");
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

        // List every key Spread can produce for this hot key, in bucket order.
        // This list is the price of spreading: a query that wants the whole hot
        // key must now be issued once per bucket.
        throw new NotImplementedException(
            "Implement SyntheticKeyBuilder.FanOutKeys once GAP 5 works. "
            + "See lessons/10-cosmos-modeling/README.md#when-no-natural-key-works-make-one.");
    }
}
