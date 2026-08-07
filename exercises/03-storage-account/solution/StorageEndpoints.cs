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
    public static Uri For(StorageService service, string accountName, StorageEnvironment environment)
    {
        return environment switch
        {
            StorageEnvironment.LiveAzure => Live(service, accountName),
            StorageEnvironment.Emulator => Emulator(service),
            _ => throw new ArgumentOutOfRangeException(
                nameof(environment),
                environment,
                "Unknown storage environment."),
        };
    }

    /// <summary>Tests whether a name can be a live storage account name.</summary>
    /// <param name="accountName">The candidate name.</param>
    /// <returns><c>true</c> when Azure would accept the name.</returns>
    public static bool IsValidAccountName(string? accountName)
    {
        if (accountName is null || accountName.Length is < 3 or > 24)
        {
            return false;
        }

        foreach (var character in accountName)
        {
            // The name becomes a DNS label, so the alphabet is narrower than the
            // one resource groups accept: no hyphen, no underscore, no uppercase.
            var allowed = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static Uri Live(StorageService service, string accountName)
    {
        if (!IsValidAccountName(accountName))
        {
            throw new ArgumentException(
                $"'{accountName}' is not a valid storage account name: 3-24 lowercase letters and "
                + "digits, because the name becomes a DNS label.",
                nameof(accountName));
        }

        var suffix = service switch
        {
            StorageService.Blob => "blob",
            StorageService.Queue => "queue",
            StorageService.Table => "table",
            StorageService.File => "file",
            _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unknown storage service."),
        };

        return new Uri($"https://{accountName}.{suffix}.core.windows.net/");
    }

    private static Uri Emulator(StorageService service)
    {
        var port = service switch
        {
            StorageService.Blob => 10000,
            StorageService.Queue => 10001,
            StorageService.Table => 10002,

            // Azurite implements no File service. Returning a plausible URI would
            // move the failure from here to a confusing connection error later.
            StorageService.File => throw new NotSupportedException(
                "Azurite does not emulate Azure Files. File shares require the live checkpoint."),
            _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unknown storage service."),
        };

        return new Uri($"http://{EmulatorHost}:{port}/{EmulatorAccountName}");
    }
}
