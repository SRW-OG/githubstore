using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

public sealed class CatalogCardTests
{
    [Fact]
    public void Card_UsesChineseCurationAndActualRepositoryMetadata()
    {
        var app = App("fixture") with
        {
            Stars = 12345, Description = "Original introduction", Topics = ["image-editor"],
            PushedAt = new DateTimeOffset(2025, 3, 4, 12, 0, 0, TimeSpan.Zero),
            FetchedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
        };
        var detail = new AppDetail
        {
            App = app, Curation = Curation("用于图片编辑的开源工具。"),
            Releases = [new() { AppId = app.Id, Key = "release", TagName = "v2.1" }],
        };
        using var card = new CatalogCardViewModel(detail);
        Assert.Equal("Original introduction", card.Description);
        Assert.Contains("用于图片编辑的开源工具。", card.FeaturesText);
        Assert.Equal("https://github.com/owner/fixture", card.RepositoryText);
        Assert.Equal(12345, card.StarCount);
        Assert.Equal(app.PushedAt, card.UpdatedAt);
        Assert.NotEqual(app.FetchedAt, card.UpdatedAt);
        Assert.Equal("v2.1", card.LatestVersion);
        Assert.DoesNotContain("暂无独立功能清单", card.FeaturesText);
        Assert.Contains("image-editor", card.FeaturesText);
        Assert.Equal("安装", card.InstallLabel);
    }

    [Fact]
    public void Card_EnglishUsesOriginalIntroAndMissingFactsStayUnknown()
    {
        var app = App("fixture") with { Description = "Original introduction" };
        using var card = new CatalogCardViewModel(new() { App = app, Curation = Curation("中文说明") }, english: true);
        Assert.NotEqual("Original introduction", card.Description);
        Assert.Equal("Original introduction", card.FeaturesText);
        Assert.Equal("No published release", card.LatestVersion);
        Assert.Equal("Unknown", card.UpdatedText);
        Assert.Equal("Install", card.InstallLabel);
        Assert.Equal("More", card.MoreLabel);
    }

    [Fact]
    public void Card_UsesExistingFeatureFactsAndRejectsUnsafeLinks()
    {
        var app = App("fixture") with { Owner = "owner/../../", Topics = ["topic"] };
        using var card = new CatalogCardViewModel(new()
        {
            App = app,
            Summary = new()
            {
                Key = "summary", AppId = app.Id, ReleaseTag = "v1", ReadmeSha = "sha",
                PromptVer = "v1", Model = "fixture", OneLiner = "Intro", KeyFeatures = ["Batch export", "  ", "Batch export"],
            },
            Screenshots = ["file:///C:/private/image.png", "http://example.org/image.png", "https://user:secret@example.org/image.png"],
        });
        Assert.Equal("• Batch export", card.FeaturesText);
        Assert.Null(card.RepositoryUri);
        Assert.False(card.HasScreenshot);
        Assert.True(card.HasNoScreenshot);
    }

    [Fact]
    public async Task Screenshot_IsLazyAndCoalescesRepeatedLoadedEvents()
    {
        var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var card = new CatalogCardViewModel(new() { App = App("fixture") }, screenshotLoader: _ =>
        {
            calls++;
            return completion.Task;
        });
        Assert.Equal(0, calls);
        var first = card.LoadScreenshotAsync();
        var second = card.LoadScreenshotAsync();
        Assert.Equal(1, calls);
        Assert.Same(first, second);
        Assert.True(card.IsScreenshotLoading);
        completion.SetResult(new Uri("https://example.org/screenshot.png"));
        await Task.WhenAll(first, second);
        Assert.True(card.HasScreenshot);
        Assert.False(card.HasNoScreenshot);
        Assert.False(card.IsScreenshotLoading);
        await card.LoadScreenshotAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExistingScreenshot_SkipsReadmeAndImageFailureShowsPlaceholder()
    {
        var calls = 0;
        using var card = new CatalogCardViewModel(new()
        {
            App = App("fixture"), Screenshots = ["https://example.org/real.png"],
        }, screenshotLoader: _ => { calls++; return Task.FromResult<Uri?>(null); });
        await card.LoadScreenshotAsync();
        Assert.Equal(0, calls);
        card.ClearScreenshot();
        Assert.Null(card.ScreenshotUri);
        Assert.True(card.HasNoScreenshot);
        Assert.Contains("暂无可用截图", card.ScreenshotFallback);
    }

    [Fact]
    public async Task DisposedCard_DoesNotPublishLateScreenshot()
    {
        var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        using var card = new CatalogCardViewModel(new() { App = App("fixture") }, screenshotLoader: ct =>
        {
            observed = ct;
            return completion.Task;
        });
        var loading = card.LoadScreenshotAsync();
        card.Dispose();
        completion.SetResult(new Uri("https://example.org/late.png"));
        await loading;
        Assert.True(observed.IsCancellationRequested);
        Assert.False(card.HasScreenshot);
        Assert.False(card.IsScreenshotLoading);
    }

    [Fact]
    public async Task Projection_EnrichesLocalDetailsWithoutReadmeOrModelCalls()
    {
        var app = App("fixture");
        var detail = new AppDetail { App = app, Curation = Curation("本地介绍"), Releases = [new() { AppId = app.Id, Key = "r", TagName = "v3" }] };
        var catalog = new FakeCatalog([detail]);
        var readme = new FakeReadme();
        using var vm = new CatalogViewModel(catalog, catalog, new LocalizationService("zh-CN"), readmeSummary: readme);
        vm.Apps.Add(app);
        await vm.RefreshCardsAsync();
        var card = Assert.Single(vm.Cards);
        Assert.Same(app, card.App);
        Assert.Equal("本地介绍", card.FeaturesText);
        Assert.Equal("v3", card.LatestVersion);
        Assert.Equal(0, readme.LoadCalls);
        Assert.Equal(0, readme.GenerateCalls);
        await card.LoadScreenshotAsync();
        Assert.Equal(1, readme.LoadCalls);
        Assert.Equal(0, readme.GenerateCalls);
        Assert.NotNull(card.ScreenshotUri);
    }

    [Fact]
    public async Task Projection_RejectsOldCategoryDetailsAfterReplacement()
    {
        var alpha = App("alpha");
        var beta = App("beta");
        var pending = new TaskCompletionSource<AppDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog([])
        {
            DetailHandler = (id, _) => id == alpha.Id ? pending.Task : Task.FromResult(new AppDetail { App = beta }),
        };
        using var vm = new CatalogViewModel(catalog, catalog);
        vm.Apps.Add(alpha);
        var first = vm.RefreshCardsAsync();
        vm.Apps.Clear();
        vm.Apps.Add(beta);
        await vm.RefreshCardsAsync();
        pending.SetResult(new() { App = alpha });
        await first;
        Assert.Equal(beta.Id, Assert.Single(vm.Cards).App.Id);
    }

    [Fact]
    public async Task VisibleCards_LimitConcurrentReadmeDiscoveryToThree()
    {
        var details = Enumerable.Range(0, 8).Select(index => new AppDetail { App = App("fixture" + index) }).ToArray();
        var catalog = new FakeCatalog(details);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readme = new FakeReadme { BeforeLoad = ct => release.Task.WaitAsync(ct) };
        using var vm = new CatalogViewModel(catalog, catalog, readmeSummary: readme);
        foreach (var detail in details) vm.Apps.Add(detail.App);
        await vm.RefreshCardsAsync();
        var requests = vm.Cards.Select(card => card.LoadScreenshotAsync()).ToArray();
        Assert.Equal(3, readme.LoadCalls);
        release.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(8, readme.LoadCalls);
        Assert.InRange(readme.MaxConcurrent, 1, 3);
        Assert.Equal(0, readme.GenerateCalls);
    }

    private static CatalogApp App(string repo) => new()
    {
        Id = new(new(Guid.NewGuid()), new("owner/" + repo)), SourceRevision = 1,
        DisplayName = repo, Owner = "owner", Repo = repo,
    };
    private static AppCuration Curation(string description) => new()
    {
        DescriptionZh = description,
        Verification = new() { RepositoryUrl = new Uri("https://github.com/owner/fixture"), Scope = "repository-license-windows-metadata" },
    };

    private sealed class FakeReadme : IReadmeSummaryService
    {
        private int _loadCalls;
        private int _concurrent;
        private int _maxConcurrent;
        public bool IsConfigured => true;
        public int LoadCalls => _loadCalls;
        public int MaxConcurrent => _maxConcurrent;
        public int GenerateCalls { get; private set; }
        public Func<CancellationToken, Task>? BeforeLoad { get; init; }
        public async Task<ReadmeDocument> LoadReadmeAsync(CatalogApp app, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loadCalls);
            var active = Interlocked.Increment(ref _concurrent);
            int current;
            do { current = _maxConcurrent; if (active <= current) break; }
            while (Interlocked.CompareExchange(ref _maxConcurrent, active, current) != current);
            try
            {
                if (BeforeLoad is not null) await BeforeLoad(ct);
                return new("![Application screenshot](https://example.org/screenshot.png)", "sha",
                    new Uri($"https://github.com/{app.Owner}/{app.Repo}/blob/main/README.md"), DateTimeOffset.UtcNow);
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public Task<AiReadmeSummary?> GetCachedAsync(CatalogApp app, string language, CancellationToken ct = default) => Task.FromResult<AiReadmeSummary?>(null);
        public Task<AiReadmeSummary> GenerateAsync(CatalogApp app, ReadmeDocument readme, string language, bool force = false, CancellationToken ct = default)
        {
            GenerateCalls++;
            throw new InvalidOperationException("Card discovery must never call a model.");
        }
    }

    private sealed class FakeCatalog(IReadOnlyList<AppDetail> details) : ICatalogService, ISearchService
    {
        public Func<AppId, CancellationToken, Task<AppDetail>>? DetailHandler { get; init; }
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) =>
            DetailHandler?.Invoke(id, ct) ?? Task.FromResult(details.Single(detail => detail.App.Id == id));
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>([]);
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) => Task.FromResult(new CatalogPage { Items = details.Select(detail => detail.App).ToArray(), Total = details.Count });
        public Task<IReadOnlyList<CatalogApp>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogApp>>(details.Select(detail => detail.App).Take(limit).ToArray());
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default) => Task.FromResult(new SearchResult { Items = details.Select(detail => detail.App).ToArray(), Total = details.Count });
    }
}
