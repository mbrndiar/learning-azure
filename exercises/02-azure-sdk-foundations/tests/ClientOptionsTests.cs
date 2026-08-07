using Azure.Core;

namespace LearningAzure.Exercises.SdkFoundations.Tests;

/// <summary>Verifies that the retry seam is configured, bounded, and exponential.</summary>
public sealed class ClientOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void MaxRetriesIsWhatTheCallerAskedFor(int maxRetries)
    {
        var options = StorageConnectionResolver.CreateClientOptions(maxRetries, TimeSpan.Zero);

        Assert.Equal(maxRetries, options.Retry.MaxRetries);
    }

    [Fact]
    public void RetriesBackOffExponentially()
    {
        var options = StorageConnectionResolver.CreateClientOptions(3, TimeSpan.FromMilliseconds(50));

        Assert.Equal(RetryMode.Exponential, options.Retry.Mode);
    }

    [Fact]
    public void TheBaseDelayIsWhatTheCallerAskedFor()
    {
        var options = StorageConnectionResolver.CreateClientOptions(3, TimeSpan.FromMilliseconds(50));

        Assert.Equal(TimeSpan.FromMilliseconds(50), options.Retry.Delay);
    }

    [Fact]
    public void BackoffIsCappedAboveTheBaseDelay()
    {
        var options = StorageConnectionResolver.CreateClientOptions(3, TimeSpan.FromMilliseconds(50));

        Assert.True(
            options.Retry.MaxDelay > options.Retry.Delay,
            $"MaxDelay {options.Retry.MaxDelay} must exceed Delay {options.Retry.Delay}.");
    }

    [Fact]
    public void ANetworkTimeoutIsSet()
    {
        var options = StorageConnectionResolver.CreateClientOptions(3, TimeSpan.Zero);

        Assert.True(
            options.Retry.NetworkTimeout > TimeSpan.Zero
                && options.Retry.NetworkTimeout <= TimeSpan.FromMinutes(1),
            $"NetworkTimeout {options.Retry.NetworkTimeout} must be a bounded, non-zero timeout.");
    }

    [Fact]
    public void ANegativeRetryBudgetIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StorageConnectionResolver.CreateClientOptions(-1, TimeSpan.Zero));
    }

    [Fact]
    public void ANegativeDelayIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StorageConnectionResolver.CreateClientOptions(3, TimeSpan.FromSeconds(-1)));
    }
}
