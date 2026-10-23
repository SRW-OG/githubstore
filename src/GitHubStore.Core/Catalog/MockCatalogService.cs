using System.Diagnostics;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>
/// Offline fixture for the initial application skeleton. Every name, count,
/// release and URL is simulated; this service performs no network or install work.
/// Register the same singleton as both ICatalogService and ISearchService.
/// </summary>
public sealed class MockCatalogService : ICatalogService, ISearchService
{
    public static SourceInstanceId OfficialSourceId { get; } =
        new(new Guid("11111111-1111-4111-8111-111111111111"));
    public static SourceInstanceId CommunitySourceId { get; } =
        new(new Guid("22222222-2222-4222-8222-222222222222"));
    public static DateTimeOffset SnapshotAt { get; } =
        new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<Category> Categories = CreateCategories();
    private static readonly IReadOnlyList<Entry> Entries = CreateEntries();

    public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated;

    public async Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default)
        => await Task.Run(() => Categories, ct).ConfigureAwait(false);

    public async Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default)
        => await Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
            ArgumentOutOfRangeException.ThrowIfNegative(query.Limit);
            var matches = Sort(Visible(query.IncludeArchived)
                .Where(e => InCategory(e.App, query.Category)), query.Sort).ToArray();
            ct.ThrowIfCancellationRequested();
            var items = matches.Skip(query.Offset).Take(query.Limit).Select(e => e.App).ToArray();
            return new CatalogPage
            {
                Items = Array.AsReadOnly(items),
                Total = matches.Length,
                HasMore = (long)query.Offset + items.Length < matches.Length,
            };
        }, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default)
        => await Task.Run<IReadOnlyList<App>>(() =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit);
            var visible = Visible();
            var ranked = board switch
            {
                BoardKind.Stars => Sort(visible, SortKey.Stars),
                BoardKind.Trending => Sort(visible, SortKey.Trending),
                // The mock uses a fixed 30-day observation, never live GitHub statistics.
                BoardKind.New => Stable(visible.Where(e =>
                    e.App.CreatedAt >= SnapshotAt.AddDays(-180) && e.App.Stars >= 200)
                    .OrderByDescending(e => e.StarsGained30d)),
                _ => throw new ArgumentOutOfRangeException(nameof(board)),
            };
            ct.ThrowIfCancellationRequested();
            return Array.AsReadOnly(ranked.Take(limit).Select(e => e.App).ToArray());
        }, ct).ConfigureAwait(false);

    public async Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default)
        => await Task.Run(() =>
        {
            var app = Entries.FirstOrDefault(e => e.App.Id == id)?.App
                ?? throw new KeyNotFoundException($"模拟目录中不存在应用 {id}。");
            ct.ThrowIfCancellationRequested();
            const string releaseKey = "stable:1.0.0";
            var release = new Release
            {
                Key = releaseKey,
                AppId = app.Id,
                TagName = "v1.0.0",
                Name = "模拟版本 1.0.0",
                Body = "离线框架演示版本，未接入真实发布记录或下载。",
                PublishedAt = app.PushedAt,
                Assets = Array.AsReadOnly(new[]
                {
                    new Asset
                    {
                        InstallerId = "win-x64-zip",
                        ReleaseKey = releaseKey,
                        Name = $"{app.Repo}-1.0.0-win-x64.zip",
                        DownloadUrl = $"https://example.invalid/mock/{app.Id}/1.0.0.zip",
                        ContentType = "application/zip",
                        Size = 12 * 1024 * 1024,
                        Kind = AssetKind.ArchiveZip,
                        Arch = Arch.Amd64,
                        Installer = InstallerKind.Zip,
                    },
                }),
            };
            return new AppDetail
            {
                App = app,
                Releases = Array.AsReadOnly(new[] { release }),
                // No probing, AI generation, or installation has taken place.
                Capability = null,
                Summary = null,
                Install = null,
                ReadmeMeta = new ReadmeMeta { Path = "README.md" },
            };
        }, ct).ConfigureAwait(false);

    public async Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default)
        => await Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentOutOfRangeException.ThrowIfNegative(options.Limit);
            ArgumentOutOfRangeException.ThrowIfNegative(options.MinStars);
            var timer = Stopwatch.StartNew();
            var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matches = Sort(Visible().Where(e =>
            {
                ct.ThrowIfCancellationRequested();
                var app = e.App;
                return InCategory(app, options.Category)
                    && app.Stars >= options.MinStars
                    && (string.IsNullOrWhiteSpace(options.Language)
                        || string.Equals(app.Language, options.Language.Trim(), StringComparison.OrdinalIgnoreCase))
                    && tokens.All(token => Matches(app, token));
            }), SortKey.Stars).ToArray();
            ct.ThrowIfCancellationRequested();
            var items = Array.AsReadOnly(matches.Take(options.Limit).Select(e => e.App).ToArray());
            timer.Stop();
            return new SearchResult
            {
                Items = items,
                Total = matches.Length,
                Source = "local",
                Took = timer.Elapsed,
                RemoteError = options.IncludeRemote ? "当前为离线模拟目录，尚未接入远程搜索。" : null,
            };
        }, ct).ConfigureAwait(false);

    public async Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (scope != RefreshScope.All)
            throw new ArgumentOutOfRangeException(nameof(scope));

        // Simulates bounded work and cancellation without changing the fixed snapshot.
        progress?.Report(new RefreshProgress { Done = 0, Total = Entries.Count, Message = "正在刷新离线模拟目录" });
        for (var i = 0; i < Entries.Count; i++)
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress?.Report(new RefreshProgress
            {
                Done = i + 1,
                Total = Entries.Count,
                Message = i + 1 == Entries.Count ? "离线模拟目录刷新完成" : $"正在读取模拟条目 {i + 1}",
            });
        }
        ct.ThrowIfCancellationRequested();
        CatalogUpdated?.Invoke(this, new CatalogUpdatedEventArgs { Count = Entries.Count, At = DateTimeOffset.UtcNow });
    }

    private static IEnumerable<Entry> Visible(bool includeArchived = false)
        => Entries.Where(e => (includeArchived || !e.App.Archived)
            && (e.App.Excluded == ExcludeReason.None
                || (includeArchived && e.App.Excluded == ExcludeReason.Archived)));

    private static bool InCategory(App app, CategoryId? category)
        => category is null || app.PrimaryCategory == category.Value || app.Categories.Contains(category.Value);

    private static bool Matches(App app, string token)
        => app.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)
            || app.Owner.Contains(token, StringComparison.OrdinalIgnoreCase)
            || app.Repo.Contains(token, StringComparison.OrdinalIgnoreCase)
            || app.Id.PackageId.Value.Contains(token, StringComparison.OrdinalIgnoreCase)
            || (app.Description?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)
            || app.Topics.Any(topic => topic.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static IOrderedEnumerable<Entry> Sort(IEnumerable<Entry> entries, SortKey sort)
        => Stable(sort switch
        {
            SortKey.Stars => entries.OrderByDescending(e => e.App.Stars).ThenByDescending(e => e.StarsGained30d),
            SortKey.Trending => entries.OrderByDescending(TrendingScore).ThenByDescending(e => e.App.Stars),
            SortKey.Updated => entries.OrderByDescending(e => e.App.PushedAt),
            SortKey.Name => entries.OrderBy(e => e.App.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        });

    private static IOrderedEnumerable<Entry> Stable(IOrderedEnumerable<Entry> entries)
        => entries.ThenBy(e => e.App.Owner, StringComparer.Ordinal)
            .ThenBy(e => e.App.Repo, StringComparer.Ordinal)
            .ThenBy(e => e.App.Id.ToString(), StringComparer.Ordinal);

    private static double TrendingScore(Entry entry)
    {
        var age = (SnapshotAt - entry.App.PushedAt).TotalDays;
        var recency = Math.Clamp(1 - Math.Max(0, age - 90) / 90, 0, 1);
        return 0.55 * Math.Log10(1 + entry.StarsGained30d)
            + 0.20 * Math.Log10(1 + entry.App.Stars) + 0.15 * recency;
    }

    private static IReadOnlyList<Category> CreateCategories()
    {
        (string Id, string Name, string English)[] definitions =
        [
            ("dev-tools", "开发工具", "Developer Tools"),
            ("system", "系统工具", "System"),
            ("network", "网络", "Network"),
            ("media", "媒体影音", "Media"),
            ("graphics", "图形设计", "Graphics"),
            ("office", "办公效率", "Office"),
            ("security", "安全隐私", "Security"),
            ("games", "游戏", "Games"),
            ("ai", "AI / LLM 工具", "AI / LLM"),
            ("terminal", "终端与 Shell", "Terminal & Shell"),
            ("files", "文件管理", "Files"),
            ("science", "教育与科学", "Education & Science"),
        ];
        return Array.AsReadOnly(definitions.Select((d, index) => new Category
        {
            Id = new CategoryId(d.Id), Name = d.Name, NameEn = d.English, Order = index,
        }).ToArray());
    }

    private static IReadOnlyList<Entry> CreateEntries()
    {
        // Entirely fictional apps and metrics. science deliberately stays empty.
        Entry[] entries =
        [
            Create("sample.codecanvas", "CodeCanvas", "代码编辑与项目管理", "dev-tools", "C#", 42000, 900, 800, 2),
            Create("sample.windowpilot", "WindowPilot", "窗口布局与快捷键工具", "system", "C#", 28000, 600, 600, 4),
            Create("sample.netbridge", "NetBridge", "网络连接诊断与下载管理", "network", "Go", 18500, 720, 480, 1),
            Create("sample.framecast", "FrameCast", "桌面录制与媒体处理", "media", "C++", 36000, 1200, 1000, 6),
            Create("sample.pixelgarden", "PixelGarden", "截图标注与图形编辑", "graphics", "Rust", 12000, 450, 700, 5),
            Create("sample.notes", "PaperNotes", "本地 Markdown 笔记与知识整理", "office", "TypeScript", 22000, 1800, 400, 2),
            Create("sample.keyvault", "KeyVault Demo", "密码整理与加密演示", "security", "C#", 15000, 330, 500, 8),
            Create("sample.playdeck", "PlayDeck", "游戏资料库与启动器", "games", "C++", 8300, 210, 300, 12),
            Create("sample.modeldesk", "ModelDesk", "本地模型目录与对话界面", "ai", "Python", 46000, 6500, 120, 1),
            Create("sample.shellharbor", "ShellHarbor", "终端会话与命令工作区", "terminal", "Rust", 19000, 950, 900, 3),
            Create("sample.fileatlas", "FileAtlas", "文件浏览与目录比较", "files", "C#", 31000, 880, 650, 7),
            Create("sample.gitlantern", "GitLantern", "版本控制图形客户端", "dev-tools", "TypeScript", 12000, 520, 90, 9),
            Create("sample.clipboard", "ClipBoard Demo", "剪贴板历史与文本整理", "office", "C#", 4500, 300, 75, 1),
            Create("sample.audiowave", "AudioWave", "音频剪辑与波形查看", "media", "C++", 6300, 160, 150, 13),
            Create("sample.notes", "PaperNotes Community", "独立社区源的笔记演示，与另一来源保持身份隔离", "office", "TypeScript", 900, 130, 45, 4, community: true),
            Create("sample.legacytools", "LegacyTools", "已归档条目，用于归档筛选演示", "system", "C#", 3000, 0, 1800, 300, archived: true),
        ];
        return Array.AsReadOnly(entries);
    }

    private static Entry Create(string package, string name, string description, string category,
        string language, int stars, int gained, int createdDays, int pushedDays,
        bool community = false, bool archived = false)
    {
        var id = new CategoryId(category);
        return new Entry(new App
        {
            Id = new AppId(community ? CommunitySourceId : OfficialSourceId, new PackageId(package)),
            SourceRevision = 1,
            DisplayName = name,
            Owner = community ? "mock-community" : "mock-official",
            Repo = package["sample.".Length..],
            Description = $"模拟数据 · {description}",
            Homepage = $"https://example.invalid/mock/{package}",
            Language = language,
            Topics = Array.AsReadOnly(new[] { category, "windows", "desktop-app", "mock" }),
            Stars = stars,
            Forks = stars / 8,
            OpenIssues = stars / 300,
            Archived = archived,
            CreatedAt = SnapshotAt.AddDays(-createdDays),
            PushedAt = SnapshotAt.AddDays(-pushedDays),
            Categories = Array.AsReadOnly(new[] { id }),
            PrimaryCategory = id,
            WindowsReady = true,
            Excluded = archived ? ExcludeReason.Archived : ExcludeReason.None,
            FetchedAt = SnapshotAt,
        }, gained);
    }

    private sealed record Entry(App App, int StarsGained30d);
}
