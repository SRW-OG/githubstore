using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class HostedCatalogFileTests
{
    [Fact]
    public void ShippedDefaultAddressPointsAtTheVersionedHostedCatalogFile()
    {
        var address = AppPreferences.DefaultCatalogLibraryUrl;
        // 客户端默认地址必须就是本仓库中的目录文件，否则软件库无法随仓库更新。
        Assert.Equal(address, PreferencesService.NormalizeCatalogLibraryUrl(address));
        Assert.EndsWith("/catalog/catalog.json", new Uri(address).AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("/SRW-OG/githubstore/", address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishedCatalogCanBeLoadedByTheClient()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GitHubStore.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "catalog", "catalog.json");
        Assert.InRange(new FileInfo(path).Length, 1, 8 * 1024 * 1024);
        await using var stream = File.OpenRead(path);
        // Use the real client parser so a catalog-only pull request validates the hosted file too.
        var entries = await CuratedCatalogLoader.LoadAsync(stream);
        Assert.NotNull(entries);
    }
}
