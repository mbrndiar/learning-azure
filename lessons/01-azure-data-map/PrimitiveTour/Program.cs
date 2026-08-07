using System.Globalization;

namespace LearningAzure.Lessons.DataMap;

/// <summary>One real expedition record, projected onto every Azure data primitive.</summary>
/// <remarks>
/// The tour is deliberately offline and deterministic: it creates no Azure
/// resources and makes no network call. Its job is to make the *shape* each
/// primitive would force on the same record concrete, so the choice stops being
/// a matter of taste.
/// </remarks>
internal static class Program
{
    /// <summary>The single observation every primitive below has to carry.</summary>
    private const string StationId = "station-bravo";

    /// <summary>Captured at 2026-07-06T12:00:00Z; fixed here so the tour is reproducible.</summary>
    private const string ObservedAt = "2026-07-06T12:00:00Z";

    /// <summary>A field photograph: 4.2 MiB of opaque bytes plus a short caption.</summary>
    private const long PhotographBytes = 4_404_019;

    /// <summary>The Queue Storage message ceiling, in bytes, after encoding.</summary>
    private const int QueueMessageLimitBytes = 65_536;

    private static void Main()
    {
        var culture = CultureInfo.InvariantCulture;

        Console.WriteLine("Cloud Expedition Field Journal — one record, five primitives");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();
        Console.WriteLine("The record:");
        Console.WriteLine($"  station    : {StationId}");
        Console.WriteLine($"  observedAt : {ObservedAt}");
        Console.WriteLine($"  caption    : \"ice shelf calving, north face\"");
        Console.WriteLine($"  photograph : {PhotographBytes.ToString("N0", culture)} bytes (image/jpeg)");
        Console.WriteLine();

        foreach (var profile in PrimitiveCatalog.All)
        {
            Console.WriteLine($"-- {profile.Primitive} ".PadRight(60, '-'));
            Console.WriteLine($"  client       : {profile.ClientType}");
            Console.WriteLine($"  stores       : {profile.StoredThing}");
            Console.WriteLine($"  addressed by : {profile.KeyModel}");
            Console.WriteLine($"  ordering     : {profile.Ordering}");
            Console.WriteLine($"  re-reading   : {profile.Replay}");
            Console.WriteLine($"  cost driver  : {profile.CostDriver}");
            Console.WriteLine($"  this record  : {Shape(profile.Primitive, culture)}");
            Console.WriteLine($"  boundary     : {Boundary(profile.Primitive, culture)}");
            Console.WriteLine();
        }

        Console.WriteLine("Verdict");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine("  The photograph is opaque bytes nobody queries by content, so it belongs");
        Console.WriteLine("  in a blob. The caption and station id are queried by station, so they");
        Console.WriteLine("  belong in a table entity that carries the blob name. The queue carries a");
        Console.WriteLine("  work order naming that blob, never the photograph itself.");
        Console.WriteLine();
        Console.WriteLine("  Chosen: Blob for the payload, Table for the index, Queue for the work.");
        Console.WriteLine("  Rejected: EventStream (nothing replays this record) and Document (no");
        Console.WriteLine("  query touches fields the table's keys do not already address).");
    }

    /// <summary>The concrete shape this record takes inside <paramref name="primitive"/>.</summary>
    private static string Shape(Primitive primitive, CultureInfo culture) => primitive switch
    {
        Primitive.Blob =>
            $"observations/{StationId}/{ObservedAt}.jpg carrying {PhotographBytes.ToString("N0", culture)} bytes, "
            + "with caption and station recorded as blob metadata",
        Primitive.Queue =>
            "a JSON work order — {\"blob\":\"observations/station-bravo/…jpg\",\"station\":\"station-bravo\"} — "
            + "roughly 120 bytes, handed to exactly one processor",
        Primitive.Table =>
            $"PartitionKey=\"{StationId}\", RowKey=\"{ObservedAt}\", plus Caption and BlobName properties",
        Primitive.EventStream =>
            $"an event with partition key \"{StationId}\" carrying the caption and blob name, appended to one partition",
        Primitive.Document =>
            $"{{ \"id\": \"{ObservedAt}\", \"station\": \"{StationId}\", \"caption\": \"…\", \"blobName\": \"…\" }} "
            + "indexed on every property",
        _ => "unknown",
    };

    /// <summary>What this record runs into if the primitive is chosen for the whole job.</summary>
    private static string Boundary(Primitive primitive, CultureInfo culture) => primitive switch
    {
        Primitive.Blob =>
            "no query by station or date — listing is a prefix scan, so an index has to live elsewhere",
        Primitive.Queue =>
            $"the photograph is {PhotographBytes.ToString("N0", culture)} bytes and the message limit is "
            + $"{QueueMessageLimitBytes.ToString("N0", culture)} bytes, so the payload must stay in a blob "
            + "and the message must carry only its name",
        Primitive.Table =>
            "an entity property tops out at 64 KiB and an entity at 1 MiB, so the photograph cannot live here either",
        Primitive.EventStream =>
            "events age out of the retention window and cannot be updated, so this is a feed, not a record of truth",
        Primitive.Document =>
            "every write and every query costs request units, so paying for an index nobody queries is waste",
        _ => "unknown",
    };
}
