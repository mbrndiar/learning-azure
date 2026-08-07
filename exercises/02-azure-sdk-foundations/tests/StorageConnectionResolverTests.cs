namespace LearningAzure.Exercises.SdkFoundations.Tests;

/// <summary>
/// Verifies the credential seam: where a client points, how it authenticates, and
/// that no secret value ever leaves the resolver.
/// </summary>
public sealed class StorageConnectionResolverTests
{
    private static readonly Dictionary<string, string?> EmulatorVariables = new(StringComparer.Ordinal)
    {
        [StorageConnectionResolver.EmulatorSecretVariable] =
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vd==;",
    };

    private static readonly Dictionary<string, string?> NoVariables = new(StringComparer.Ordinal);

    [Fact]
    public void LiveResolvesToTheAccountEndpoint()
    {
        var connection = StorageConnectionResolver.Resolve(
            DeploymentEnvironment.LiveAzure,
            "stexpedition",
            NoVariables);

        Assert.Equal(new Uri("https://stexpedition.blob.core.windows.net/"), connection.BlobServiceUri);
    }

    [Fact]
    public void LiveAuthenticatesWithEntra()
    {
        var connection = StorageConnectionResolver.Resolve(
            DeploymentEnvironment.LiveAzure,
            "stexpedition",
            NoVariables);

        Assert.Equal(AuthenticationMode.EntraDefaultAzureCredential, connection.Authentication);
    }

    [Fact]
    public void LiveCarriesNoSecretVariable()
    {
        var connection = StorageConnectionResolver.Resolve(
            DeploymentEnvironment.LiveAzure,
            "stexpedition",
            NoVariables);

        Assert.Null(connection.SecretVariableName);
    }

    [Theory]
    [InlineData("STORAGE_ACCOUNTKEY")]
    [InlineData("AZURE_STORAGE_ACCOUNTKEY")]
    [InlineData("AZURITE_CONNECTION_STRING")]
    public void LiveRefusesToRunWithASharedKeyPresent(string variableName)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [variableName] = "some-key-material",
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => StorageConnectionResolver.Resolve(
                DeploymentEnvironment.LiveAzure,
                "stexpedition",
                variables));

        Assert.Contains(variableName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRefusalDoesNotLeakTheKeyMaterial()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["STORAGE_ACCOUNTKEY"] = "s3cr3t-key-material",
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => StorageConnectionResolver.Resolve(
                DeploymentEnvironment.LiveAzure,
                "stexpedition",
                variables));

        Assert.DoesNotContain("s3cr3t-key-material", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmulatorResolvesToTheLoopbackEndpoint()
    {
        var connection = StorageConnectionResolver.Resolve(
            DeploymentEnvironment.LocalEmulator,
            "ignored",
            EmulatorVariables);

        Assert.Equal(StorageConnectionResolver.EmulatorBlobServiceUri, connection.BlobServiceUri);
    }

    [Fact]
    public void EmulatorUsesTheSharedKeyMode()
    {
        var connection = StorageConnectionResolver.Resolve(
            DeploymentEnvironment.LocalEmulator,
            "ignored",
            EmulatorVariables);

        Assert.Equal(AuthenticationMode.EmulatorSharedKey, connection.Authentication);
    }

    [Fact]
    public void EmulatorReturnsTheVariableNameNotTheSecret()
    {
        var connection = StorageConnectionResolver.Resolve(
            DeploymentEnvironment.LocalEmulator,
            "ignored",
            EmulatorVariables);

        Assert.Equal(StorageConnectionResolver.EmulatorSecretVariable, connection.SecretVariableName);
        Assert.DoesNotContain("AccountKey", connection.SecretVariableName, StringComparison.Ordinal);
    }

    [Fact]
    public void EmulatorFailsWhenTheVariableIsMissing()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => StorageConnectionResolver.Resolve(
                DeploymentEnvironment.LocalEmulator,
                "ignored",
                NoVariables));

        Assert.Contains(
            StorageConnectionResolver.EmulatorSecretVariable,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmulatorFailureNamesTheCommandThatFixesIt()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => StorageConnectionResolver.Resolve(
                DeploymentEnvironment.LocalEmulator,
                "ignored",
                NoVariables));

        Assert.Contains("docker compose up -d azurite", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmulatorTreatsABlankVariableAsMissing()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StorageConnectionResolver.EmulatorSecretVariable] = "   ",
        };

        Assert.Throws<InvalidOperationException>(
            () => StorageConnectionResolver.Resolve(
                DeploymentEnvironment.LocalEmulator,
                "ignored",
                variables));
    }

    [Fact]
    public void ResolveRejectsANullVariableMap()
    {
        Assert.Throws<ArgumentNullException>(
            () => StorageConnectionResolver.Resolve(DeploymentEnvironment.LiveAzure, "stexpedition", null!));
    }
}
