using LearningAzure.Exercises.CosmosDevelopment;

namespace LearningAzure.Exercises.CosmosDevelopment.Tests;

/// <summary>
/// Checks that a query is read to the end, and that the end is recognised from
/// the token rather than from the size of the last page.
/// </summary>
public sealed class PageReaderTests
{
    [Fact]
    public void NextRequest_CarriesTheContinuationToken()
    {
        var request = PageReader.NextRequest(25, "token-3");

        Assert.Equal("token-3", request.ContinuationToken);
        Assert.Equal(25, request.MaxItemCount);
    }

    [Fact]
    public void NextRequest_HasNoTokenOnTheFirstPage()
    {
        Assert.Null(PageReader.NextRequest(25, null).ContinuationToken);
    }

    [Fact]
    public void NextRequest_ClampsAnOversizedPageToWhatTheServiceAccepts()
    {
        Assert.Equal(PageReader.MaximumPageSize, PageReader.NextRequest(50_000, null).MaxItemCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NextRequest_RejectsANonPositivePageSize(int pageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageReader.NextRequest(pageSize, null));
    }

    [Fact]
    public void IsExhausted_IsTrueOnlyWhenThereIsNoToken()
    {
        var last = new Page<int>([1, 2, 3], null, 1.0);

        Assert.True(PageReader.IsExhausted(last, 25));
    }

    [Fact]
    public void IsExhausted_IsFalseForAShortPageThatStillCarriesAToken()
    {
        // Cosmos cuts pages short at 4 MB, at five seconds, and at partition
        // boundaries. A short page is not the end of the result set.
        var shortPage = new Page<int>([1, 2], "more", 1.0);

        Assert.False(PageReader.IsExhausted(shortPage, 25));
    }

    [Fact]
    public void IsExhausted_IsFalseForAnEmptyPageThatStillCarriesAToken()
    {
        var empty = new Page<int>([], "more", 1.0);

        Assert.False(PageReader.IsExhausted(empty, 25));
    }

    [Fact]
    public void IsExhausted_TreatsAnEmptyTokenAsTheEnd()
    {
        Assert.True(PageReader.IsExhausted(new Page<int>([1], string.Empty, 1.0), 25));
    }

    [Fact]
    public void IsExhausted_RejectsANullPage()
    {
        Assert.Throws<ArgumentNullException>(() => PageReader.IsExhausted<int>(null!, 25));
    }

    [Fact]
    public void Drain_ReturnsEveryDocument()
    {
        var source = new PagedSource(Fixtures.Documents(120));

        var result = PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        Assert.Equal(120, result.Items.Count);
        Assert.Equal(Fixtures.Documents(120), result.Items);
    }

    [Fact]
    public void Drain_TakesOnePageMoreThanTheDivision()
    {
        // 120 documents at 25 per page is five pages: four full and one of 20.
        var source = new PagedSource(Fixtures.Documents(120));

        var result = PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        Assert.Equal(5, result.Pages);
        Assert.Equal(5, source.Calls);
    }

    [Fact]
    public void Drain_IssuesTheFirstRequestEvenThoughThereIsNoToken()
    {
        // A `while (token != null)` loop never runs at all.
        var source = new PagedSource(Fixtures.Documents(3));

        var result = PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        Assert.Equal(1, result.Pages);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public void Drain_KeepsGoingWhenTheServiceCutsPagesShortOnItsOwn()
    {
        // The caller asks for 25; the service returns 3, then 1, then 4, each
        // with a token. Every one of those is shorter than requested.
        var source = new PagedSource(Fixtures.Documents(20), forcedPageSizes: [3, 1, 4]);

        var result = PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        Assert.Equal(20, result.Items.Count);
    }

    [Fact]
    public void Drain_ReturnsNothingForAnEmptyResultSet()
    {
        var source = new PagedSource([]);

        var result = PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.Pages);
    }

    [Fact]
    public void Drain_AddsUpEveryPagesCharge()
    {
        var source = new PagedSource(Fixtures.Documents(120));

        var result = PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        // Five pages of overhead, and one hundred and twenty documents.
        Assert.Equal((5 * 2.5) + (120 * 0.1), result.RequestCharge, precision: 6);
    }

    [Fact]
    public void Drain_AsksForTheSamePageSizeEveryTime()
    {
        var source = new PagedSource(Fixtures.Documents(120));

        PageReader.Drain(source.Fetch, pageSize: 25, maximumPages: 50);

        Assert.All(source.RequestedSizes, size => Assert.Equal(25, size));
    }

    [Fact]
    public void Drain_StopsRatherThanFollowingTokensForever()
    {
        var source = new PagedSource(Fixtures.Documents(1000));

        var failure = Assert.Throws<InvalidOperationException>(
            () => PageReader.Drain(source.Fetch, pageSize: 10, maximumPages: 4));

        Assert.Contains("4 pages", failure.Message, StringComparison.Ordinal);
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public void Drain_RejectsANullFetch()
    {
        Assert.Throws<ArgumentNullException>(
            () => PageReader.Drain<int>(null!, pageSize: 25, maximumPages: 5));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    public void Drain_RejectsNonPositiveBounds(int pageSize, int maximumPages)
    {
        var source = new PagedSource(Fixtures.Documents(10));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PageReader.Drain(source.Fetch, pageSize, maximumPages));
    }
}
