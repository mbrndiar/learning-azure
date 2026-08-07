namespace LearningAzure.Exercises.StorageAccount;

/// <summary>Resolves the service endpoint an account exposes.</summary>
/// <remarks>
/// The account name is not a label: it is the leftmost DNS label of every live
/// endpoint, which is why it is globally unique and why the emulator — which
/// cannot own DNS — addresses accounts by path instead.
/// </remarks>
public static class StorageEndpoints
{
    /// <summary>Azurite's loopback host.</summary>
    public const string EmulatorHost = "127.0.0.1";

    /// <summary>The well-known emulator account name.</summary>
    public const string EmulatorAccountName = "devstoreaccount1";

    /// <summary>Returns the endpoint of one service on one account.</summary>
    /// <param name="service">The service to address.</param>
    /// <param name="accountName">Account name; ignored in the emulator.</param>
    /// <param name="environment">Emulator or live Azure.</param>
    /// <returns>The absolute service endpoint.</returns>
    public static Uri For(StorageService service, string accountName, StorageEnvironment environment) =>
        // GAP 1 — Resolve the service endpoint.
        //
        // LiveAzure:  https://{accountName}.{blob|queue|table|file}.core.windows.net/
        //             The account name is a DNS label, so validate it first with
        //             IsValidAccountName and throw ArgumentException when it fails.
        //
        // Emulator:   http://127.0.0.1:{10000|10001|10002}/devstoreaccount1
        //             Blob 10000, Queue 10001, Table 10002 — the ports in
        //             compose.yaml. Azurite has no File service at all, so
        //             StorageService.File must throw NotSupportedException rather
        //             than return an endpoint that cannot answer.
        throw new NotImplementedException(
            "GAP 1: implement StorageEndpoints.For. See "
            + "lessons/03-storage-account/README.md#the-account-is-the-dns-name.");

    /// <summary>Tests whether a name can be a live storage account name.</summary>
    /// <param name="accountName">The candidate name.</param>
    /// <returns><c>true</c> when Azure would accept the name.</returns>
    public static bool IsValidAccountName(string? accountName) =>
        // GAP 2 — Encode the naming rule the endpoint depends on.
        //
        // Valid: 3-24 characters, lowercase letters and digits only. No hyphens,
        // no uppercase, no underscores — because it becomes a DNS label, and a
        // DNS label is case-insensitive and cannot carry the characters resource
        // groups allow. This is the rule module 1's name generator was built to
        // satisfy; here it becomes an executable check.
        throw new NotImplementedException(
            "GAP 2: implement StorageEndpoints.IsValidAccountName. See "
            + "lessons/03-storage-account/README.md#the-account-is-the-dns-name.");
}
