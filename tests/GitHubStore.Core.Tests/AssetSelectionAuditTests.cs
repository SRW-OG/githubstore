using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 审计：对随包发布的目录里所有软件运行真实选包策略，把默认选中的资产写入报告文件，
/// 用于发现“本应选中绿色包却退回到安装器”的情况。报告不参与断言内容。
/// </summary>
public sealed class AssetSelectionAuditTests
{
    [Fact]
    public async Task ReportSelectionForEveryCuratedRepository()
    {
        using var stream = File.OpenRead(FindCatalogPath());
        var entries = await CuratedCatalogLoader.LoadAsync(stream);
        var lines = new List<string>();
        var skippedInstaller = new List<string>();
        var total = 0;
        foreach (var detail in entries)
        {
            var release = detail.Releases.FirstOrDefault();
            if (release is null || release.Assets.Count == 0) continue;
            total++;
            var key = $"{detail.App.Owner}/{detail.App.Repo}";
            var selected = DefaultAssetSelectionPolicy.Select(release.Assets, Arch.Amd64, key);
            lines.Add($"{key} => {selected?.Name ?? "<none>"}");
            lines.Add("    assets: " + string.Join(" | ", release.Assets.Select(a => a.Name)));

            // 只报告“存在非 ARM 的压缩包，但默认选中了安装器”的情况，作为人工复核线索。
            if (selected is not null && Path.GetExtension(selected.Name).ToLowerInvariant() is ".exe" or ".msi")
            {
                var archives = release.Assets
                    .Where(a => Path.GetExtension(a.Name).ToLowerInvariant() is ".zip" or ".7z")
                    .Select(a => a.Name).ToArray();
                if (archives.Length > 0)
                    skippedInstaller.Add($"{key}: selected={selected.Name}; archives={string.Join(", ", archives)}");
            }
        }
        lines.Insert(0, $"total={total}");
        lines.Add("--- selected installer while archives exist ---");
        lines.AddRange(skippedInstaller.Select(s => "  " + s));

        var report = Path.Combine(FindRepositoryRoot(), "artifacts", "asset-selection-audit.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllLinesAsync(report, lines);
        Assert.True(total > 0);
    }

    private static string FindCatalogPath()
        => Path.Combine(FindRepositoryRoot(), "catalog", "catalog.json");

    private static string FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GitHubStore.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
