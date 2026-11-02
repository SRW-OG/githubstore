using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ManualLauncherViewModelTests
{
    [Fact]
    public async Task RefreshMergesManualAndInstalledEntriesWithDistinctSourcesAndCatalogGroups()
    {
        var installed = ManagedEntry();
        var managed = new ManagedStub { Entries = [installed] };
        var manual = new ManualStub { Entries = [ManualEntry("manual", "Alpha Local", "dev-tools")] };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, new CatalogStub(), manual);
        await vm.RefreshEntriesAsync();

        Assert.Equal(new[] { "Alpha Local", "Store Tool" }, vm.Apps.Select(item => item.DisplayName));
        var localItem = Assert.Single(vm.Apps, item => item.IsManual);
        var installedItem = Assert.Single(vm.Apps, item => !item.IsManual);
        Assert.Equal(text["LauncherManualSource"], localItem.SourceLabel);
        Assert.Equal(text["LauncherStoreSource"], installedItem.SourceLabel);
        Assert.Contains(localItem.LaunchCountText, localItem.Tooltip);
        Assert.Contains(localItem.ExecutablePath, localItem.Tooltip);
        Assert.Equal(new[] { text["category.dev-tools"], text["category.system"] }, vm.Groups.Select(group => group.Name));
        Assert.Same(localItem, Assert.Single(vm.Groups[0].Apps));
        Assert.Same(installedItem, Assert.Single(vm.Groups[1].Apps));
        Assert.Equal(text.Format("LauncherCount", 2, 2), vm.CountText);
        Assert.Equal(1, managed.Reads);
        Assert.Equal(1, manual.Reads);
    }

    [Fact]
    public async Task ManualCategoryAndPathSearchWorkWithoutAnInstalledCatalogEntry()
    {
        var manual = new ManualStub
        {
            Entries = [ManualEntry("local", "Local Editor", "dev-tools"), ManualEntry("other", "Uncategorized")],
        };
        var catalog = new CatalogStub();
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(new ManagedStub(), text, catalog, manual);
        await vm.RefreshEntriesAsync();
        Assert.Equal(new[] { text["category.dev-tools"], text["LauncherUncategorized"] }, vm.Groups.Select(group => group.Name));
        Assert.Equal(0, catalog.AppReads);

        vm.SearchText = "EDITOR";
        Assert.Equal("local", Assert.Single(vm.Apps).InstallId);
        Assert.Equal(text["category.dev-tools"], Assert.Single(vm.Groups).Name);
        vm.SearchText = @"shortcuts\other.lnk";
        Assert.Equal("other", Assert.Single(vm.Apps).InstallId);
        Assert.Equal(text["LauncherUncategorized"], Assert.Single(vm.Groups).Name);
        vm.SearchText = "not present";
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Groups);
        Assert.Equal(text["LauncherNoMatches"], vm.EmptyMessage);
        Assert.Equal(text.Format("LauncherCount", 0, 2), vm.CountText);
    }

    [Fact]
    public async Task LaunchRoutesCollidingIdsToTheirOwnServiceAndNeverPassesThePath()
    {
        var managed = new ManagedStub { Entries = [ManagedEntry() with { InstallId = "same-id" }] };
        var manual = new ManualStub { Entries = [ManualEntry("same-id", "Local Tool")] };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, manualLauncher: manual);
        await vm.RefreshEntriesAsync();
        await vm.LaunchEntryAsync(vm.Apps.Single(item => item.IsManual));
        Assert.Equal(new[] { "same-id" }, manual.Launched);
        Assert.Empty(managed.Launched);
        Assert.Equal(text.Format("LauncherStarted", "Local Tool"), vm.StatusMessage);
        await vm.LaunchEntryAsync(vm.Apps.Single(item => !item.IsManual));
        Assert.Equal(new[] { "same-id" }, managed.Launched);
        Assert.Single(manual.Launched);
        Assert.False(vm.IsLaunching);
    }

    [Fact]
    public async Task UnavailableManualEntryStaysVisibleAndDoesNotCallEitherLauncher()
    {
        var managed = new ManagedStub();
        var manual = new ManualStub { Entries = [ManualEntry("missing", "Missing Tool") with { IsAvailable = false }] };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, manualLauncher: manual);
        await vm.RefreshEntriesAsync();
        var item = Assert.Single(vm.Apps);
        Assert.False(item.IsAvailable);
        await vm.LaunchEntryAsync(item);
        Assert.Empty(manual.Launched);
        Assert.Empty(managed.Launched);
        Assert.Equal(text["LauncherExecutableMissing"], vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
    }

    [Fact]
    public async Task RemoveManualEntryOnlyCallsManualServiceEvenWhenIdsCollide()
    {
        var installed = ManagedEntry() with { InstallId = "same-id" };
        var managed = new ManagedStub { Entries = [installed] };
        var manual = new ManualStub { Entries = [ManualEntry("same-id", "Local Tool")] };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, manualLauncher: manual);
        await vm.RefreshEntriesAsync();
        await vm.RemoveManualEntryAsync(vm.Apps.Single(item => item.IsManual));
        Assert.Equal(new[] { "same-id" }, manual.Removed);
        Assert.Empty(managed.Unregistered);
        Assert.Equal(installed, Assert.Single(managed.Entries));
        Assert.False(Assert.Single(vm.Apps).IsManual);
        Assert.Equal(text.Format("LauncherManualRemoved", "Local Tool"), vm.StatusMessage);
        await vm.RemoveManualEntryAsync(Assert.Single(vm.Apps));
        Assert.Single(manual.Removed);
        Assert.Empty(managed.Unregistered);
        Assert.False(vm.IsAddingManual);
    }

    [Fact]
    public async Task AddTrimsNameClearsSearchAndRefreshesSavedCategory()
    {
        var managed = new ManagedStub { Entries = [ManagedEntry()] };
        var manual = new ManualStub();
        var catalog = new CatalogStub();
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, catalog, manual);
        var categories = await vm.GetManualCategoriesAsync();
        Assert.Equal(new[] { new CategoryId("dev-tools"), new CategoryId("system") }, categories.Select(category => category.Id));
        await vm.RefreshEntriesAsync();
        vm.SearchText = "no matches";
        Assert.Empty(vm.Apps);
        var path = @"C:\external\editor.exe";
        var category = new CategoryId("dev-tools");
        Assert.True(await vm.AddManualEntryAsync(path, "  Editor  ", category));

        var added = Assert.Single(manual.Added);
        Assert.Equal((path, "Editor", (CategoryId?)category), added);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(2, vm.Apps.Count);
        var saved = Assert.Single(manual.Entries);
        Assert.Equal(category, saved.Category);
        Assert.Equal(saved.Id, Assert.Single(vm.Groups[0].Apps).InstallId);
        Assert.Equal(text["category.dev-tools"], vm.Groups[0].Name);
        Assert.Equal(text.Format("LauncherManualAdded", "Editor"), vm.StatusMessage);
        Assert.Equal(2, manual.Reads);
        Assert.Equal(2, managed.Reads);
        Assert.False(vm.IsAddingManual);
    }

    [Theory]
    [InlineData("manual_launcher_invalid_path", "LauncherManualInvalidPath")]
    [InlineData("manual_launcher_unsafe_path", "LauncherManualInvalidPath")]
    [InlineData("manual_launcher_path_unavailable", "LauncherManualInvalidPath")]
    [InlineData("manual_launcher_invalid_display_name", "LauncherManualInvalidName")]
    [InlineData("manual_launcher_entry_missing", "LauncherExecutableMissing")]
    [InlineData("manual_launcher_not_found", "LauncherEntryMissing")]
    [InlineData("manual_launcher_invalid_id", "LauncherEntryMissing")]
    [InlineData("manual_launcher_access_denied", "LauncherAccessDenied")]
    [InlineData("manual_launcher_invalid_manifest", "LauncherManualRegistryInvalid")]
    [InlineData("manual_launcher_manifest_too_large", "LauncherManualRegistryInvalid")]
    [InlineData("manual_launcher_start_failed", "LauncherLaunchFailed")]
    [InlineData("manual_launcher_read_failed", "LauncherManualFailed")]
    [InlineData("manual_launcher_write_failed", "LauncherManualFailed")]
    [InlineData("manual_launcher_invalid_category", "LauncherManualFailed")]
    public async Task ManualLaunchFailureDisplaysMappedErrorWithoutSuccess(string code, string textKey)
    {
        var managed = new ManagedStub();
        var manual = new ManualStub
        {
            Entries = [ManualEntry("local", "Local Tool")],
            LaunchResult = new(false, code),
        };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, manualLauncher: manual);
        await vm.RefreshEntriesAsync();
        await vm.LaunchEntryAsync(Assert.Single(vm.Apps));
        Assert.Equal(text[textKey], vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
        Assert.Equal(new[] { "local" }, manual.Launched);
        Assert.Empty(managed.Launched);
        Assert.False(vm.IsLaunching);
    }

    [Fact]
    public async Task FailedAddKeepsSearchAndEntriesAndReportsTheManualError()
    {
        var manual = new ManualStub
        {
            Entries = [ManualEntry("original", "Original")],
            AddFailure = new LauncherException("manual_launcher_invalid_manifest"),
        };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(new ManagedStub(), text, manualLauncher: manual);
        await vm.RefreshEntriesAsync();
        vm.SearchText = "Original";
        Assert.False(await vm.AddManualEntryAsync(@"C:\external\tool.exe", "Tool", null));
        Assert.Equal("Original", vm.SearchText);
        Assert.Equal("original", Assert.Single(vm.Apps).InstallId);
        Assert.Equal(text["LauncherManualRegistryInvalid"], vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
        Assert.False(vm.IsAddingManual);
        Assert.Equal(1, manual.Reads);
    }

    [Fact]
    public async Task FailedManualRefreshKeepsBothSourcesVisibleAndDisplaysIndexError()
    {
        var managed = new ManagedStub { Entries = [ManagedEntry()] };
        var manual = new ManualStub { Entries = [ManualEntry("local", "Local Tool")] };
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(managed, text, manualLauncher: manual);
        await vm.RefreshEntriesAsync();
        manual.ReadFailure = new LauncherException("manual_launcher_invalid_manifest");
        await vm.RefreshEntriesAsync();
        Assert.Equal(2, vm.Apps.Count);
        Assert.Contains(vm.Apps, item => item.IsManual);
        Assert.Contains(vm.Apps, item => !item.IsManual);
        Assert.Equal(text["LauncherManualRegistryInvalid"], vm.ErrorMessage);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public void UnexpectedManualErrorsDoNotExposeExceptionDetails()
    {
        var text = new LocalizationService("zh-CN");
        using var vm = new LauncherViewModel(new ManagedStub(), text, manualLauncher: new ManualStub());
        vm.ReportManualError(new InvalidOperationException("private process details"));
        Assert.Equal(text["LauncherManualFailed"], vm.ErrorMessage);
        Assert.DoesNotContain("private process details", vm.ErrorMessage);
    }

    private static ManualLauncherEntry ManualEntry(string id, string name, string? category = null) => new()
    {
        Id = id, DisplayName = name, Path = @"C:\external\shortcuts\" + id + ".lnk",
        Category = category is null ? null : new CategoryId(category),
        AddedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), IsAvailable = true,
    };

    private static LauncherApp ManagedEntry()
    {
        var app = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "store-tool"), null,
            SeedFixture.FetchedAt, new CategoryId("system")).App;
        return new LauncherApp
        {
            InstallId = "install-store-tool", AppId = app.Id, DisplayName = "Store Tool",
            InstallDirectory = @"C:\githubapps\store-tool", ExecutablePath = @"C:\githubapps\store-tool\app.exe",
            IsAvailable = true,
        };
    }

    private sealed class ManagedStub : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<LauncherApp> Entries = [];
        public int Reads;
        public List<string> Launched { get; } = [];
        public List<string> Unregistered { get; } = [];
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Entries);
        }
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default)
        {
            Launched.Add(installId);
            return Task.FromResult(new LauncherLaunchResult(true));
        }
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) => throw new InvalidOperationException("Manual entries must not become installations");
        public Task UnregisterAsync(string installId, CancellationToken ct = default)
        {
            Unregistered.Add(installId);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualStub : IManualAppLauncher
    {
        public IReadOnlyList<ManualLauncherEntry> Entries = [];
        public int Reads;
        public Exception? AddFailure;
        public Exception? ReadFailure;
        public LauncherLaunchResult LaunchResult = new(true);
        public List<string> Launched { get; } = [];
        public List<string> Removed { get; } = [];
        public List<(string Path, string DisplayName, CategoryId? Category)> Added { get; } = [];
        public Task<IReadOnlyList<ManualLauncherEntry>> ListAsync(CancellationToken ct = default)
        {
            Reads++;
            if (ReadFailure is not null) throw ReadFailure;
            return Task.FromResult(Entries);
        }
        public Task<ManualLauncherEntry> AddAsync(string path, string displayName, CategoryId? category = null, CancellationToken ct = default)
        {
            if (AddFailure is not null) throw AddFailure;
            Added.Add((path, displayName, category));
            var entry = ManualEntry("added-id", displayName) with { Path = path, Category = category };
            Entries = Entries.Append(entry).ToArray();
            return Task.FromResult(entry);
        }
        public Task RemoveAsync(string id, CancellationToken ct = default)
        {
            Removed.Add(id);
            Entries = Entries.Where(entry => entry.Id != id).ToArray();
            return Task.CompletedTask;
        }
        public Task<LauncherLaunchResult> LaunchAsync(string id, CancellationToken ct = default)
        {
            Launched.Add(id);
            return Task.FromResult(LaunchResult);
        }
    }

    private sealed class CatalogStub : ICatalogService
    {
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public int AppReads;
        private static readonly IReadOnlyList<Category> Categories =
        [
            new Category { Id = new("dev-tools"), Name = "开发工具", NameEn = "Developer tools", Order = 0 },
            new Category { Id = new("system"), Name = "系统工具", NameEn = "System tools", Order = 1 },
        ];
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) => Task.FromResult(Categories);
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default)
        {
            AppReads++;
            var app = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "store-tool"), null,
                SeedFixture.FetchedAt, new CategoryId("system")).App;
            return Task.FromResult(new AppDetail { App = app });
        }
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
