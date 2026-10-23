using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class CatalogViewModelTests
{
    [Fact]
    public async Task InitializeAndLoadMore_LoadCategoriesSelectFirstAndAppendTheNextPage()
    {
        var first = App("first");
        var second = App("second");
        var third = App("third");
        var fake = new FakeCatalog
        {
            Browse = (query, _) => Task.FromResult(query.Offset == 0
                ? Page([first, second], 3, true) : Page([third], 3, false)),
        };
        using var vm = new CatalogViewModel(fake, fake);
        await vm.InitializeAsync();
        Assert.Equal(fake.CategoryList, vm.Categories);
        Assert.Equal(new[] { first, second }, vm.Apps);
        Assert.Equal(first.Id, vm.SelectedDetail?.App.Id);
        Assert.True(vm.HasMore);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.ErrorMessage);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(new[] { first, second, third }, vm.Apps);
        Assert.False(vm.HasMore);
        Assert.Equal(new[] { 0, 2 }, fake.BrowseCalls.Select(q => q.Offset));
        Assert.All(fake.BrowseCalls, q => Assert.Equal(12, q.Limit));
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 0, 2 }, fake.BrowseCalls.Select(q => q.Offset));
    }

    [Fact]
    public async Task Search_ForwardsTrimmedQueryAndCategoryAndDisablesPagination()
    {
        var found = App("found");
        var fake = new FakeCatalog
        {
            Search = (_, _, _) => Task.FromResult(new SearchResult { Items = [found], Total = 100 }),
        };
        using var vm = new CatalogViewModel(fake, fake)
        {
            SelectedCategory = fake.CategoryList[0],
            SearchText = "  notes  ",
        };
        await vm.SearchCommand.ExecuteAsync(null);
        var call = Assert.Single(fake.SearchCalls);
        Assert.Equal("notes", call.Query);
        Assert.Equal(fake.CategoryList[0].Id, call.Options.Category);
        Assert.Equal(500, call.Options.Limit);
        Assert.False(call.Options.IncludeRemote);
        Assert.Equal(found, Assert.Single(vm.Apps));
        Assert.False(vm.HasMore);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Empty(fake.BrowseCalls);
        Assert.Single(fake.SearchCalls);
    }

    [Fact]
    public async Task CategoryAndSort_AreForwardedToBrowseAndEmptyResultsClearDetail()
    {
        var fake = new FakeCatalog();
        using var vm = new CatalogViewModel(fake, fake);
        await vm.SelectAsync(App("previous"));
        vm.SelectedCategory = fake.CategoryList[0];
        vm.Sort = SortKey.Updated;
        await vm.LoadAsync();
        var call = Assert.Single(fake.BrowseCalls);
        Assert.Equal(fake.CategoryList[0].Id, call.Category);
        Assert.Equal(SortKey.Updated, call.Sort);
        Assert.Empty(vm.Apps);
        Assert.Null(vm.SelectedDetail);
        Assert.False(vm.HasMore);
        Assert.Contains("没有匹配", vm.StatusMessage);
    }

    [Fact]
    public async Task FailedLoad_PreservesPreviousItemsAndAllowsSuccessfulRetry()
    {
        var app = App("cached");
        var fake = new FakeCatalog { Browse = (_, _) => Task.FromResult(Page([app], 1)) };
        using var vm = new CatalogViewModel(fake, fake);
        await vm.LoadAsync();
        fake.Browse = (_, _) => Task.FromException<CatalogPage>(new InvalidOperationException("service failed"));
        await vm.LoadAsync();
        Assert.Equal(app, Assert.Single(vm.Apps));
        Assert.Contains("service failed", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasMore);
        fake.Browse = (_, _) => Task.FromResult(Page([], 0));
        await vm.LoadAsync();
        Assert.Empty(vm.ErrorMessage);
        Assert.Empty(vm.Apps);
    }

    [Fact]
    public async Task CancelledLoad_IsNotDisplayedAsAnErrorAndRestoresBusyState()
    {
        var pending = Pending<CatalogPage>();
        var fake = new FakeCatalog { Browse = (_, ct) => pending.Task.WaitAsync(ct) };
        using var vm = new CatalogViewModel(fake, fake);
        using var cancellation = new CancellationTokenSource();
        var load = vm.LoadAsync(cancellation.Token);
        Assert.True(vm.IsBusy);
        cancellation.Cancel();
        await load;
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.ErrorMessage);
        Assert.Empty(vm.Apps);
    }

    [Fact]
    public async Task NewerLoad_WinsEvenWhenCancelledOlderServiceIgnoresCancellation()
    {
        var oldResponse = Pending<CatalogPage>();
        var newResponse = Pending<CatalogPage>();
        var fake = new FakeCatalog();
        CancellationToken oldToken = default;
        fake.Browse = (_, ct) =>
        {
            if (fake.BrowseCalls.Count == 1)
            {
                oldToken = ct;
                return oldResponse.Task;
            }
            return newResponse.Task;
        };
        using var vm = new CatalogViewModel(fake, fake);
        var oldLoad = vm.LoadAsync();
        var newLoad = vm.LoadAsync();
        Assert.True(oldToken.IsCancellationRequested);
        oldResponse.SetResult(Page([App("stale")], 1));
        await oldLoad;
        Assert.True(vm.IsBusy);
        Assert.Empty(vm.Apps);
        var current = App("current");
        newResponse.SetResult(Page([current], 1));
        await newLoad;
        Assert.Equal(current, Assert.Single(vm.Apps));
        Assert.Equal(current.Id, vm.SelectedDetail?.App.Id);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailSelection_IgnoresStaleSuccessAndFailure(bool staleFails)
    {
        var oldResponse = Pending<AppDetail>();
        var old = App("old");
        var current = App("current");
        var fake = new FakeCatalog
        {
            Detail = (id, _) => id == old.Id ? oldResponse.Task : Task.FromResult(new AppDetail { App = current }),
        };
        using var vm = new CatalogViewModel(fake, fake);
        var oldSelection = vm.SelectAsync(old);
        await vm.SelectAsync(current);
        if (staleFails) oldResponse.SetException(new InvalidOperationException("stale failure"));
        else oldResponse.SetResult(new AppDetail { App = old });
        await oldSelection;
        Assert.Equal(current.Id, vm.SelectedDetail?.App.Id);
        Assert.Empty(vm.ErrorMessage);
        await vm.SelectAsync(null);
        Assert.Null(vm.SelectedDetail);
    }

    [Fact]
    public async Task DetailAndRefreshFailures_ArePresentedWithoutThrowing()
    {
        var fake = new FakeCatalog
        {
            Detail = (_, _) => Task.FromException<AppDetail>(new KeyNotFoundException("detail missing")),
            Refresh = _ => Task.FromException(new InvalidOperationException("refresh failed")),
        };
        using var vm = new CatalogViewModel(fake, fake);
        await vm.SelectAsync(App("missing"));
        Assert.Null(vm.SelectedDetail);
        Assert.Contains("detail missing", vm.ErrorMessage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("refresh failed", vm.ErrorMessage);
        Assert.Empty(fake.BrowseCalls);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightLoadAndRejectsFurtherLoads()
    {
        var pending = Pending<CatalogPage>();
        var fake = new FakeCatalog { Browse = (_, ct) => pending.Task.WaitAsync(ct) };
        var vm = new CatalogViewModel(fake, fake);
        var load = vm.LoadAsync();
        vm.Dispose();
        vm.Dispose();
        await load;
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.ErrorMessage);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.LoadAsync());
    }

    [Fact]
    public async Task ManualDetailWithoutCuration_HidesMetadataAndPreservesDescriptionAndMarkdown()
    {
        var app = App("manual") with { Description = "Original GitHub description" };
        const string markdown = "## Release\n\n- Original **Markdown**";
        var detail = new AppDetail
        {
            App = app,
            Releases = [new Release { Key = "v1", AppId = app.Id, TagName = "v1", Body = markdown }]
        };
        var fake = new FakeCatalog { Detail = (_, _) => Task.FromResult(detail) };
        using var vm = new CatalogViewModel(fake, fake);
        Assert.False(vm.HasCurationDescription);
        Assert.False(vm.HasRecommendationSources);
        Assert.False(vm.HasVerification);
        await vm.SelectAsync(app);
        Assert.True(vm.HasDetail);
        Assert.Equal(app.Description, vm.DetailDescription);
        Assert.Equal(markdown, vm.ReleaseNotes);
        Assert.Equal("https://github.com/fixture/manual", vm.RepositoryUri?.AbsoluteUri);
        Assert.False(vm.HasCurationDescription);
        Assert.Empty(vm.CurationDescription);
        Assert.False(vm.HasRecommendationSources);
        Assert.Empty(vm.RecommendationSources);
        Assert.False(vm.HasVerification);
        Assert.Empty(vm.VerificationDateText);
        Assert.Empty(vm.VerificationScopeText);
        Assert.Empty(vm.VerificationNotes);
    }

    [Theory]
    [InlineData("http://example.com/post")]
    [InlineData("https://fixture:ignored@example.com/post")]
    [InlineData("https://user@example.com/post")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/post.html")]
    [InlineData("/relative/post")]
    [InlineData(null)]
    public void Recommendations_FilterUnsafeCacheLinks(string? unsafeUrl)
    {
        var fake = new FakeCatalog();
        using var vm = new CatalogViewModel(fake, fake);
        var valid = Recommendation("https://example.com/post", "search-snippet");
        var invalid = Recommendation("https://example.com/placeholder", "full-text") with
        {
            Url = unsafeUrl is null ? null! : new Uri(unsafeUrl, UriKind.RelativeOrAbsolute)
        };
        vm.SelectedDetail = new AppDetail { App = App("curated"), Curation = Curation([invalid, valid]) };
        var source = Assert.Single(vm.RecommendationSources);
        Assert.True(vm.HasRecommendationSources);
        Assert.Equal(valid.Url, source.Url);
        Assert.Equal("知乎 · A useful application", source.LinkText);
        vm.SelectedDetail = vm.SelectedDetail! with { Curation = Curation([invalid]) };
        Assert.False(vm.HasRecommendationSources);
        Assert.Empty(vm.RecommendationSources);
    }

    [Theory]
    [InlineData("full-text", "证据：全文正文", "Evidence: full post text")]
    [InlineData("search-snippet", "证据：搜索摘要/标题（非全文）", "Evidence: search snippet/title (not full text)")]
    [InlineData("title-only", "证据：仅标题（非全文）", "Evidence: title only (not full text)")]
    [InlineData("future-evidence-level", "证据级别未识别（不视为全文）", "Evidence level unrecognized (not treated as full text)")]
    public void Recommendations_EvidenceLabelsDistinguishFullTextFromIndexedEvidence(
        string evidence, string chinese, string english)
    {
        var text = new LocalizationService("zh-CN");
        var fake = new FakeCatalog();
        using var vm = new CatalogViewModel(fake, fake, text);
        vm.SelectedDetail = new AppDetail
        {
            App = App("curated"), Curation = Curation([Recommendation("https://example.com/post", evidence)])
        };
        Assert.Equal(chinese, Assert.Single(vm.RecommendationSources).EvidenceText);
        text.ApplyLanguage("en-US");
        Assert.Equal(english, Assert.Single(vm.RecommendationSources).EvidenceText);
    }

    [Fact]
    public async Task Curation_LanguageAndSelectionChangesUpdateStateAndNotifyBindings()
    {
        var app = App("curated") with { Description = "Original description" };
        var other = App("other");
        var curated = new AppDetail
        {
            App = app, Curation = Curation([Recommendation("https://example.com/post", "search-snippet")])
        };
        var fake = new FakeCatalog
        {
            Detail = (id, _) => Task.FromResult(id == app.Id ? curated : new AppDetail { App = other })
        };
        var text = new LocalizationService("zh-CN");
        using var vm = new CatalogViewModel(fake, fake, text);
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await vm.SelectAsync(app);
        Assert.True(vm.HasCurationDescription);
        Assert.Equal("帮助整理本地文件。", vm.CurationDescription);
        Assert.Equal("Original description", vm.DetailDescription);
        Assert.False(vm.HasSummary);
        Assert.Empty(vm.SummaryOneLiner);
        Assert.True(vm.HasVerification);
        changed.Clear();
        text.ApplyLanguage("en-US");
        Assert.False(vm.HasCurationDescription);
        Assert.Empty(vm.CurationDescription);
        Assert.Equal("Original description", vm.DetailDescription);
        Assert.StartsWith("Verified on:", vm.VerificationDateText);
        Assert.Contains("search snippet/title", Assert.Single(vm.RecommendationSources).EvidenceText);
        Assert.Contains(nameof(vm.CurationDescription), changed);
        Assert.Contains(nameof(vm.HasCurationDescription), changed);
        Assert.Contains(nameof(vm.HasRecommendationSources), changed);
        Assert.Contains(nameof(vm.VerificationDateText), changed);
        Assert.Contains(nameof(vm.VerificationScopeText), changed);
        text.ApplyLanguage("zh-CN");
        Assert.True(vm.HasCurationDescription);
        Assert.Equal("帮助整理本地文件。", vm.CurationDescription);
        await vm.SelectAsync(other);
        Assert.Equal(other.DisplayName, vm.DetailTitle);
        Assert.False(vm.HasCurationDescription);
        Assert.False(vm.HasRecommendationSources);
        Assert.False(vm.HasVerification);
        Assert.Empty(vm.RecommendationSources);
        Assert.Empty(vm.VerificationScopeText);
        await vm.SelectAsync(app);
        Assert.True(vm.HasRecommendationSources);
        changed.Clear();
        await vm.SelectAsync(null);
        Assert.False(vm.HasDetail);
        Assert.False(vm.HasCurationDescription);
        Assert.False(vm.HasRecommendationSources);
        Assert.False(vm.HasVerification);
        Assert.Empty(vm.RecommendationSources);
        Assert.Empty(vm.VerificationDateText);
        Assert.Empty(vm.VerificationNotes);
        Assert.Contains(nameof(vm.HasRecommendationSources), changed);
        Assert.Contains(nameof(vm.HasVerification), changed);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void Verification_StatesMetadataScopeWithoutInstallationOrSecurityAssurance(string language)
    {
        var fake = new FakeCatalog();
        using var vm = new CatalogViewModel(fake, fake, new LocalizationService(language));
        var curation = Curation([]);
        vm.SelectedDetail = new AppDetail
        {
            App = App("curated"),
            Curation = curation with
            {
                Verification = curation.Verification with
                {
                    Notes = ["", "Official repository checked.", new string('x', 300), "Windows metadata checked.", "Omitted fourth note"]
                }
            }
        };
        Assert.True(vm.HasVerification);
        Assert.Contains("2026-10-20", vm.VerificationDateText);
        var scope = vm.VerificationScopeText;
        if (language == "zh-CN")
        {
            Assert.Contains("官方仓库/许可证/Windows 支持元数据", scope);
            Assert.Contains("未执行安装或安全审计", scope);
            Assert.DoesNotContain("安全保证", scope);
            Assert.DoesNotContain("安全认证", scope);
        }
        else
        {
            Assert.Contains("Official repository, license and Windows support metadata", scope);
            Assert.Contains("no installation or security audit was performed", scope);
            Assert.DoesNotContain("safe to install", scope);
            Assert.DoesNotContain("security verified", scope);
        }
        Assert.Equal(3, vm.VerificationNotes.Count);
        Assert.Equal("Official repository checked.", vm.VerificationNotes[0]);
        Assert.EndsWith("…", vm.VerificationNotes[1]);
        Assert.True(vm.VerificationNotes[1].Length <= 241);
        Assert.Equal("Windows metadata checked.", vm.VerificationNotes[2]);
        vm.SelectedDetail = vm.SelectedDetail! with
        {
            Curation = curation with { Verification = curation.Verification with { Scope = "unrecognized-scope" } }
        };
        Assert.Equal(language == "zh-CN"
            ? "核验范围未标明；未执行安装或安全审计。"
            : "Verification scope unspecified; no installation or security audit was performed.", vm.VerificationScopeText);
    }

    [Fact]
    public void AssetPolicy_SelectsPortableByDefaultButPreservesManualChoiceAcrossLanguageChanges()
    {
        var fake = new FakeCatalog();
        var text = new LocalizationService();
        text.ApplyLanguage("zh-CN");
        using var vm = new CatalogViewModel(fake, fake, text);
        var app = App("packages");
        Asset Package(string name, AssetKind kind) => new()
        {
            InstallerId = name, ReleaseKey = "v1", Name = name, Kind = kind,
            Arch = vm.SystemArchitecture, DownloadUrl = "https://example.com/" + name,
        };
        var portable = Package("app-windows-portable.zip", AssetKind.ArchiveZip);
        var exe = Package("app-setup.exe", AssetKind.InstallerExe);
        var msi = Package("app.msi", AssetKind.InstallerMsi);
        vm.SelectedDetail = new AppDetail { App = app,
            Releases = [new Release { AppId = app.Id, Key = "v1", TagName = "v1", Assets = [msi, exe, portable] }] };
        Assert.Equal(3, vm.WindowsAssets.Count);
        Assert.Equal(portable.Name, vm.SelectedAsset?.Name);
        Assert.True(vm.SelectedAsset?.IsRecommended);
        Assert.Equal("ZIP", vm.SelectedAsset?.Kind);
        vm.SelectedAsset = vm.WindowsAssets.Single(a => a.Name == msi.Name);
        text.ApplyLanguage("en-US");
        Assert.Equal(msi.Name, vm.SelectedAsset?.Name);
        Assert.False(vm.SelectedAsset?.IsRecommended);
        Assert.Contains(portable.Name, vm.AssetRecommendation);
        vm.SelectedDetail = new AppDetail { App = App("empty") };
        Assert.Empty(vm.WindowsAssets);
        Assert.Null(vm.SelectedAsset);
        Assert.False(vm.HasSelectedAsset);
    }

    [Fact]
    public void GenericArchivesRemainManuallyAvailableWithoutAutomaticRecommendation()
    {
        var fake = new FakeCatalog();
        using var vm = new CatalogViewModel(fake, fake);
        var app = App("unclassified");
        var archive = new Asset { InstallerId = "zip", ReleaseKey = "v1", Name = "app-windows.zip",
            Kind = AssetKind.ArchiveZip, DownloadUrl = "https://example.com/app.zip" };
        vm.SelectedDetail = new AppDetail { App = app,
            Releases = [new Release { AppId = app.Id, Key = "v1", TagName = "v1", Assets = [archive] }] };
        Assert.Single(vm.WindowsAssets);
        Assert.Null(vm.SelectedAsset);
        Assert.Contains("未找到", vm.AssetRecommendation);
    }

    private static RecommendationSource Recommendation(string url, string evidence) => new()
    {
        Platform = "知乎", Title = "A useful application", Url = new Uri(url),
        DiscoveredAt = DateTimeOffset.Parse("2026-10-20T08:00:00Z"), EvidenceLevel = evidence
    };

    private static AppCuration Curation(IReadOnlyList<RecommendationSource> sources) => new()
    {
        DescriptionZh = "帮助整理本地文件。", RecommendationSources = sources,
        Verification = new RepositoryVerification
        {
            VerifiedAt = new DateTimeOffset(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Local)),
            RepositoryUrl = new Uri("https://github.com/fixture/curated"),
            Scope = "repository-license-windows-metadata"
        }
    };

    private static CatalogApp App(string name) => new()
    {
        Id = new AppId(new SourceInstanceId(Guid.Parse("11111111-1111-4111-8111-111111111111")), new PackageId(name)),
        SourceRevision = 1,
        DisplayName = name,
        Owner = "fixture",
        Repo = name,
    };

    private static CatalogPage Page(IReadOnlyList<CatalogApp> apps, int total, bool hasMore = false)
        => new() { Items = apps, Total = total, HasMore = hasMore };

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeCatalog : ICatalogService, ISearchService
    {
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public IReadOnlyList<Category> CategoryList { get; } =
            [new Category { Id = new CategoryId("office"), Name = "办公效率", NameEn = "Office" }];
        public List<CatalogQuery> BrowseCalls { get; } = [];
        public List<(string Query, SearchOptions Options)> SearchCalls { get; } = [];
        public Func<CatalogQuery, CancellationToken, Task<CatalogPage>> Browse { get; set; }
            = (_, _) => Task.FromResult(Page([], 0));
        public Func<string, SearchOptions, CancellationToken, Task<SearchResult>> Search { get; set; }
            = (_, _, _) => Task.FromResult(new SearchResult());
        public Func<AppId, CancellationToken, Task<AppDetail>> Detail { get; set; }
            = (id, _) => Task.FromResult(new AppDetail { App = App(id.PackageId.Value) });
        public Func<CancellationToken, Task> Refresh { get; set; } = _ => Task.CompletedTask;

        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult(CategoryList);
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default)
        {
            BrowseCalls.Add(query);
            return Browse(query, ct);
        }
        public Task<IReadOnlyList<CatalogApp>> RankAsync(BoardKind board, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CatalogApp>>([]);
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) => Detail(id, ct);
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default)
            => Refresh(ct);
        public Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default)
        {
            SearchCalls.Add((query, options));
            return Search(query, options, ct);
        }
    }
}
