using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>一条诊断的种类。种类决定严重程度，也决定界面该不该拦住保存。</summary>
public enum LibraryDiagnosticKind
{
    /// <summary>配置的版本策略或选包规则在当前发行版里选不出任何包。</summary>
    NoMatchingAsset,

    /// <summary>该条目没有任何发行版，因此无法安装，只能浏览。</summary>
    NoRelease,

    /// <summary>固定版本策略里的标签在当前发行版中不存在。</summary>
    PinnedTagMissing,

    /// <summary>下载地址不是 HTTPS，或带凭据、带片段。</summary>
    UnsafeDownloadUrl,

    /// <summary>发行资产没有校验摘要，安装时无法核验完整性。</summary>
    MissingDigest,
}

/// <summary>
/// 一条诊断结果。<see cref="Detail"/> 只描述这条配置本身的问题，
/// 不包含任何本地路径或凭据。
/// </summary>
public sealed record LibraryDiagnostic
{
    public required LibraryDiagnosticKind Kind { get; init; }
    public required string RepositoryKey { get; init; }
    /// <summary>只用于展示的具体说明，例如失配的文件名规则。</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// 在不联网、不安装的前提下检查软件库配置本身是否自洽。
/// 这些检查只依据库里已有的发行快照，因此“没有发现问题”不等于下载后一定能装成功。
/// </summary>
public static class CatalogLibraryDiagnostics
{
    /// <summary>诊断上限，避免超大库把界面拖垮。</summary>
    public const int MaximumDiagnostics = 500;

    public static IReadOnlyList<LibraryDiagnostic> Inspect(IEnumerable<AppDetail> entries, Arch architecture)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var diagnostics = new List<LibraryDiagnostic>();
        foreach (var entry in entries)
        {
            if (diagnostics.Count >= MaximumDiagnostics) break;
            var key = $"{entry.App.Owner}/{entry.App.Repo}";
            var policy = entry.PackagePolicy;
            if (policy is not null)
            {
                try { PackagePolicyValidator.Validate(policy); }
                catch (RepositoryCatalogException)
                {
                    Add(diagnostics, LibraryDiagnosticKind.NoMatchingAsset, key, "invalid_package_policy");
                    continue;
                }
            }
            if (entry.Releases.Count == 0)
            {
                // 没有任何发行版：条目可以浏览，但没有可安装的包。
                Add(diagnostics, LibraryDiagnosticKind.NoRelease, key, null);
                continue;
            }
            var release = DefaultAssetSelectionPolicy.SelectRelease(entry);
            if (release is null)
            {
                var kind = policy?.ReleaseChannel == PackageReleaseChannel.Pinned
                    ? LibraryDiagnosticKind.PinnedTagMissing : LibraryDiagnosticKind.NoRelease;
                Add(diagnostics, kind, key, policy?.PinnedTag);
                continue;
            }
            if (DefaultAssetSelectionPolicy.Select(entry, architecture) is null)
            {
                var detail = policy?.AssetRules.FirstOrDefault(rule => rule.Architecture == architecture) is { } rule
                    ? string.Join(",", rule.Include) : null;
                Add(diagnostics, LibraryDiagnosticKind.NoMatchingAsset, key, detail);
            }
            foreach (var asset in release.Assets)
            {
                if (diagnostics.Count >= MaximumDiagnostics) break;
                if (string.IsNullOrWhiteSpace(asset.Digest))
                    Add(diagnostics, LibraryDiagnosticKind.MissingDigest, key, asset.Name);
                // 下载地址来自库文件，必须按不可信输入校验，不能直接展示成可点击链接。
                if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                    || !string.IsNullOrEmpty(uri.Fragment))
                    Add(diagnostics, LibraryDiagnosticKind.UnsafeDownloadUrl, key, asset.Name);
            }
        }
        return diagnostics.AsReadOnly();
    }

    private static void Add(List<LibraryDiagnostic> diagnostics, LibraryDiagnosticKind kind, string key, string? detail)
    {
        if (diagnostics.Count >= MaximumDiagnostics) return;
        diagnostics.Add(new LibraryDiagnostic { Kind = kind, RepositoryKey = key, Detail = detail });
    }
}
