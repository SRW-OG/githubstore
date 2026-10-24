using System.Text;
using System.Text.Json.Nodes;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 导出的软件库必须能被本程序自己的加载器读回。导出不做网络访问，
/// 也不能凭空生成来源证据或核验结论。
/// </summary>
public sealed class CatalogLibraryExportTests
{
    [Fact]
    public async Task ExportRoundTripsThroughTheRealLoader()
    {
        var (entries, categories) = await LibraryAsync();
        var payload = await CatalogLibraryExporter.ExportAsync(entries, categories, Stamp);

        using var stream = new MemoryStream(payload);
        var readBack = await CuratedCatalogLoader.LoadAsync(stream);

        Assert.Equal(entries.Count, readBack.Count);
        var original = entries.OrderBy(e => $"{e.App.Owner}/{e.App.Repo}", StringComparer.OrdinalIgnoreCase).First();
        var reloaded = readBack.First(e => e.App.Id.PackageId == original.App.Id.PackageId);
        Assert.Equal(original.App.DisplayName, reloaded.App.DisplayName);
        Assert.Equal(original.App.Owner, reloaded.App.Owner);
        Assert.Equal(original.App.Repo, reloaded.App.Repo);
        Assert.Equal(original.App.Stars, reloaded.App.Stars);
        Assert.Equal(original.App.License, reloaded.App.License);
        Assert.Equal(original.App.PrimaryCategory, reloaded.App.PrimaryCategory);
        // 证据字段必须原样保留：导出不是重新核验。
        Assert.Equal(original.Curation!.Verification.RepositoryUrl, reloaded.Curation!.Verification.RepositoryUrl);
        Assert.Equal(original.Curation.DescriptionZh, reloaded.Curation.DescriptionZh);
    }

    [Fact]
    public async Task ExportPreservesPackagePolicy()
    {
        var (entries, categories) = await LibraryAsync();
        var withPolicy = entries.Select(entry => entry.PackagePolicy is null ? entry : entry with
        {
            PackagePolicy = new PackagePolicy
            {
                ReleaseChannel = PackageReleaseChannel.Stable,
                InstallFormat = PackageInstallFormat.PortableArchive,
                AssetRules = [new PackageAssetRule { Architecture = Arch.Amd64, Include = ["Product-*_x64-portable.zip"] }],
            },
        }).ToArray();

        var payload = await CatalogLibraryExporter.ExportAsync(withPolicy, categories, Stamp);
        using var stream = new MemoryStream(payload);
        var readBack = await CuratedCatalogLoader.LoadAsync(stream);

        var policy = readBack.Select(e => e.PackagePolicy).First(p => p is not null)!;
        Assert.Equal(PackageInstallFormat.PortableArchive, policy.InstallFormat);
        Assert.Equal(Arch.Amd64, Assert.Single(policy.AssetRules).Architecture);
        Assert.Equal("Product-*_x64-portable.zip", Assert.Single(Assert.Single(policy.AssetRules).Include));
    }

    [Fact]
    public async Task ExportIsDeterministicForTheSameInput()
    {
        var (entries, categories) = await LibraryAsync();
        var first = await CatalogLibraryExporter.ExportAsync(entries, categories, Stamp);
        var shuffled = entries.Reverse().ToArray();
        var second = await CatalogLibraryExporter.ExportAsync(shuffled, categories, Stamp);
        Assert.Equal(Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(second));
    }

    [Fact]
    public async Task ExportRefusesEntriesWithoutVerificationEvidence()
    {
        var (entries, categories) = await LibraryAsync();
        // 缺少核验信息时不能导出：否则同步方会得到一份没有出处的条目。
        var stripped = entries.Select(e => e with { Curation = null }).ToArray();
        await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => CatalogLibraryExporter.ExportAsync(stripped, categories, Stamp));
    }

    [Fact]
    public async Task ExportRefusesUnknownCategory()
    {
        var (entries, categories) = await LibraryAsync();
        await Assert.ThrowsAsync<RepositoryCatalogException>(
            () => CatalogLibraryExporter.ExportAsync(entries, [], Stamp));
    }

    [Fact]
    public async Task LocalExportRoundTripsManualCatalogOnlyAndCuratedEntriesWithEveryRelease()
    {
        var (curated, categories) = await LibraryAsync();
        var manual = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "Manual"),
            ApiFixture.ReleaseJson("https://github.com/fixture/Manual", "v1", ("Product-x64.zip", 123)), Stamp);
        var stable = manual.Releases[0];
        var prerelease = stable with
        {
            Key = "prerelease:v2-beta", TagName = "v2-beta", Prerelease = true,
            Assets = stable.Assets.Select(asset => asset with
            {
                ReleaseKey = "prerelease:v2-beta", InstallerId = "stable-preserved-asset-id",
                ApiUrl = "https://api.github.com/repos/fixture/Manual/releases/assets/123", Digest = "sha256:fixture",
            }).ToArray(),
        };
        manual = manual with
        {
            App = manual.App with { DisplayName = "Local display name", SourceRevision = 7, Categories = [new("system"), new("dev-tools")] },
            Releases = [stable, prerelease], DescriptionOverride = "Local description",
            PackagePolicy = new PackagePolicy { ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "v2-beta", PreservePaths = ["data"] },
            ReadmeMeta = new ReadmeMeta { HtmlUrl = "https://github.com/fixture/Manual/blob/main/README.md", Sha = "actual-readme-sha", FetchedAt = Stamp },
            Screenshots = ["https://example.com/screenshot.png"],
            Capability = new Capability { AppId = manual.App.Id, ReleaseKey = stable.Key, InstallerId = stable.Assets[0].InstallerId },
        };
        var catalogOnly = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "NoRelease"), null, Stamp);
        var payload = await CatalogLibraryExporter.ExportLocalAsync([manual, catalogOnly, curated[0]], categories, Stamp);
        using var stream = new MemoryStream(payload);
        var result = await CatalogLibraryExporter.ImportLocalAsync(stream);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(categories.Count, result.Categories.Count);
        var restored = Assert.Single(result.Entries, entry => entry.App.Repo == "Manual");
        Assert.Null(restored.Curation);
        Assert.Null(restored.Capability);
        Assert.Null(restored.Install);
        Assert.Null(restored.Summary);
        Assert.Equal("Local display name", restored.App.DisplayName);
        Assert.Equal(7, restored.App.SourceRevision);
        Assert.Equal("Local description", restored.DescriptionOverride);
        Assert.Equal(restored.DescriptionOverride, restored.App.Description);
        Assert.Equal(2, restored.App.Categories.Count);
        Assert.Equal(2, restored.Releases.Count);
        Assert.Equal("v2-beta", restored.PackagePolicy!.PinnedTag);
        Assert.Equal("v2-beta", DefaultAssetSelectionPolicy.SelectRelease(restored)!.TagName);
        Assert.Equal("stable-preserved-asset-id", restored.Releases[1].Assets[0].InstallerId);
        Assert.Equal(prerelease.Assets[0].ApiUrl, restored.Releases[1].Assets[0].ApiUrl);
        Assert.Equal("sha256:fixture", restored.Releases[1].Assets[0].Digest);
        Assert.Equal("actual-readme-sha", restored.ReadmeMeta.Sha);
        Assert.Equal(manual.Screenshots, restored.Screenshots);
        Assert.Empty(Assert.Single(result.Entries, entry => entry.App.Repo == "NoRelease").Releases);
        Assert.NotNull(Assert.Single(result.Entries, entry => entry.App.Id == curated[0].App.Id).Curation);
    }

    [Fact]
    public async Task LocalFormatNeverPassesPublicCuratedLoaderAndCuratedFormatIsNotALocalBackup()
    {
        var (entries, categories) = await LibraryAsync();
        var local = await CatalogLibraryExporter.ExportLocalAsync(entries, categories, Stamp);
        using var localStream = new MemoryStream(local);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => CuratedCatalogLoader.LoadAsync(localStream))).Code);
        var curated = await CatalogLibraryExporter.ExportAsync(entries, categories, Stamp);
        using var curatedStream = new MemoryStream(curated);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => CatalogLibraryExporter.ImportLocalAsync(curatedStream))).Code);
    }

    [Fact]
    public async Task CuratedRoundTripRestoresDescriptionAndExportsConfiguredRelease()
    {
        var (entries, categories) = await LibraryAsync();
        var entry = entries.First(value => value.Releases.Count > 0);
        var release = entry.Releases[0];
        var pinned = release with { Key = "prerelease:fixture-pin", TagName = "fixture-pin", Prerelease = true,
            Assets = release.Assets.Select(asset => asset with { ReleaseKey = "prerelease:fixture-pin" }).ToArray() };
        entry = entry with { DescriptionOverride = "Actual local description", Releases = [release, pinned],
            PackagePolicy = new PackagePolicy { ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "fixture-pin" } };
        var payload = await CatalogLibraryExporter.ExportAsync([entry], categories, Stamp);
        using var stream = new MemoryStream(payload);
        var restored = Assert.Single(await CuratedCatalogLoader.LoadAsync(stream));
        Assert.Equal("Actual local description", restored.DescriptionOverride);
        Assert.Equal(restored.DescriptionOverride, restored.App.Description);
        Assert.Equal("fixture-pin", Assert.Single(restored.Releases).TagName);
        Assert.Equal(entry.Curation!.Verification.RepositoryUrl, restored.Curation!.Verification.RepositoryUrl);
    }

    [Fact]
    public async Task LocalExportIsDeterministicWithoutDiscardingManualEntries()
    {
        var (entries, categories) = await LibraryAsync();
        var manual = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "Manual"), null, Stamp);
        var first = await CatalogLibraryExporter.ExportLocalAsync(entries.Append(manual).ToArray(), categories, Stamp);
        var second = await CatalogLibraryExporter.ExportLocalAsync(entries.Reverse().Prepend(manual).ToArray(), categories.Reverse().ToArray(), Stamp);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("releaseIdentity")]
    [InlineData("assetRelease")]
    [InlineData("category")]
    [InlineData("description")]
    [InlineData("format")]
    [InlineData("version")]
    public async Task LocalReaderRejectsMalformedMetadata(string fault)
    {
        var (entries, categories) = await LibraryAsync();
        var entry = entries.First(value => value.Releases.Any(release => release.Assets.Count > 0));
        var root = JsonNode.Parse(await CatalogLibraryExporter.ExportLocalAsync([entry], categories, Stamp))!;
        var value = root["entries"]![0]!;
        if (fault == "identity") value["app"]!["owner"] = "other";
        if (fault == "releaseIdentity") value["releases"]![0]!["appId"]!["packageId"]!["value"] = "other/repo";
        if (fault == "assetRelease") value["releases"]![0]!["assets"]![0]!["releaseKey"] = "wrong";
        if (fault == "category") value["app"]!["primaryCategory"]!["value"] = "unknown-category";
        if (fault == "description") value["descriptionOverride"] = "unsafe\0description";
        if (fault == "format") root["format"] = "other-format";
        if (fault == "version") root["schemaVersion"] = 2;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => CatalogLibraryExporter.ImportLocalAsync(stream))).Code);
    }

    [Fact]
    public async Task LocalImportBoundsInputAndHonorsCancellation()
    {
        using var oversized = new MemoryStream(new byte[CatalogLibraryExporter.MaximumBytes + 1]);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => CatalogLibraryExporter.ImportLocalAsync(oversized))).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var empty = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CatalogLibraryExporter.ImportLocalAsync(empty, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CatalogLibraryExporter.ExportLocalAsync([], [], Stamp, cancelled.Token));
    }

    [Fact]
    public async Task LocalSnapshotContainsAllEntriesWithoutHttpAndIsDetachedFromLiveCollections()
    {
        using var directory = new TempDirectory();
        using var client = new HttpClient(new OfflineHandler());
        var service = new GitHubCatalogService(client, directory.DirectoryPath, includeCuratedSeed: true);
        var snapshot = await ((ILocalCatalogSnapshotService)service).ReadLocalSnapshotAsync();
        var page = await service.BrowseAsync(new CatalogQuery { Limit = int.MaxValue, IncludeArchived = true });
        Assert.Equal(page.Total, snapshot.Entries.Count);
        Assert.NotEmpty(snapshot.Categories);
        var detached = snapshot.Entries.First(entry => entry.Releases.Count > 0);
        var live = await service.GetAppAsync(detached.App.Id);
        Assert.NotSame(live, detached);
        Assert.NotSame(live.App.Topics, detached.App.Topics);
        Assert.NotSame(live.Releases, detached.Releases);
        Assert.NotSame(live.Releases[0].Assets, detached.Releases[0].Assets);
        Assert.Equal(live.Releases[0].TagName, detached.Releases[0].TagName);
    }

    [Fact]
    public async Task LocalSnapshotRefusesToExportFallbackSeedAfterCacheReadFailure()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.Combine("github-catalog.json"), "malformed");
        using var client = new HttpClient(new OfflineHandler());
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        Assert.Equal("cache_read_failed", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.ReadLocalSnapshotAsync())).Code);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Local snapshot must not send HTTP.");
    }

    private static readonly DateTimeOffset Stamp = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(IReadOnlyList<AppDetail> Entries, IReadOnlyList<Category> Categories)> LibraryAsync()
    {
        await using var stream = typeof(GitHubCatalogService).Assembly
            .GetManifestResourceStream(CuratedCatalogLoader.ResourceName)!;
        var entries = await CuratedCatalogLoader.LoadAsync(stream);
        using var directory = new TempDirectory();
        using var client = new HttpClient(new OfflineHandler());
        var services = new GitHubCatalogService(client, directory.DirectoryPath, includeCuratedSeed: true);
        var categories = await services.ListCategoriesAsync();
        return (entries, categories);
    }
}
