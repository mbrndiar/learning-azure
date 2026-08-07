namespace LearningAzure.Exercises.StorageAccount.Tests;

/// <summary>Verifies endpoint resolution and the naming rule the endpoint depends on.</summary>
public sealed class StorageEndpointsTests
{
    [Theory]
    [InlineData(StorageService.Blob, "https://stexpedition.blob.core.windows.net/")]
    [InlineData(StorageService.Queue, "https://stexpedition.queue.core.windows.net/")]
    [InlineData(StorageService.Table, "https://stexpedition.table.core.windows.net/")]
    [InlineData(StorageService.File, "https://stexpedition.file.core.windows.net/")]
    public void LiveEndpointsPutTheAccountNameFirst(StorageService service, string expected)
    {
        var endpoint = StorageEndpoints.For(service, "stexpedition", StorageEnvironment.LiveAzure);

        Assert.Equal(new Uri(expected), endpoint);
    }

    [Fact]
    public void LiveEndpointsAreHttps()
    {
        var endpoint = StorageEndpoints.For(StorageService.Blob, "stexpedition", StorageEnvironment.LiveAzure);

        Assert.Equal("https", endpoint.Scheme);
    }

    [Fact]
    public void LiveEndpointsRejectANameAzureWouldRefuse()
    {
        Assert.Throws<ArgumentException>(
            () => StorageEndpoints.For(StorageService.Blob, "st-expedition", StorageEnvironment.LiveAzure));
    }

    [Theory]
    [InlineData(StorageService.Blob, 10000)]
    [InlineData(StorageService.Queue, 10001)]
    [InlineData(StorageService.Table, 10002)]
    public void EmulatorEndpointsUseThePortsFromComposeYaml(StorageService service, int port)
    {
        var endpoint = StorageEndpoints.For(service, "ignored", StorageEnvironment.Emulator);

        Assert.Equal(port, endpoint.Port);
    }

    [Fact]
    public void EmulatorEndpointsAddressTheAccountByPath()
    {
        var endpoint = StorageEndpoints.For(StorageService.Blob, "ignored", StorageEnvironment.Emulator);

        Assert.Equal("/" + StorageEndpoints.EmulatorAccountName, endpoint.AbsolutePath);
        Assert.Equal(StorageEndpoints.EmulatorHost, endpoint.Host);
    }

    [Fact]
    public void EmulatorEndpointsIgnoreTheSuppliedAccountName()
    {
        var endpoint = StorageEndpoints.For(StorageService.Blob, "stexpedition", StorageEnvironment.Emulator);

        Assert.DoesNotContain("stexpedition", endpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmulatorHasNoFileService()
    {
        // Returning a plausible URI here would move the failure from a clear
        // NotSupportedException to a confusing connection error much later.
        Assert.Throws<NotSupportedException>(
            () => StorageEndpoints.For(StorageService.File, "ignored", StorageEnvironment.Emulator));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("stexpedition")]
    [InlineData("st1234567890123456789012")]
    [InlineData("000")]
    public void ValidNamesAreAccepted(string name)
    {
        Assert.True(StorageEndpoints.IsValidAccountName(name), name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("st12345678901234567890123")]
    [InlineData("StExpedition")]
    [InlineData("st-expedition")]
    [InlineData("st_expedition")]
    [InlineData("st expedition")]
    [InlineData("stexpedition.dev")]
    public void NamesAzureWouldRefuseAreRejected(string? name)
    {
        Assert.False(StorageEndpoints.IsValidAccountName(name), name ?? "<null>");
    }

    [Fact]
    public void TheNameLimitIsTwentyFourCharactersNotSixtyThree()
    {
        // A DNS label allows 63 characters; a storage account name allows 24.
        // Reading the wider limit from RFC 1035 produces names Azure refuses.
        Assert.True(StorageEndpoints.IsValidAccountName(new string('a', 24)));
        Assert.False(StorageEndpoints.IsValidAccountName(new string('a', 25)));
    }
}
