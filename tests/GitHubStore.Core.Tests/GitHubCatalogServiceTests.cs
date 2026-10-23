using System.Net;
using System.Net.Http;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Integration")]
public sealed class GitHubCatalogServiceTests
{
    private const string PowerToysRepository = "microsoft/PowerToys";
    private const string ToolBoxRepository = "fixture/ToolBox";
    private const string ToolBoxUrl = "https://github.com/fixture/ToolBox";

    private static HttpClient Client(StubHttpHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task Initialize_LoadsTheEmbeddedPowerToysSeedWithoutAnyHttpRequest()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("The offline seed must not require HTTP."));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        await service.InitializeAsync();

        Assert.Empty(handler.Requests);
        var page = await service.BrowseAsync(new CatalogQuery());
        var app = Assert.Single(page.Items);
        Assert.Equal("PowerToys", app.DisplayName);
        Assert.Equal("microsoft", app.Owner);
        Assert.Equal("PowerToys", app.Repo);
        Assert.Equal(139480, app.Stars);
        Assert.Contains("MIT", app.License, StringComparison.Ordinal);
        Assert.Equal(SeedFixture.FetchedAt, app.FetchedAt);
        Assert.True(app.Stale, "The shipped seed is a point-in-time snapshot, not a live reading.");
        Assert.Equal(GitHubCatalogService.ManualSourceId, app.Id.SourceId);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Fact]
    public async Task Initialize_ReusesSeedSnapshotFromDiskCacheOnRestart()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        await service.InitializeAsync();
        await service.AddAsync(ToolBoxRepository, new CategoryId("dev-tools"));

        var reopened = new GitHubCatalogService(Client(handler), directory.DirectoryPath);
        await reopened.InitializeAsync();

        var page = await reopened.BrowseAsync(new CatalogQuery());
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, app => app.Repo == "ToolBox");
        Assert.Contains(page.Items, app => app.Repo == "PowerToys");
        var restored = Assert.Single(page.Items, app => app.Repo == "ToolBox");
        Assert.True(restored.Stale, "Cached entries are snapshots until a successful refresh.");
        Assert.Equal("Synthetic fixture repository used by offline tests.", restored.Description);
    }

    [Fact]
    public async Task Preview_FetchesRepositoryAndLatestReleaseAndMapsOnlyInstallerAssets()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var detail = await service.PreviewAsync(PowerToysRepository);

        Assert.Equal("v0.101.2362.0", detail.Releases[0].TagName);
        Assert.Equal("stable:v0.101.2362.0", detail.Releases[0].Key);
        Assert.Null(detail.ReadmeMeta.FetchedAt); // README content itself was never fetched.
        Assert.Equal("https://github.com/microsoft/PowerToys/blob/main/README.md", detail.ReadmeMeta.HtmlUrl);
        Assert.True(detail.App.WindowsReady);
        Assert.False(detail.App.Stale, "A successful preview reports live metadata.");
        Assert.False(File.Exists(directory.Combine("github-catalog.json")), "Preview must not persist anything.");

        var installers = detail.Releases[0].Assets
            .Where(asset => asset.Kind is AssetKind.InstallerExe or AssetKind.InstallerMsi or AssetKind.PortableExe)
            .ToArray();
        Assert.Equal(4, installers.Length);
        Assert.All(installers, asset => Assert.EndsWith(".exe", asset.Name));
        Assert.All(installers, asset => Assert.Contains("Setup", asset.Name, StringComparison.Ordinal));
        Assert.Contains(installers, asset => asset.Arch == Arch.Amd64);
        Assert.Contains(installers, asset => asset.Arch == Arch.Arm64);
        Assert.All(installers, asset => Assert.StartsWith("https://github.com/microsoft/PowerToys/releases/download/", asset.DownloadUrl));

        // Symbols and Group Policy archives are published files, never install assets.
        var archives = detail.Releases[0].Assets
            .Where(asset => asset.Name.Contains("zip", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(3, archives.Length);
        Assert.All(archives, asset => Assert.Equal(AssetKind.ArchiveZip, asset.Kind));
        Assert.DoesNotContain(archives, asset => asset.Kind is AssetKind.InstallerExe or AssetKind.InstallerMsi);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/repos/microsoft/PowerToys", handler.Requests[0].AbsolutePath);
        Assert.Equal("/repos/microsoft/PowerToys/releases/latest", handler.Requests[1].AbsolutePath);
        Assert.All(handler.Requests, uri => Assert.Equal("api.github.com", uri.Host));
    }

    [Fact]
    public async Task Add_PersistsEntryAndRejectsTheSameRepositoryIgnoringCase()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var added = await service.AddAsync(ToolBoxRepository, new CategoryId("dev-tools"));

        Assert.True(File.Exists(directory.Combine("github-catalog.json")));
        Assert.Equal("ToolBox", added.App.Repo);
        Assert.Equal(new CategoryId("dev-tools"), added.App.PrimaryCategory);

        var duplicate = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.AddAsync("FIXTURE/toolbox", new CategoryId("dev-tools")));
        Assert.Equal("duplicate_repository", duplicate.Code);

        // The shipped seed already owns microsoft/PowerToys, so re-adding it is a duplicate too.
        var seededDuplicate = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.AddAsync("Microsoft/PowerToys", new CategoryId("system")));
        Assert.Equal("duplicate_repository", seededDuplicate.Code);
    }

    [Fact]
    public async Task Add_RejectsUnknownCategoryWithoutWritingAnything()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.AddAsync(ToolBoxRepository, new CategoryId("not-a-category")));

        Assert.Equal("invalid_category", exception.Code);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Fact]
    public async Task Remove_DropsOnlyTheRequestedEntryAndKeepsSeedAvailable()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);
        var added = await service.AddAsync(ToolBoxRepository, new CategoryId("dev-tools"));

        await service.RemoveAsync(added.App.Id);

        var page = await service.BrowseAsync(new CatalogQuery());
        Assert.Equal(PowerToysRepository.ToLowerInvariant(), Assert.Single(page.Items).Id.PackageId.Value);

        var missing = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.RemoveAsync(added.App.Id));
        Assert.Equal("repository_not_found", missing.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "repository_not_found")]
    [InlineData(HttpStatusCode.Forbidden, "access_denied")]
    [InlineData(HttpStatusCode.InternalServerError, "network_error")]
    [InlineData(HttpStatusCode.BadGateway, "network_error")]
    public async Task Preview_MapsHttpStatusToStableRepositoryErrorCodes(HttpStatusCode status, string expectedCode)
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(status));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.PreviewAsync(ToolBoxRepository));

        Assert.Equal(expectedCode, exception.Code);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Fact]
    public async Task Preview_MapsExhaustedRateLimitToRateLimited()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(_ => StubHttpHandler.RateLimited());
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.PreviewAsync(ToolBoxRepository));

        Assert.Equal("rate_limited", exception.Code);
    }

    [Fact]
    public async Task Preview_MapsNonJsonBodyToInvalidResponse()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("<html>not json</html>"));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.PreviewAsync(ToolBoxRepository));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task Preview_RequiresTheRepositoryShapeAndRejectsMissingMetadata()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{\"message\":\"Not Found\"}"));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.PreviewAsync(ToolBoxRepository));

        Assert.Equal("invalid_response", exception.Code);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheExistingCacheFileIntact()
    {
        using var directory = new TempDirectory();
        var failToolBox = false;
        var handler = new StubHttpHandler(request => Respond(request, failToolBox));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);
        await service.AddAsync(ToolBoxRepository, new CategoryId("dev-tools"));
        var cachePath = directory.Combine("github-catalog.json");
        var before = await File.ReadAllBytesAsync(cachePath);

        failToolBox = true;
        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => service.RefreshAsync(RefreshScope.All));

        Assert.Equal("network_error", exception.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(cachePath));
        var page = await service.BrowseAsync(new CatalogQuery());
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task SuccessfulRefresh_MarksEntriesAsLiveAndReportsProgress()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);
        await service.AddAsync(ToolBoxRepository, new CategoryId("dev-tools"));
        var reports = new List<RefreshProgress>();

        await service.RefreshAsync(RefreshScope.All, new SyncProgress<RefreshProgress>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(2, reports[^1].Done);
        var page = await service.BrowseAsync(new CatalogQuery());
        Assert.All(page.Items, app => Assert.False(app.Stale));
    }

    [Fact]
    public async Task Cancellation_IsObservedAsOperationCanceled()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewAsync(ToolBoxRepository, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.InitializeAsync(cancellation.Token));
    }

    [Fact]
    public async Task Search_MatchesRepositoryNameAndHonoursCategoryFilter()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);
        await service.AddAsync(ToolBoxRepository, new CategoryId("dev-tools"));

        var hit = await service.SearchAsync("toolbox", new SearchOptions());
        Assert.Equal(ToolBoxRepository.ToLowerInvariant(), Assert.Single(hit.Items).Id.PackageId.Value);
        Assert.Equal("local", hit.Source);
        Assert.Null(hit.RemoteError);

        var categorized = await service.SearchAsync("toolbox", new SearchOptions { Category = new CategoryId("system") });
        Assert.Empty(categorized.Items);

        var remote = await service.SearchAsync("toolbox", new SearchOptions { IncludeRemote = true });
        Assert.Equal("remote_search_unavailable", remote.RemoteError);
    }

    [Fact]
    public async Task Rank_OnlyReportsStarsBecauseNoGrowthHistoryExists()
    {
        using var directory = new TempDirectory();
        var handler = new StubHttpHandler(request => Respond(request, failToolBox: false));
        var service = new GitHubCatalogService(Client(handler), directory.DirectoryPath);

        var stars = await service.RankAsync(BoardKind.Stars, 10);
        Assert.Equal("PowerToys", Assert.Single(stars).DisplayName);
        Assert.Empty(await service.RankAsync(BoardKind.Trending, 10));
        Assert.Empty(await service.RankAsync(BoardKind.New, 10));
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, bool failToolBox)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("ToolBox", StringComparison.Ordinal))
        {
            if (failToolBox) return StubHttpHandler.Status(HttpStatusCode.InternalServerError);
            return path.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? StubHttpHandler.Json(ApiFixture.ReleaseJson(ToolBoxUrl, "v2.0.0",
                    ("ToolBox-win-x64.exe", 1048576), ("ToolBox-source.zip", 4096)))
                : StubHttpHandler.Json(ApiFixture.RepositoryJson("fixture", "ToolBox"));
        }
        if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            return StubHttpHandler.Json(SeedFixture.ReleaseJson);
        return StubHttpHandler.Json(SeedFixture.RepositoryJson);
    }
}
