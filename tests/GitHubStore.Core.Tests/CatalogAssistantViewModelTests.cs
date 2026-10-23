using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class CatalogAssistantViewModelTests
{
    [Fact]
    public async Task ExplicitAiSearchIsGlobalAndReturnsActionableCatalogItemsWithExistingSelection()
    {
        var catalog = new CatalogStub();
        var assistant = new AssistantStub { Search = (_, _, _) => Task.FromResult<IReadOnlyList<CatalogApp>>([catalog.First, catalog.Second]) };
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant);
        await vm.SelectAsync(catalog.Second);
        vm.SelectedCategory = new Category { Id = new CategoryId("office"), Name = "办公", NameEn = "Office" };
        vm.SearchText = "  help me edit photos  ";
        Assert.Empty(assistant.Queries);
        await vm.SearchWithAiCommand.ExecuteAsync(null);
        Assert.Equal("help me edit photos", Assert.Single(assistant.Queries));
        Assert.Null(vm.SelectedCategory);
        Assert.Equal(new[] { catalog.First, catalog.Second }, vm.Apps);
        Assert.Equal(catalog.Second.Id, vm.SelectedDetail?.App.Id);
        Assert.False(vm.HasMore);
        Assert.False(vm.IsAssistantSearchBusy);
        Assert.Empty(vm.AssistantSearchError);
        Assert.Contains("2", vm.AssistantSearchStatus);
        await vm.SelectAsync(vm.Apps[0]);
        Assert.Equal(catalog.First.Id, vm.SelectedDetail?.App.Id);
        Assert.NotNull(vm.CreateInstallRequest());
    }

    [Fact]
    public async Task TypingCancelsOldSearchWithoutCallingModelAndNewSearchWinsLateOldResponse()
    {
        var catalog = new CatalogStub();
        var old = Pending<IReadOnlyList<CatalogApp>>();
        CancellationToken oldToken = default;
        var assistant = new AssistantStub
        {
            Search = (query, _, ct) => query == "first need"
                ? Capture(ct) : Task.FromResult<IReadOnlyList<CatalogApp>>([catalog.Second]),
        };
        Task<IReadOnlyList<CatalogApp>> Capture(CancellationToken ct) { oldToken = ct; return old.Task; }
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant) { SearchText = "first need" };
        var stale = vm.SearchWithAiCommand.ExecuteAsync(null);
        Assert.True(vm.IsAssistantSearchBusy);
        vm.SearchText = "second need";
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Single(assistant.Queries);
        await vm.SearchWithAiCommand.ExecuteAsync(null);
        old.SetResult([catalog.First]);
        await stale;
        Assert.Equal(catalog.Second, Assert.Single(vm.Apps));
        Assert.Equal(catalog.Second.Id, vm.SelectedDetail?.App.Id);
        Assert.Equal(2, assistant.Queries.Count);
        Assert.Empty(vm.AssistantSearchError);
    }

    [Fact]
    public async Task NormalSearchInvalidatesLateAiResultsAndStillUsesNormalSearchService()
    {
        var catalog = new CatalogStub();
        var pending = Pending<IReadOnlyList<CatalogApp>>();
        CancellationToken token = default;
        var assistant = new AssistantStub { Search = (_, _, ct) => { token = ct; return pending.Task; } };
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant) { SearchText = "photo editing" };
        var stale = vm.SearchWithAiCommand.ExecuteAsync(null);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal("photo editing", catalog.LastKeywordQuery);
        pending.SetResult([catalog.First]);
        await stale;
        Assert.Equal(catalog.Second, Assert.Single(vm.Apps));
        Assert.Empty(vm.AssistantSearchStatus);
        Assert.Empty(vm.AssistantSearchError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetOrDetailChangesCancelExplanationAndIgnoreLateResponse(bool changeDetail)
    {
        var catalog = new CatalogStub();
        var pending = Pending<string>();
        CancellationToken token = default;
        var assistant = new AssistantStub { Analyze = (_, _, ct) => { token = ct; return pending.Task; } };
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant);
        await vm.SelectAsync(catalog.First);
        var oldAsset = vm.SelectedAsset;
        var stale = vm.ExplainAssetCommand.ExecuteAsync(null);
        Assert.True(vm.IsAssistantDetailBusy);
        if (changeDetail) await vm.SelectAsync(catalog.Second);
        else vm.SelectedAsset = vm.WindowsAssets.First(asset => !ReferenceEquals(asset, oldAsset));
        Assert.True(token.IsCancellationRequested);
        pending.SetResult("Stale explanation");
        await stale;
        Assert.Empty(vm.AssetExplanationMarkdown);
        Assert.Empty(vm.AssistantDetailError);
        Assert.False(vm.IsAssistantDetailBusy);
        assistant.Analyze = (_, _, _) => Task.FromResult("Current explanation");
        await vm.ExplainAssetCommand.ExecuteAsync(null);
        Assert.Equal("Current explanation", vm.AssetExplanationMarkdown);
        Assert.True(vm.HasAssetExplanation);
        Assert.Equal(vm.SelectedAsset!.Asset, assistant.Requests.Last().Asset);
    }

    [Fact]
    public async Task ReleaseExplanationIncludesActualNotesAndClearlyStatesMissingInstalledBaseline()
    {
        var catalog = new CatalogStub();
        var assistant = new AssistantStub();
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant);
        await vm.SelectAsync(catalog.First);
        await vm.ExplainReleaseCommand.ExecuteAsync(null);
        var request = Assert.Single(assistant.Requests);
        Assert.Equal(AiAssistantTask.ReleaseImpact, request.Task);
        Assert.Equal("## Changes\nAdds export support.", request.Release?.Body);
        Assert.Contains("This view supplies no installed baseline", request.Context);
        Assert.Contains("7z extraction is unsupported", request.Context);
        Assert.Equal("# Explanation", vm.ReleaseImpactMarkdown);
        Assert.True(vm.HasReleaseImpact);
        Assert.Empty(vm.AssistantDetailError);
    }

    [Fact]
    public async Task ExplicitCancellationAndErrorsRestoreStateWithoutLeakingExceptionDetails()
    {
        var catalog = new CatalogStub();
        var pending = Pending<string>();
        var assistant = new AssistantStub { Analyze = (_, _, ct) => pending.Task.WaitAsync(ct) };
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant);
        await vm.SelectAsync(catalog.First);
        var operation = vm.ExplainAssetCommand.ExecuteAsync(null);
        vm.CancelAssistantDetailCommand.Execute(null);
        await operation;
        Assert.False(vm.IsAssistantDetailBusy);
        Assert.Empty(vm.AssistantDetailError);
        Assert.Empty(vm.AssetExplanationMarkdown);
        assistant.Analyze = (_, _, _) => throw new HttpRequestException("private endpoint token should not appear");
        await vm.ExplainReleaseCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.AssistantDetailError);
        Assert.DoesNotContain("private endpoint", vm.AssistantDetailError);
        Assert.Empty(vm.ReleaseImpactMarkdown);
    }

    [Fact]
    public async Task LanguageChangeCancelsExplanationAndUpdatesBilingualLabels()
    {
        var catalog = new CatalogStub();
        var pending = Pending<string>();
        CancellationToken token = default;
        var assistant = new AssistantStub { Analyze = (_, _, ct) => { token = ct; return pending.Task; } };
        var text = new LocalizationService("zh-CN");
        using var vm = new CatalogViewModel(catalog, catalog, text, assistant: assistant);
        await vm.SelectAsync(catalog.First);
        var operation = vm.ExplainAssetCommand.ExecuteAsync(null);
        text.ApplyLanguage("en-US");
        Assert.True(token.IsCancellationRequested);
        pending.SetResult("Old-language response");
        await operation;
        Assert.Equal("AI search", vm.SearchWithAiLabel);
        Assert.Equal("Explain selected package", vm.ExplainAssetLabel);
        Assert.Empty(vm.AssetExplanationMarkdown);
        Assert.Empty(vm.AssistantDetailStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalCancelsAndRejectsLateSearchOrExplanationWrites(bool search)
    {
        var catalog = new CatalogStub();
        var searchResponse = Pending<IReadOnlyList<CatalogApp>>();
        var explanationResponse = Pending<string>();
        CancellationToken token = default;
        var assistant = new AssistantStub
        {
            Search = (_, _, ct) => { token = ct; return searchResponse.Task; },
            Analyze = (_, _, ct) => { token = ct; return explanationResponse.Task; },
        };
        var vm = new CatalogViewModel(catalog, catalog, assistant: assistant) { SearchText = "need editor" };
        await vm.SelectAsync(catalog.First);
        var operation = search ? vm.SearchWithAiCommand.ExecuteAsync(null) : vm.ExplainReleaseCommand.ExecuteAsync(null);
        vm.Dispose();
        Assert.True(token.IsCancellationRequested);
        searchResponse.SetResult([catalog.Second]);
        explanationResponse.SetResult("Late private result");
        await operation;
        Assert.Empty(vm.Apps);
        Assert.Empty(vm.AssetExplanationMarkdown);
        Assert.Empty(vm.ReleaseImpactMarkdown);
        Assert.Empty(vm.AssistantSearchStatus);
        Assert.Empty(vm.AssistantDetailStatus);
        Assert.False(vm.CanSearchWithAi);
        Assert.False(vm.CanExplainAsset);
    }

    [Fact]
    public async Task MissingAiConfigurationReportsActionableErrorWithoutRequests()
    {
        var catalog = new CatalogStub();
        var assistant = new AssistantStub { IsConfigured = false };
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant) { SearchText = "need editor" };
        await vm.SelectAsync(catalog.First);
        await vm.SearchWithAiCommand.ExecuteAsync(null);
        await vm.ExplainAssetCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.AssistantSearchError);
        Assert.NotEmpty(vm.AssistantDetailError);
        Assert.Empty(assistant.Queries);
        Assert.Empty(assistant.Requests);
    }

    [Theory]
    [InlineData("search-text")]
    [InlineData("asset-change")]
    [InlineData("cancel-search")]
    [InlineData("cancel-detail")]
    [InlineData("dispose")]
    public async Task ThrowingCancellationCallbacksCannotEscapeStateTransitionsOrRestoreStaleResults(string transition)
    {
        var catalog = new CatalogStub();
        var searchResponse = Pending<IReadOnlyList<CatalogApp>>();
        var detailResponse = Pending<string>();
        var callbackExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        void RegisterThrowingCallback(CancellationToken ct)
        {
            token = ct;
            ct.Register(() =>
            {
                callbackExecuted.TrySetResult();
                throw new InvalidOperationException("Service cancellation callback failed");
            });
        }
        var assistant = new AssistantStub
        {
            Search = (_, _, ct) => { RegisterThrowingCallback(ct); return searchResponse.Task; },
            Analyze = (_, _, ct) => { RegisterThrowingCallback(ct); return detailResponse.Task; },
        };
        using var vm = new CatalogViewModel(catalog, catalog, assistant: assistant) { SearchText = "find an editor" };
        await vm.SelectAsync(catalog.First);
        var search = transition is "search-text" or "cancel-search";
        var operation = search ? vm.SearchWithAiCommand.ExecuteAsync(null) : vm.ExplainAssetCommand.ExecuteAsync(null);
        var oldAsset = vm.SelectedAsset;
        var exception = Record.Exception(() =>
        {
            switch (transition)
            {
                case "search-text": vm.SearchText = "a different need"; break;
                case "asset-change": vm.SelectedAsset = vm.WindowsAssets.First(asset => !ReferenceEquals(asset, oldAsset)); break;
                case "cancel-search": vm.CancelAssistantSearchCommand.Execute(null); break;
                case "cancel-detail": vm.CancelAssistantDetailCommand.Execute(null); break;
                case "dispose": vm.Dispose(); break;
            }
        });
        Assert.Null(exception);
        Assert.True(token.IsCancellationRequested);
        Assert.False(vm.IsAssistantSearchBusy);
        Assert.False(vm.IsAssistantDetailBusy);
        await callbackExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        searchResponse.SetResult([catalog.Second]);
        detailResponse.SetResult("Stale result after throwing callback");
        await operation;
        Assert.Empty(vm.Apps);
        Assert.Empty(vm.AssetExplanationMarkdown);
        Assert.Empty(vm.ReleaseImpactMarkdown);
        Assert.Empty(vm.AssistantSearchError);
        Assert.Empty(vm.AssistantDetailError);
    }

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class AssistantStub : IAiAssistantService
    {
        public bool IsConfigured { get; set; } = true;
        public List<string> Queries { get; } = [];
        public List<AiAssistantRequest> Requests { get; } = [];
        public Func<string, string, CancellationToken, Task<IReadOnlyList<CatalogApp>>> Search { get; set; }
            = (_, _, _) => Task.FromResult<IReadOnlyList<CatalogApp>>([]);
        public Func<AiAssistantRequest, string, CancellationToken, Task<string>> Analyze { get; set; }
            = (_, _, _) => Task.FromResult("# Explanation");
        public Task<string> AnalyzeAsync(AiAssistantRequest request, string language, CancellationToken ct = default)
        { Requests.Add(request); return Analyze(request, language, ct); }
        public Task<IReadOnlyList<CatalogApp>> SearchAsync(string query, string language, CancellationToken ct = default)
        { Queries.Add(query); return Search(query, language, ct); }
    }

    private sealed class CatalogStub : ICatalogService, ISearchService
    {
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public CatalogApp First { get; } = CreateApp("first", "office");
        public CatalogApp Second { get; } = CreateApp("second", "developer");
        public string? LastKeywordQuery { get; private set; }
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Category>>([]);
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default)
            => Task.FromResult(new CatalogPage { Items = [First, Second], Total = 2 });
        public Task<IReadOnlyList<CatalogApp>> RankAsync(BoardKind board, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CatalogApp>>([First, Second]);
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var app = id == First.Id ? First : Second;
            var assets = new[]
            {
                new Asset { InstallerId = "setup", ReleaseKey = "v2", Name = "app-setup.exe", DownloadUrl = "https://example.com/setup.exe", Kind = AssetKind.InstallerExe },
                new Asset { InstallerId = "portable", ReleaseKey = "v2", Name = "app-portable.exe", DownloadUrl = "https://example.com/portable.exe", Kind = AssetKind.PortableExe },
            };
            return Task.FromResult(new AppDetail { App = app, Releases = [new Release
                { Key = "v2", AppId = app.Id, TagName = "v2", Body = "## Changes\nAdds export support.", Assets = assets }] });
        }
        public Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default)
        { LastKeywordQuery = query; return Task.FromResult(new SearchResult { Items = [Second], Total = 1 }); }
        private static CatalogApp CreateApp(string name, string category) => new()
        {
            Id = new AppId(new SourceInstanceId(Guid.Parse("11111111-1111-4111-8111-111111111111")), new PackageId(name)),
            SourceRevision = 1, DisplayName = name, Owner = "fixture", Repo = name, PrimaryCategory = new CategoryId(category),
        };
    }
}
