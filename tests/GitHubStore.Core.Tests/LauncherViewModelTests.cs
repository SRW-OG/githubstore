using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LauncherViewModelTests
{
    [Fact]
    public async Task EmptyRegistryHasHonestEmptyStateWithoutScanningExternalApps()
    {
        var service = new StubLauncher();
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"));
        await vm.RefreshEntriesAsync();
        Assert.Empty(vm.Apps);
        Assert.True(vm.IsEmpty);
        Assert.Contains("还没有", vm.EmptyMessage);
        Assert.Equal(1, service.Reads);
        Assert.Empty(service.Launched);
    }

    [Fact]
    public async Task RegisteredItemsCanBeSearchedByNameOrRepositoryAndLanguageUpdates()
    {
        var service = new StubLauncher { Entries = new[] { Entry("alpha", "Alpha Editor"), Entry("beta", "Beta Viewer") } };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(service, text);
        await vm.RefreshEntriesAsync();
        Assert.Equal(2, vm.Apps.Count);
        vm.SearchText = "editor";
        Assert.Equal("Alpha Editor", Assert.Single(vm.Apps).DisplayName);
        vm.SearchText = "fixture/beta";
        Assert.Equal("Beta Viewer", Assert.Single(vm.Apps).DisplayName);
        vm.SearchText = "not-present";
        Assert.True(vm.IsEmpty);
        Assert.Contains("匹配", vm.EmptyMessage);
        text.ApplyLanguage("en-US");
        Assert.Contains("matching", vm.EmptyMessage);
        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.Apps.Count);
        Assert.Contains("Showing", vm.CountText);
    }

    [Fact]
    public async Task LaunchUsesRegisteredIdAndMissingEntryReportsUnavailable()
    {
        var service = new StubLauncher { Entries = new[] { Entry("alpha", "Alpha"), Entry("missing", "Missing", false) } };
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"));
        await vm.RefreshEntriesAsync();
        await vm.LaunchEntryAsync(vm.Apps.Single(item => item.DisplayName == "Alpha"));
        Assert.Equal(new[] { "install-alpha" }, service.Launched);
        Assert.Contains("已启动", vm.StatusMessage);
        await vm.LaunchEntryAsync(vm.Apps.Single(item => item.DisplayName == "Missing"));
        Assert.Single(service.Launched);
        Assert.Contains("不存在", vm.ErrorMessage);
        Assert.False(vm.IsLaunching);
    }

    [Fact]
    public async Task FailedRefreshPreservesVisibleItemsAndNeverLeaksExceptionDetails()
    {
        var service = new StubLauncher { Entries = new[] { Entry("alpha", "Alpha") } };
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"));
        await vm.RefreshEntriesAsync();
        service.Read = _ => throw new InvalidOperationException("private registry details");
        await vm.RefreshEntriesAsync();
        Assert.Single(vm.Apps);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.DoesNotContain("private registry details", vm.ErrorMessage);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task OldRefreshCannotReplaceNewerResultsEvenIfServiceIgnoresCancellation()
    {
        var delayed = new TaskCompletionSource<IReadOnlyList<LauncherApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubLauncher { Read = _ => delayed.Task };
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"));
        var first = vm.RefreshEntriesAsync();
        service.Read = _ => Task.FromResult<IReadOnlyList<LauncherApp>>(new[] { Entry("current", "Current") });
        await vm.RefreshEntriesAsync();
        delayed.SetResult(new[] { Entry("old", "Old") });
        await first;
        Assert.Equal("Current", Assert.Single(vm.Apps).DisplayName);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task LaunchFailureReportsStableCodeAndDoesNotClaimSuccess()
    {
        var service = new StubLauncher
        {
            Entries = new[] { Entry("alpha", "Alpha") },
            LaunchResult = new LauncherLaunchResult(false, "launcher_start_failed"),
        };
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"));
        await vm.RefreshEntriesAsync();
        await vm.LaunchEntryAsync(vm.Apps[0]);
        Assert.Empty(vm.StatusMessage);
        Assert.Contains("未能启动", vm.ErrorMessage);
    }

    private static LauncherApp Entry(string repo, string name, bool available = true)
    {
        var app = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", repo), null, SeedFixture.FetchedAt, new CategoryId("system")).App;
        return new LauncherApp
        {
            InstallId = "install-" + repo, AppId = app.Id, DisplayName = name,
            InstallDirectory = @"C:\githubapps\" + repo, ExecutablePath = @"C:\githubapps\" + repo + @"\app.exe",
            IsAvailable = available,
        };
    }

    [Fact]
    public async Task InstalledAppsAreGroupedByCatalogCategoryWithUnknownAppsLast()
    {
        var service = new StubLauncher { Entries = new[]
        {
            Entry("PowerToys", "PowerToys"), Entry("notepad--", "Notepad--"), Entry("localsend", "LocalSend"),
            Entry("ghost", "Ghost Tool"),
        } };
        var catalog = new StubCatalog();
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"), catalog);
        await vm.RefreshEntriesAsync();
        Assert.Equal(new[] { "开发工具", "系统工具", "网络", "其他" }, vm.Groups.Select(group => group.Name));
        Assert.Equal("开发工具", vm.Groups[0].Name);
        Assert.Single(vm.Groups[0].Apps);
        Assert.Equal("其他", vm.Groups[^1].Name);
        Assert.Equal("Ghost Tool", Assert.Single(vm.Groups[^1].Apps).DisplayName);
        Assert.Equal(4, vm.Apps.Count);
    }

    [Fact]
    public async Task GroupedViewKeepsSearchFilteringAndEmptyState()
    {
        var service = new StubLauncher { Entries = new[] { Entry("PowerToys", "PowerToys"), Entry("localsend", "LocalSend") } };
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"), new StubCatalog());
        await vm.RefreshEntriesAsync();
        Assert.Equal(2, vm.Groups.Count);
        vm.SearchText = "localsend";
        Assert.Equal("网络", Assert.Single(vm.Groups).Name);
        Assert.Equal("LocalSend", Assert.Single(vm.Groups[0].Apps).DisplayName);
        vm.SearchText = "nothing";
        Assert.Empty(vm.Groups);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task CatalogFailureFallsBackToUncategorizedWithoutLosingInstalledApps()
    {
        var service = new StubLauncher { Entries = new[] { Entry("PowerToys", "PowerToys") } };
        using var vm = new LauncherViewModel(service, new LocalizationService("zh-CN"), new FailingCatalog());
        await vm.RefreshEntriesAsync();
        Assert.Equal("其他", Assert.Single(vm.Groups).Name);
        Assert.Single(vm.Groups[0].Apps);
        Assert.Single(vm.Apps);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task RefreshUpdatesCategoryAfterCatalogRemovalAndRecovery()
    {
        var service = new StubLauncher { Entries = new[] { Entry("PowerToys", "PowerToys") } };
        var catalog = new StubCatalog();
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(service, text, catalog);
        await vm.RefreshEntriesAsync();
        Assert.Equal("系统工具", Assert.Single(vm.Groups).Name);
        catalog.Unavailable = true;
        await vm.RefreshEntriesAsync();
        Assert.Equal("其他", Assert.Single(vm.Groups).Name);
        catalog.Unavailable = false;
        await vm.RefreshEntriesAsync();
        Assert.Equal("系统工具", Assert.Single(vm.Groups).Name);
        text.ApplyLanguage("en-US");
        Assert.Equal("System tools", Assert.Single(vm.Groups).Name);
        Assert.Equal("PowerToys", Assert.Single(vm.Groups[0].Apps).DisplayName);
    }

    private sealed class StubCatalog : ICatalogService
    {
        public bool Unavailable { get; set; }
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        private static readonly IReadOnlyList<Category> All =
        [
            new Category { Id = new CategoryId("dev-tools"), Name = "开发工具", NameEn = "Developer Tools", Order = 0 },
            new Category { Id = new CategoryId("system"), Name = "系统工具", NameEn = "System", Order = 1 },
            new Category { Id = new CategoryId("network"), Name = "网络", NameEn = "Network", Order = 2 },
        ];
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) => Task.FromResult(All);
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default)
        {
            if (Unavailable) throw new RepositoryCatalogException("repository_not_found");
            // The real catalog stores repository keys lowercased; match the same way.
            var key = id.PackageId.Value.ToLowerInvariant();
            var category = key switch
            {
                "fixture/notepad--" => "dev-tools",
                "fixture/powertoys" => "system",
                "fixture/localsend" => "network",
                _ => throw new RepositoryCatalogException("repository_not_found"),
            };
            var app = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", key[8..]), null,
                SeedFixture.FetchedAt, new CategoryId(category)).App;
            return Task.FromResult(new AppDetail { App = app });
        }
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FailingCatalog : ICatalogService
    {
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) => throw new InvalidOperationException("catalog unavailable");
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) => throw new InvalidOperationException("catalog unavailable");
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubLauncher : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<LauncherApp> Entries = [];
        public Func<CancellationToken, Task<IReadOnlyList<LauncherApp>>>? Read;
        public int Reads;
        public List<string> Launched { get; } = [];
        public LauncherLaunchResult LaunchResult = new(true);
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default)
        {
            Reads++;
            return Read?.Invoke(ct) ?? Task.FromResult(Entries);
        }
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default)
        {
            Launched.Add(installId);
            return Task.FromResult(LaunchResult);
        }
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterAsync(string installId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
