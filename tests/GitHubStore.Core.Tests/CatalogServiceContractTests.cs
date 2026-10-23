using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

// A future real implementation can inherit this suite with the same fixture snapshot.
[Trait("Category", "Contract")]
public abstract class CatalogServiceContractTests
{
    protected abstract ICatalogService CreateService();

    [Fact]
    public async Task Categories_HaveTheTwelveDocumentedTabsInOrder()
    {
        var categories = await CreateService().ListCategoriesAsync();
        string[] expected = ["dev-tools", "system", "network", "media", "graphics", "office",
            "security", "games", "ai", "terminal", "files", "science"];
        Assert.Equal(expected, categories.Select(c => c.Id.Value));
        Assert.Equal(categories.OrderBy(c => c.Order), categories);
        Assert.All(categories, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.NameEn));
            Assert.Null(c.ParentId);
        });
    }

    [Theory]
    [InlineData(SortKey.Stars)]
    [InlineData(SortKey.Trending)]
    [InlineData(SortKey.Updated)]
    [InlineData(SortKey.Name)]
    public async Task Pagination_IsStableAndHasNoGapsOrDuplicates(SortKey sort)
    {
        var service = CreateService();
        var full = await service.BrowseAsync(new CatalogQuery { Sort = sort, Limit = int.MaxValue });
        Assert.NotEmpty(full.Items);
        Assert.Equal(full.Items.Count, full.Total);
        Assert.False(full.HasMore);
        var ids = new List<AppId>();
        const int pageSize = 4;
        for (var offset = 0; offset < full.Total; offset += pageSize)
        {
            var page = await service.BrowseAsync(new CatalogQuery { Sort = sort, Offset = offset, Limit = pageSize });
            Assert.Equal(full.Total, page.Total);
            Assert.Equal(full.Items.Skip(offset).Take(pageSize).Select(a => a.Id), page.Items.Select(a => a.Id));
            Assert.Equal(offset + page.Items.Count < full.Total, page.HasMore);
            ids.AddRange(page.Items.Select(a => a.Id));
        }
        Assert.Equal(full.Items.Select(a => a.Id), ids);
        Assert.Distinct(ids);
        var again = await service.BrowseAsync(new CatalogQuery { Sort = sort, Limit = int.MaxValue });
        Assert.Equal(ids, again.Items.Select(a => a.Id));
    }

    [Fact]
    public async Task Pagination_HandlesZeroLimitAndOffsetsAtOrBeyondEnd()
    {
        var service = CreateService();
        var full = await service.BrowseAsync(new CatalogQuery { Limit = int.MaxValue });
        var zero = await service.BrowseAsync(new CatalogQuery { Limit = 0 });
        Assert.Empty(zero.Items);
        Assert.Equal(full.Total, zero.Total);
        Assert.Equal(full.Total > 0, zero.HasMore);
        foreach (var offset in new[] { full.Total, full.Total + 1, int.MaxValue })
        {
            var page = await service.BrowseAsync(new CatalogQuery { Offset = offset, Limit = int.MaxValue });
            Assert.Empty(page.Items);
            Assert.Equal(full.Total, page.Total);
            Assert.False(page.HasMore);
        }
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    public async Task Pagination_RejectsNegativeArguments(int offset, int limit)
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().BrowseAsync(
            new CatalogQuery { Offset = offset, Limit = limit }));

    [Fact]
    public async Task Sorting_UsesStarsUpdatedTimeAndNameAsPrimaryKeys()
    {
        var service = CreateService();
        foreach (var sort in new[] { SortKey.Stars, SortKey.Updated, SortKey.Name })
        {
            var page = await service.BrowseAsync(new CatalogQuery { Sort = sort });
            var expected = sort switch
            {
                SortKey.Stars => page.Items.OrderByDescending(a => a.Stars),
                SortKey.Updated => page.Items.OrderByDescending(a => a.PushedAt),
                _ => page.Items.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase),
            };
            Assert.Equal(expected.Select(a => a.Id), page.Items.Select(a => a.Id));
        }
    }

    [Fact]
    public async Task CategoryFilter_ReturnsOnlyMembersAndUnknownCategoryIsEmpty()
    {
        var service = CreateService();
        var full = await service.BrowseAsync(new CatalogQuery());
        var category = full.Items.First().PrimaryCategory;
        var filtered = await service.BrowseAsync(new CatalogQuery { Category = category });
        Assert.Equal(full.Items.Where(a => a.PrimaryCategory == category || a.Categories.Contains(category)), filtered.Items);
        var unknown = await service.BrowseAsync(new CatalogQuery { Category = new CategoryId("missing-category") });
        Assert.Empty(unknown.Items);
        Assert.Equal(0, unknown.Total);
        Assert.False(unknown.HasMore);
    }

    [Theory]
    [InlineData(BoardKind.Stars)]
    [InlineData(BoardKind.Trending)]
    [InlineData(BoardKind.New)]
    public async Task Rankings_AreStableRespectLimitAndExcludeArchivedApps(BoardKind board)
    {
        var service = CreateService();
        var full = await service.RankAsync(board, int.MaxValue);
        Assert.NotEmpty(full);
        Assert.All(full, a => Assert.False(a.Archived));
        Assert.Equal(full.Take(3), await service.RankAsync(board, 3));
        Assert.Equal(full, await service.RankAsync(board, int.MaxValue));
        Assert.Empty(await service.RankAsync(board, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RankAsync(board, -1));
    }

    [Fact]
    public async Task Detail_PreservesAppAndReleaseAssetRelationships()
    {
        var service = CreateService();
        var page = await service.BrowseAsync(new CatalogQuery());
        var app = page.Items.First();
        var detail = await service.GetAppAsync(app.Id);
        Assert.Equal(app, detail.App);
        Assert.NotEmpty(detail.Releases);
        Assert.All(detail.Releases, release =>
        {
            Assert.Equal(app.Id, release.AppId);
            Assert.All(release.Assets, asset => Assert.Equal(release.Key, asset.ReleaseKey));
        });
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAppAsync(
            new AppId(app.Id.SourceId, new PackageId("missing.package"))));
    }

    [Fact]
    public async Task AllCatalogMethods_PropagatePreCancellation()
    {
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ct = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListCategoriesAsync(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BrowseAsync(new CatalogQuery(), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RankAsync(BoardKind.Stars, 5, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAppAsync(default, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(RefreshScope.All, ct: ct));
    }
}

public sealed class MockCatalogServiceTests : CatalogServiceContractTests
{
    protected override ICatalogService CreateService() => new MockCatalogService();

    [Fact]
    public async Task Fixture_HasAnEmptyCategoryAndExplicitArchivedVisibility()
    {
        var service = new MockCatalogService();
        var visible = await service.BrowseAsync(new CatalogQuery());
        var all = await service.BrowseAsync(new CatalogQuery { IncludeArchived = true });
        Assert.Equal(15, visible.Total);
        Assert.Equal(16, all.Total);
        Assert.All(visible.Items, a => Assert.False(a.Archived));
        Assert.Single(all.Items, a => a.Archived);
        var empty = await service.BrowseAsync(new CatalogQuery { Category = new CategoryId("science") });
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Total);
        Assert.False(empty.HasMore);
    }

    [Fact]
    public async Task CompositeIdentity_KeepsSamePackageInTwoSourcesSeparate()
    {
        var service = new MockCatalogService();
        var page = await service.BrowseAsync(new CatalogQuery());
        var apps = page.Items.Where(a => a.Id.PackageId == new PackageId("sample.notes")).ToArray();
        Assert.Collection(apps,
            a => Assert.Equal(MockCatalogService.OfficialSourceId, a.Id.SourceId),
            a => Assert.Equal(MockCatalogService.CommunitySourceId, a.Id.SourceId));
        Assert.NotEqual(apps[0].Id, apps[1].Id);
        Assert.NotEqual(apps[0].Id.ToString(), apps[1].Id.ToString());
        foreach (var app in apps)
        {
            var detail = await service.GetAppAsync(app.Id);
            Assert.Equal(app, detail.App);
            Assert.All(detail.Releases, r => Assert.Equal(app.Id, r.AppId));
        }
        var unknownSource = new SourceInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAppAsync(new AppId(unknownSource, apps[0].Id.PackageId)));
    }

    [Fact]
    public async Task Refresh_ReportsOrderedProgressAndRaisesOneUpdateWithoutChangingSnapshot()
    {
        var service = new MockCatalogService();
        var before = await service.BrowseAsync(new CatalogQuery { IncludeArchived = true });
        var reports = new List<RefreshProgress>();
        var events = new List<CatalogUpdatedEventArgs>();
        service.CatalogUpdated += (_, e) => events.Add(e);
        await service.RefreshAsync(RefreshScope.All, new InlineProgress<RefreshProgress>(reports.Add));
        Assert.NotEmpty(reports);
        Assert.Equal(Enumerable.Range(0, before.Total + 1), reports.Select(p => p.Done));
        Assert.All(reports, p =>
        {
            Assert.Equal(before.Total, p.Total);
            Assert.False(string.IsNullOrWhiteSpace(p.Message));
        });
        Assert.Equal(before.Total, Assert.Single(events).Count);
        var after = await service.BrowseAsync(new CatalogQuery { IncludeArchived = true });
        Assert.Equal(before.Items, after.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_CancellationDoesNotPublishAnUpdate(bool cancelBeforeStart)
    {
        var service = new MockCatalogService();
        using var cancellation = new CancellationTokenSource();
        var reports = new List<RefreshProgress>();
        var eventCount = 0;
        service.CatalogUpdated += (_, _) => eventCount++;
        var progress = new InlineProgress<RefreshProgress>(p =>
        {
            reports.Add(p);
            if (p.Done == 1) cancellation.Cancel();
        });
        if (cancelBeforeStart) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RefreshAsync(RefreshScope.All, progress, cancellation.Token));
        Assert.Equal(0, eventCount);
        if (cancelBeforeStart) Assert.Empty(reports);
        else Assert.Equal(new[] { 0, 1 }, reports.Select(p => p.Done));
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
