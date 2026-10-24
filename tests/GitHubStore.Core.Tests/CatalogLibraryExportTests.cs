using System.Text;
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

    private static readonly DateTimeOffset Stamp = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(IReadOnlyList<AppDetail> Entries, IReadOnlyList<Category> Categories)> LibraryAsync()
    {
        await using var stream = typeof(GitHubCatalogService).Assembly
            .GetManifestResourceStream(CuratedCatalogLoader.ResourceName)!;
        var entries = await CuratedCatalogLoader.LoadAsync(stream);
        var services = new GitHubCatalogService(new HttpClient(new HttpClientHandler()), Path.GetTempPath(), includeCuratedSeed: true);
        var categories = await services.ListCategoriesAsync();
        return (entries, categories);
    }
}
