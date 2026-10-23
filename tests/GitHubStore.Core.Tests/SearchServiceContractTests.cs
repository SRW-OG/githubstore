using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Contract")]
public abstract class SearchServiceContractTests
{
    protected abstract ISearchService CreateService();
    protected abstract ICatalogService CreateCatalog();

    [Theory]
    [InlineData("")]
    [InlineData("   \t\n")]
    public async Task EmptyQuery_ReturnsVisibleLocalCatalogAndTotalBeforeLimit(string query)
    {
        var visible = await CreateCatalog().BrowseAsync(new CatalogQuery());
        var result = await CreateService().SearchAsync(query, new SearchOptions { Limit = 3 });
        Assert.Equal(visible.Total, result.Total);
        Assert.Equal(visible.Items.Take(3), result.Items);
        Assert.Equal("local", result.Source);
        Assert.Null(result.RemoteError);
        Assert.True(result.Took >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Search_MatchesMetadataIgnoringCaseAndCombinesWords()
    {
        var service = CreateService();
        var app = (await CreateCatalog().BrowseAsync(new CatalogQuery())).Items.First();
        string[] terms = [app.DisplayName, app.Owner, app.Repo, app.Id.PackageId.Value,
            app.Description!, app.Topics.First()];
        foreach (var term in terms)
        {
            var result = await service.SearchAsync(term.ToUpperInvariant(), new SearchOptions());
            Assert.Contains(result.Items, a => a.Id == app.Id);
        }
        var combined = await service.SearchAsync($"{app.Owner} {app.Repo}", new SearchOptions());
        Assert.Contains(combined.Items, a => a.Id == app.Id);
        var missing = await service.SearchAsync($"{app.Owner} definitely-missing-token", new SearchOptions());
        Assert.Empty(missing.Items);
        Assert.Equal(0, missing.Total);
    }

    [Fact]
    public async Task Filters_CombineCategoryLanguageAndMinimumStars()
    {
        var full = await CreateCatalog().BrowseAsync(new CatalogQuery());
        var sample = full.Items.First(a => a.Language is not null);
        var options = new SearchOptions
        {
            Category = sample.PrimaryCategory,
            Language = $" {sample.Language!.ToUpperInvariant()} ",
            MinStars = sample.Stars,
            Limit = int.MaxValue,
        };
        var expected = full.Items.Where(a =>
            (a.PrimaryCategory == sample.PrimaryCategory || a.Categories.Contains(sample.PrimaryCategory))
            && string.Equals(a.Language, sample.Language, StringComparison.OrdinalIgnoreCase)
            && a.Stars >= sample.Stars);
        var result = await CreateService().SearchAsync("", options);
        Assert.Equal(expected, result.Items);
        Assert.Equal(result.Items.Count, result.Total);
        var empty = await CreateService().SearchAsync("", options with { Category = new CategoryId("missing-category") });
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Total);
    }

    [Fact]
    public async Task Limits_AllowZeroAndRejectNegativeValues()
    {
        var service = CreateService();
        var zero = await service.SearchAsync("", new SearchOptions { Limit = 0 });
        Assert.Empty(zero.Items);
        Assert.True(zero.Total > 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync("", new SearchOptions { Limit = -1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync("", new SearchOptions { MinStars = -1 }));
    }

    [Fact]
    public async Task Search_PropagatesPreCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().SearchAsync("", new SearchOptions(), cancellation.Token));
    }
}

public sealed class MockSearchServiceTests : SearchServiceContractTests
{
    protected override ISearchService CreateService() => new MockCatalogService();
    protected override ICatalogService CreateCatalog() => new MockCatalogService();

    [Fact]
    public async Task RemoteSearchUnavailable_ReturnsLocalResultsAndAnExplicitError()
    {
        var service = new MockCatalogService();
        var local = await service.SearchAsync("notes", new SearchOptions());
        var requestedRemote = await service.SearchAsync("notes", new SearchOptions { IncludeRemote = true });
        Assert.Equal(local.Items, requestedRemote.Items);
        Assert.Equal(local.Total, requestedRemote.Total);
        Assert.Equal("local", requestedRemote.Source);
        Assert.False(string.IsNullOrWhiteSpace(requestedRemote.RemoteError));
        Assert.Equal(new[] { MockCatalogService.OfficialSourceId, MockCatalogService.CommunitySourceId },
            requestedRemote.Items.Select(a => a.Id.SourceId).Distinct());
    }
}
