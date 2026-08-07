namespace LearningAzure.Exercises.BlobStorage.Tests;

/// <summary>Verifies that listing is paged, lazy, cancellable, and costed.</summary>
public sealed class ArtifactCatalogTests
{
    private static readonly DateTimeOffset Observed = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static CountingPageSource Source(int artifacts, int pageSize, string station = "station-bravo")
    {
        var items = Enumerable.Range(0, artifacts)
            .Select(index => new ArtifactListing(
                ArtifactPath.For(station, Observed.AddSeconds(index), $"a{index}.jpg"),
                1024 + index))
            .ToArray();

        return new CountingPageSource(items, pageSize);
    }

    [Fact]
    public async Task EveryArtifactIsReturned()
    {
        var source = Source(25, pageSize: 10);
        var seen = new List<ArtifactListing>();

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            seen.Add(item);
        }

        Assert.Equal(25, seen.Count);
    }

    [Fact]
    public async Task ArtifactsArriveInServiceOrder()
    {
        var source = Source(25, pageSize: 10);
        var names = new List<string>();

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            names.Add(item.Name);
        }

        Assert.Equal<IEnumerable<string>>(names, [.. names.OrderBy(name => name, StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task AFullEnumerationCostsOneCallPerPage()
    {
        var source = Source(25, pageSize: 10);

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            _ = item;
        }

        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task TakingTheFirstPageCostsExactlyOneCall()
    {
        var source = Source(10_000, pageSize: 10);
        var seen = 0;

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            _ = item;
            if (++seen == 10)
            {
                break;
            }
        }

        // An implementation that collects every page into a List before returning
        // makes 1000 calls here, and the caller wanted ten artifacts.
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task StoppingEarlyStopsFetching()
    {
        var source = Source(10_000, pageSize: 10);

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            _ = item;
            break;
        }

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ThePageSizeIsPassedThroughToTheService()
    {
        var source = Source(25, pageSize: 10);

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            _ = item;
        }

        Assert.Equal(10, source.LastRequestedPageSize);
    }

    [Fact]
    public async Task ThePrefixExcludesOtherStations()
    {
        var mixed = new CountingPageSource(
            [
                new ArtifactListing(ArtifactPath.For("station-bravo", Observed, "a.jpg"), 1),
                new ArtifactListing(ArtifactPath.For("station-alfa", Observed, "b.jpg"), 1),
            ],
            declaredPageSize: 10);

        var seen = new List<ArtifactListing>();
        await foreach (var item in ArtifactCatalog
            .ListAsync(mixed, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            seen.Add(item);
        }

        Assert.Single(seen);
    }

    [Fact]
    public async Task AnEmptyPrefixYieldsNothingAndCostsOneCall()
    {
        var source = Source(0, pageSize: 10);
        var seen = 0;

        await foreach (var item in ArtifactCatalog
            .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            _ = item;
            seen++;
        }

        Assert.Equal(0, seen);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ACancelledTokenStopsTheEnumeration()
    {
        var source = Source(10_000, pageSize: 10);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in ArtifactCatalog
                .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, cancellation.Token)
                .ConfigureAwait(true))
            {
                _ = item;
            }
        });
    }

    [Fact]
    public async Task CancellingMidEnumerationStopsFurtherCalls()
    {
        var source = Source(10_000, pageSize: 10);
        using var cancellation = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in ArtifactCatalog
                .ListAsync(source, ArtifactPath.StationPrefix("station-bravo"), 10, cancellation.Token)
                .ConfigureAwait(true))
            {
                _ = item;
                if (++seen == 5)
                {
                    await cancellation.CancelAsync().ConfigureAwait(true);
                }
            }
        });

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void ANullSourceIsRejectedBeforeEnumeration()
    {
        // An async iterator runs no statement until it is enumerated, so a guard
        // clause inside one throws at the wrong time, or never.
        Assert.Throws<ArgumentNullException>(
            () => ArtifactCatalog.ListAsync(null!, "observations/", 10, CancellationToken.None));
    }

    [Fact]
    public void ANonPositivePageSizeIsRejectedBeforeEnumeration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArtifactCatalog.ListAsync(Source(1, 10), "observations/", 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(25, 10, 3)]
    [InlineData(5000, 5000, 1)]
    [InlineData(5001, 5000, 2)]
    public void RequestCountIsWhatAzureBills(int artifacts, int pageSize, int expected)
    {
        Assert.Equal(expected, ArtifactCatalog.RequestCount(artifacts, pageSize));
    }

    [Fact]
    public void RequestCountMatchesTheCallsAnEnumerationActuallyMakes()
    {
        Assert.Equal(3, ArtifactCatalog.RequestCount(25, 10));
    }

    [Fact]
    public void RequestCountRejectsANonPositivePageSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtifactCatalog.RequestCount(10, 0));
    }
}
