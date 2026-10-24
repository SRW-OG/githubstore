using System.Text;
using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>
/// 把当前本地软件库导出成可再次同步的 <c>schemaVersion 1</c> 文件。
/// 导出只做格式转换与校验，不访问网络、不修改任何安装或设置。
/// 远端维护的证据字段原样保留：导出不是重新核验，不能凭空生成来源证据。
/// </summary>
public static class CatalogLibraryExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>导出上限，与同步侧的读取上限保持一致，避免导出出无法被自己读回的文件。</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 导出一份完整软件库。条目按仓库键排序，保证同样的输入得到字节稳定的输出。
    /// </summary>
    /// <exception cref="RepositoryCatalogException">
    /// 条目缺少同步所需的身份或核验信息时抛出；绝不导出半成品让别人同步到。
    /// </exception>
    /// <param name="curatedAt">
    /// 写入文件的整理时间。由调用方显式提供，导出的字节才能在同一份输入上保持稳定；
    /// 在这里读取当前时间会让同一份软件库每次导出都产生不同的文件。
    /// </param>
    public static async Task<byte[]> ExportAsync(IReadOnlyList<AppDetail> entries,
        IReadOnlyList<Category> categories, DateTimeOffset curatedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(categories);
        if (curatedAt == default) throw new RepositoryCatalogException("invalid_response");
        ct.ThrowIfCancellationRequested();
        var known = categories.Select(category => category.Id).ToHashSet();
        var exported = new List<object>(entries.Count);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(item => $"{item.App.Owner}/{item.App.Repo}", StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.App.Id.ToString(), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var curation = Validate(entry, known);
            var key = $"{entry.App.Owner}/{entry.App.Repo}";
            if (!keys.Add(key)) throw new RepositoryCatalogException("invalid_response");
            exported.Add(new ExportedEntry(
                entry.App.PrimaryCategory.Value,
                entry.App.FetchedAt,
                BuildRepositorySnapshot(entry.App),
                BuildReleaseSnapshot(entry),
                curation,
                entry.PackagePolicy,
                entry.DescriptionOverride));
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ExportedLibrary(1, curatedAt, exported), Json);
        if (payload.Length > MaximumBytes) throw new RepositoryCatalogException("cache_write_failed");
        // 先自校验：导出的文件必须能被本程序自己的加载器读回，否则不交给用户。
        try
        {
            using var stream = new MemoryStream(payload);
            var readBack = await CuratedCatalogLoader.LoadAsync(stream, ct).ConfigureAwait(false);
            if (readBack.Count != entries.Count) throw new RepositoryCatalogException("invalid_response");
        }
        catch (RepositoryCatalogException) { throw; }
        return payload;
    }

    /// <summary>
    /// 条目只有具备同步侧要求的最小身份与核验信息才可导出。
    /// 缺什么就拒绝什么，不用占位文本把缺口糊过去。
    /// </summary>
    private static AppCuration Validate(AppDetail entry, HashSet<CategoryId> knownCategories)
    {
        if (entry.Curation is not { } curation) throw new RepositoryCatalogException("invalid_response");
        CuratedCatalogLoader.ValidateCuration(curation, entry.App);
        if (!knownCategories.Contains(entry.App.PrimaryCategory)) throw new RepositoryCatalogException("invalid_response");
        if (entry.App.FetchedAt == default) throw new RepositoryCatalogException("invalid_response");
        if (string.IsNullOrWhiteSpace(entry.App.Owner) || string.IsNullOrWhiteSpace(entry.App.Repo))
            throw new RepositoryCatalogException("invalid_response");
        if (entry.PackagePolicy is not null) PackagePolicyValidator.Validate(entry.PackagePolicy);
        return curation;
    }

    /// <summary>
    /// 重建仓库快照。只写同步侧解析器真正读取的字段，避免导出文件里出现无出处的伪快照字段。
    /// </summary>
    private static object BuildRepositorySnapshot(App app) => new
    {
        name = app.Repo,
        full_name = $"{app.Owner}/{app.Repo}",
        owner = new { login = app.Owner },
        description = app.Description,
        homepage = app.Homepage,
        language = app.Language,
        license = new { spdx_id = app.License },
        stargazers_count = app.Stars,
        forks_count = app.Forks,
        open_issues_count = app.OpenIssues,
        archived = app.Archived,
        is_template = app.IsTemplate,
        default_branch = app.DefaultBranch,
        pushed_at = app.PushedAt,
        created_at = app.CreatedAt,
        topics = app.Topics,
        @private = false,
    };

    private static object? BuildReleaseSnapshot(AppDetail entry)
    {
        var release = entry.Releases.FirstOrDefault();
        if (release is null) return null;
        return new
        {
            tag_name = release.TagName,
            name = release.Name,
            body = release.Body,
            prerelease = release.Prerelease,
            draft = release.Draft,
            published_at = release.PublishedAt,
            assets = release.Assets.Select(asset => new
            {
                name = asset.Name,
                size = asset.Size,
                content_type = asset.ContentType,
                browser_download_url = asset.DownloadUrl,
                digest = asset.Digest,
                download_count = asset.DownloadCount,
            }).ToArray(),
        };
    }

    private sealed record ExportedLibrary(int SchemaVersion, DateTimeOffset CuratedAt, IReadOnlyList<object> Entries);
    private sealed record ExportedEntry(string CategoryId, DateTimeOffset FetchedAt, object Repository, object? Release,
        AppCuration Curation, PackagePolicy? PackagePolicy, string? DescriptionOverride);
}
