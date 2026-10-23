using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class HostedCatalogFileTests
{
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
