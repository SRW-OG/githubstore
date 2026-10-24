using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class PackagePolicyTests
{
    [Fact]
    public void ExplicitRulesSelectUnmarkedArchivesAndFailClosed()
    {
        var archive = Asset("Product-1.0-x64.zip");
        var fallback = Asset("Product-setup-x64.exe");
        var policy = Policy("Product-*-x64.zip");
        Assert.Same(fallback, DefaultAssetSelectionPolicy.Select([archive, fallback], Arch.Amd64));
        Assert.Same(archive, DefaultAssetSelectionPolicy.Select([archive, fallback], Arch.Amd64, null, policy));
        Assert.Null(DefaultAssetSelectionPolicy.Select([fallback], Arch.Amd64, null, policy));
        Assert.Null(DefaultAssetSelectionPolicy.Select([archive], Arch.Arm64, null, policy));
        Assert.Null(DefaultAssetSelectionPolicy.Select([archive], Arch.Amd64, null, policy with
        { AssetRules = [new PackageAssetRule { Architecture = Arch.Amd64, Include = ["*.zip"], Exclude = ["PRODUCT-?.0-*.zip"] }] }));
    }

    [Theory]
    [InlineData("Product-linux-x64.zip")]
    [InlineData("Product-macos-x64.zip")]
    [InlineData("Product-source-x64.zip")]
    [InlineData("Product-debug-x64.zip")]
    [InlineData("Product-arm64.zip")]
    [InlineData("Product-x64-arm64.zip")]
    [InlineData("Product.sha256")]
    public void ExplicitRulesNeverOverridePlatformArchitectureOrSourceExclusions(string name)
        => Assert.Null(DefaultAssetSelectionPolicy.Select([Asset(name)], Arch.Amd64, null, Policy("*")));

    [Fact]
    public void SourceMetadataCannotBeOverriddenAndFormatMustMatchExtension()
    {
        Assert.Null(DefaultAssetSelectionPolicy.Select([Asset("app.zip") with { Kind = AssetKind.SourceArchive }], Arch.Amd64, null, Policy("*")));
        Assert.Null(DefaultAssetSelectionPolicy.Select([Asset("app.exe")], Arch.Amd64, null,
            Policy("*") with { InstallFormat = PackageInstallFormat.PortableArchive }));
        Assert.Null(DefaultAssetSelectionPolicy.Select([Asset("app-setup.zip")], Arch.Amd64, null,
            Policy("*") with { InstallFormat = PackageInstallFormat.PortableArchive }));
        Assert.Same(AssetInstance, DefaultAssetSelectionPolicy.Select([AssetInstance], Arch.Amd64, null,
            new PackagePolicy { InstallFormat = PackageInstallFormat.PortableArchive }));
    }
    private static readonly Asset AssetInstance = Asset("Product.zip");

    [Theory]
    [InlineData("../app.exe")]
    [InlineData("C:\\app.exe")]
    [InlineData("/app.exe")]
    [InlineData("\\\\server\\app.exe")]
    [InlineData("bin/../app.exe")]
    [InlineData("bin//app.exe")]
    [InlineData("app.exe:stream")]
    [InlineData("bin/CON.txt")]
    [InlineData("bin/app. ")]
    [InlineData("bin/*.exe")]
    public void MetadataRejectsUnsafePaths(string path)
    {
        Assert.Throws<RepositoryCatalogException>(() => PackagePolicyValidator.Validate(new PackagePolicy { ExecutableRelativePath = path }));
        Assert.Throws<RepositoryCatalogException>(() => PackagePolicyValidator.Validate(new PackagePolicy { PreservePaths = [path] }));
    }

    [Fact]
    public void ValidationBoundsGlobsAndEnforcesPinnedChannel()
    {
        PackagePolicyValidator.Validate(new PackagePolicy { ExecutableRelativePath = "bin/Product.exe", PreservePaths = ["data", "config/settings.json"] });
        foreach (var pattern in new[] { "", "../*.zip", "folder/*.zip", "[a-z]*.zip", new string('*', 257) })
            Assert.Throws<RepositoryCatalogException>(() => PackagePolicyValidator.Validate(Policy(pattern)));
        Assert.Throws<RepositoryCatalogException>(() => PackagePolicyValidator.Validate(Policy("*") with
        { AssetRules = Enumerable.Range(0, 5).Select(_ => new PackageAssetRule()).ToArray() }));
        Assert.Throws<RepositoryCatalogException>(() => PackagePolicyValidator.Validate(new PackagePolicy { ReleaseChannel = PackageReleaseChannel.Pinned }));
        Assert.Throws<RepositoryCatalogException>(() => PackagePolicyValidator.Validate(new PackagePolicy { PinnedTag = "v1" }));
    }

    [Fact]
    public async Task LoaderReadsOptionalPolicyAndRejectsInvalidPolicyAsInvalidResponse()
    {
        var root = await Library();
        root["entries"]![0]!["packagePolicy"] = JsonNode.Parse("""
            {"releaseChannel":"Pinned","pinnedTag":"v1","installFormat":"PortableArchive",
             "assetRules":[{"architecture":"Amd64","include":["Product-*.zip"],"exclude":["*-symbols.zip"]}],
             "executableRelativePath":"bin/Product.exe","preservePaths":["data"]}
            """);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var detail = Assert.Single(await CuratedCatalogLoader.LoadAsync(stream));
        Assert.Equal(PackageReleaseChannel.Pinned, detail.PackagePolicy!.ReleaseChannel);
        Assert.Equal(Arch.Amd64, Assert.Single(detail.PackagePolicy.AssetRules).Architecture);
        root["entries"]![0]!["packagePolicy"]!["executableRelativePath"] = "../escape.exe";
        using var bad = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => CuratedCatalogLoader.LoadAsync(bad))).Code);
    }

    [Fact]
    public async Task PolicyUpdateRefreshAndRestartPreserveManualMetadataAndPinnedRelease()
    {
        using var directory = new TempDirectory();
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri.AbsolutePath.Contains("/releases/")
                ? ApiFixture.ReleaseJson("https://github.com/fixture/Tool", request.RequestUri.AbsolutePath.EndsWith("/latest") ? "v2" : "v1", ("Product.zip", 20))
                : ApiFixture.RepositoryJson("fixture", "Tool"));
        }));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var added = await service.AddAsync("fixture/Tool");
        // Remove unrelated bundled membership to keep refresh requests focused on this manual app.
        var bundled = (await service.BrowseAsync(new CatalogQuery())).Items.First(a => a.Id != added.App.Id);
        await service.RemoveAsync(bundled.Id);
        var policy = Policy("Product.zip") with
        { ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "v1", ExecutableRelativePath = "bin/Product.exe", PreservePaths = ["data"] };
        var updated = await service.UpdatePackagePolicyAsync(added.App.Id, policy);
        Assert.Null(updated.Curation);
        Assert.Equal("v1", Assert.Single(updated.Releases).TagName);
        Assert.Contains("/repos/fixture/Tool/releases/tags/v1", paths);
        await service.RefreshAsync(RefreshScope.All);
        Assert.Equal(policy, (await service.GetAppAsync(added.App.Id)).PackagePolicy);
        var restarted = new GitHubCatalogService(new HttpClient(new Handler(_ => throw new InvalidOperationException("Offline"))), directory.DirectoryPath);
        var restored = await restarted.GetAppAsync(added.App.Id);
        Assert.Equal("bin/Product.exe", restored.PackagePolicy!.ExecutableRelativePath);
        Assert.Equal("data", Assert.Single(restored.PackagePolicy.PreservePaths));
        var cleared = await service.UpdatePackagePolicyAsync(added.App.Id, null);
        Assert.Null(cleared.PackagePolicy);
        Assert.Equal("v2", Assert.Single(cleared.Releases).TagName);
    }

    [Fact]
    public async Task LocalPolicyOverrideSurvivesLibrarySyncRestartAndClear()
    {
        using var directory = new TempDirectory();
        var library = await Library();
        var entry = library["entries"]![0]!;
        var repository = entry["repository"]!.ToJsonString();
        var appName = entry["repository"]!["name"]!.GetValue<string>();
        var owner = entry["repository"]!["owner"]!["login"]!.GetValue<string>();
        entry["packagePolicy"] = JsonSerializer.SerializeToNode(new PackagePolicy { InstallFormat = PackageInstallFormat.Installer },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var client = new HttpClient(new Handler(request => Json(request.RequestUri!.Host == "api.github.com"
            ? request.RequestUri.AbsolutePath.Contains("/releases/")
                ? ApiFixture.ReleaseJson($"https://github.com/{owner}/{appName}", "v1", ("Product.zip", 20)) : repository
            : library.ToJsonString())));
        var service = new GitHubCatalogService(client, directory.DirectoryPath, includeCuratedSeed: true);
        await service.SyncLibraryAsync("https://example.com/library.json");
        var app = Assert.Single((await service.BrowseAsync(new CatalogQuery { IncludeArchived = true })).Items);
        var policy = new PackagePolicy { InstallFormat = PackageInstallFormat.PortableArchive, ExecutableRelativePath = "bin/Product.exe" };
        await service.UpdatePackagePolicyAsync(app.Id, policy);
        await service.SyncLibraryAsync("https://example.com/library.json");
        Assert.Equal(policy, (await service.GetAppAsync(app.Id)).PackagePolicy);
        var restarted = new GitHubCatalogService(client, directory.DirectoryPath, includeCuratedSeed: true);
        Assert.Equal("bin/Product.exe", (await restarted.GetAppAsync(app.Id)).PackagePolicy!.ExecutableRelativePath);
        await restarted.UpdatePackagePolicyAsync(app.Id, null);
        await restarted.SyncLibraryAsync("https://example.com/library.json");
        Assert.Null((await restarted.GetAppAsync(app.Id)).PackagePolicy);
        var cleared = new GitHubCatalogService(client, directory.DirectoryPath, includeCuratedSeed: true);
        Assert.Null((await cleared.GetAppAsync(app.Id)).PackagePolicy);
    }

    [Fact]
    public async Task PrereleaseChannelSkipsDraftAndStableAndChoosesNewestPublished()
    {
        using var directory = new TempDirectory();
        var releases = new JsonArray(ReleaseNode("new", true, false, "2026-10-20T00:00:00Z"),
            ReleaseNode("draft", true, true, "2026-10-22T00:00:00Z"), ReleaseNode("stable", false, false, "2026-10-23T00:00:00Z"),
            ReleaseNode("old", true, false, "2026-10-19T00:00:00Z"));
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri.AbsolutePath.EndsWith("/releases") ? releases.ToJsonString()
                : request.RequestUri.AbsolutePath.EndsWith("/latest") ? ApiFixture.ReleaseJson("https://github.com/fixture/Tool", "v1")
                : ApiFixture.RepositoryJson("fixture", "Tool"));
        }));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var detail = await service.AddAsync("fixture/Tool");
        var result = await service.UpdatePackagePolicyAsync(detail.App.Id, new PackagePolicy { ReleaseChannel = PackageReleaseChannel.LatestPrerelease });
        Assert.Equal("new", Assert.Single(result.Releases).TagName);
        Assert.Contains("/repos/fixture/Tool/releases?per_page=100&page=1", requests);
        Assert.NotNull(DefaultAssetSelectionPolicy.Select(result, Arch.Amd64));
    }

    [Fact]
    public async Task FailedPolicyUpdateLeavesExactCacheAndPublishedPolicyUnchanged()
    {
        using var directory = new TempDirectory();
        var fail = false;
        using var client = new HttpClient(new Handler(request => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json(request.RequestUri!.AbsolutePath.EndsWith("/latest") ? ApiFixture.ReleaseJson("https://github.com/fixture/Tool", "v1") : ApiFixture.RepositoryJson("fixture", "Tool"))));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var detail = await service.AddAsync("fixture/Tool");
        var before = await File.ReadAllBytesAsync(directory.Combine("github-catalog.json"));
        fail = true;
        await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.UpdatePackagePolicyAsync(detail.App.Id,
            new PackagePolicy { ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "v0" }));
        Assert.Null((await service.GetAppAsync(detail.App.Id)).PackagePolicy);
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.Combine("github-catalog.json")));
    }

    private static PackagePolicy Policy(string include) => new() { AssetRules = [new PackageAssetRule { Architecture = Arch.Amd64, Include = [include] }] };
    private static Asset Asset(string name) => new() { Name = name, InstallerId = name, ReleaseKey = "stable:v1", DownloadUrl = "https://example.com/" + name };
    private static JsonNode ReleaseNode(string tag, bool prerelease, bool draft, string published)
    {
        var node = JsonNode.Parse(ApiFixture.ReleaseJson("https://github.com/fixture/Tool", tag, ("Product-x64.exe", 20)))!;
        node["prerelease"] = prerelease;
        node["draft"] = draft;
        node["published_at"] = published;
        return node;
    }
    private static async Task<JsonNode> Library()
    {
        await using var stream = typeof(GitHubCatalogService).Assembly.GetManifestResourceStream(CuratedCatalogLoader.ResourceName)!;
        var root = (await JsonNode.ParseAsync(stream))!;
        root["entries"] = new JsonArray(root["entries"]![0]!.DeepClone());
        return root;
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
