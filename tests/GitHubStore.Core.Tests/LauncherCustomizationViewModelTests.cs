using GitHubStore.App.Services.Launcher;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LauncherCustomizationViewModelTests
{
    [Fact]
    public async Task SuccessfulLaunchPersistsCountAndReordersItsGroupBeforeReloadingInANewViewModel()
    {
        using var fixture = new Fixture();
        var managed = new ManagedStub { Entries = [Entry("alpha", "Alpha"), Entry("zulu", "Zulu")] };
        using (var vm = fixture.ViewModel(managed))
        {
            await vm.RefreshEntriesAsync();
            Assert.Equal(new[] { "Alpha", "Zulu" }, Assert.Single(vm.Groups).Apps.Select(item => item.DisplayName));
            await vm.LaunchEntryAsync(vm.Apps.Single(item => item.InstallId == "zulu"));
            Assert.Equal(new[] { "Zulu", "Alpha" }, Assert.Single(vm.Groups).Apps.Select(item => item.DisplayName));
            var launched = vm.Apps[0];
            Assert.Equal(1L, launched.LaunchCount);
            Assert.Contains(launched.LaunchCountText, launched.Tooltip);
            Assert.Equal(fixture.Text["LauncherSortByLaunchCount"], Assert.Single(vm.Groups).SortDescription);
            Assert.Equal(fixture.Text.Format("LauncherStarted", "Zulu"), vm.StatusMessage);
            Assert.Empty(vm.ErrorMessage);
        }
        using var reopened = fixture.ViewModel(managed);
        await reopened.RefreshEntriesAsync();
        Assert.Equal("zulu", reopened.Apps[0].InstallId);
        Assert.Equal(1L, reopened.Apps[0].LaunchCount);
        Assert.Equal(1L, Assert.Single(await fixture.Store().ListAsync()).LaunchCount);
        Assert.Equal(new[] { "zulu" }, managed.Launched);
    }

    [Fact]
    public async Task FailedLaunchDoesNotRecordUsageOrChangeOrdering()
    {
        using var fixture = new Fixture();
        var managed = new ManagedStub
        {
            Entries = [Entry("alpha", "Alpha"), Entry("zulu", "Zulu")],
            LaunchResult = new(false, "launcher_start_failed"),
        };
        using var vm = fixture.ViewModel(managed);
        await vm.RefreshEntriesAsync();
        await vm.LaunchEntryAsync(vm.Apps.Single(item => item.InstallId == "zulu"));
        Assert.Empty(await fixture.Store().ListAsync());
        Assert.All(vm.Apps, item => Assert.Equal(0L, item.LaunchCount));
        Assert.Equal(new[] { "Alpha", "Zulu" }, Assert.Single(vm.Groups).Apps.Select(item => item.DisplayName));
        Assert.Empty(vm.StatusMessage);
        Assert.Equal(fixture.Text["LauncherLaunchFailed"], vm.ErrorMessage);
    }

    [Fact]
    public async Task EqualCountsSortByNameThenSourceAndIdRegardlessOfInputOrder()
    {
        using var fixture = new Fixture();
        var managed = new ManagedStub
        {
            Entries = [Entry("zulu", "Zulu"), Entry("beta", "Same"), Entry("alpha", "Same"), Entry("first", "Alpha")],
        };
        var manualEntry = await fixture.Manual().AddAsync(fixture.Target("manual.exe"), "Same");
        foreach (var entry in managed.Entries) await fixture.Store().RecordLaunchAsync(false, entry.InstallId);
        await fixture.Store().RecordLaunchAsync(true, manualEntry.Id);
        using var vm = fixture.ViewModel(managed, fixture.Manual());
        await vm.RefreshEntriesAsync();
        Assert.Equal(new[] { "first", "alpha", "beta", manualEntry.Id, "zulu" }, Assert.Single(vm.Groups).Apps.Select(item => item.InstallId));
        await vm.RefreshEntriesAsync();
        Assert.Equal(new[] { "first", "alpha", "beta", manualEntry.Id, "zulu" }, vm.Apps.Select(item => item.InstallId));
        Assert.All(vm.Apps, item => Assert.Equal(1L, item.LaunchCount));
    }

    [Fact]
    public async Task SameIdInDifferentSourcesKeepsCountsAndLaunchRoutingSeparate()
    {
        using var fixture = new Fixture();
        var manual = fixture.Manual();
        var local = await manual.AddAsync(fixture.Target("local.lnk"), "Local");
        var managed = new ManagedStub { Entries = [Entry(local.Id, "Installed")] };
        await fixture.Store().RecordLaunchAsync(false, local.Id);
        await fixture.Store().RecordLaunchAsync(false, local.Id);
        using var vm = fixture.ViewModel(managed, manual);
        await vm.RefreshEntriesAsync();
        Assert.Equal(2L, Assert.Single(vm.Apps, item => !item.IsManual).LaunchCount);
        Assert.Equal(0L, Assert.Single(vm.Apps, item => item.IsManual).LaunchCount);
        await vm.LaunchEntryAsync(vm.Apps.Single(item => item.IsManual));
        Assert.Equal(2L, Assert.Single(vm.Apps, item => !item.IsManual).LaunchCount);
        Assert.Equal(1L, Assert.Single(vm.Apps, item => item.IsManual).LaunchCount);
        Assert.Empty(managed.Launched);
        Assert.Equal(new[] { local.Path }, fixture.ManualLaunches);
        var entries = await fixture.Store().ListAsync();
        Assert.Equal(2L, Assert.Single(entries, item => !item.IsManual).LaunchCount);
        Assert.Equal(1L, Assert.Single(entries, item => item.IsManual).LaunchCount);
    }

    [Fact]
    public async Task InstalledPropertiesOverridePresentationAndKeepReceiptAndTrustedPathUnchanged()
    {
        using var fixture = new Fixture();
        var receipt = Entry("installed", "Original");
        var managed = new ManagedStub { Entries = [receipt] };
        await fixture.Store().RecordLaunchAsync(false, receipt.InstallId);
        using var vm = fixture.ViewModel(managed);
        await vm.RefreshEntriesAsync();
        var item = Assert.Single(vm.Apps);
        vm.SearchText = "no matches";
        var category = new CategoryId("dev-tools");
        Assert.True(await vm.SaveEntryPropertiesAsync(item, "  My Editor  ", category, item.ExecutablePath));
        var saved = Assert.Single(vm.Apps);
        Assert.Equal("My Editor", saved.DisplayName);
        Assert.Equal(category, saved.Category);
        Assert.Equal(1L, saved.LaunchCount);
        Assert.Equal(receipt.ExecutablePath, saved.ExecutablePath);
        Assert.Equal(receipt, Assert.Single(managed.Entries));
        Assert.Equal("Original", receipt.DisplayName);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(fixture.Text["category.dev-tools"], Assert.Single(vm.Groups).Name);
        Assert.Equal(fixture.Text.Format("LauncherPropertiesSaved", "My Editor"), vm.StatusMessage);
        var customization = Assert.Single(await fixture.Store().ListAsync());
        Assert.False(customization.IsManual);
        Assert.True(customization.HasCategoryOverride);
        Assert.Equal(category, customization.Category);
        Assert.Equal(1L, customization.LaunchCount);
        using var reopened = fixture.ViewModel(managed);
        await reopened.RefreshEntriesAsync();
        Assert.Equal("My Editor", Assert.Single(reopened.Apps).DisplayName);
        Assert.Equal(category, Assert.Single(reopened.Apps).Category);
    }

    [Fact]
    public async Task InstalledPropertiesRejectAnExecutablePathChangeWithoutSavingOverrides()
    {
        using var fixture = new Fixture();
        var receipt = Entry("installed", "Original");
        var managed = new ManagedStub { Entries = [receipt] };
        using var vm = fixture.ViewModel(managed);
        await vm.RefreshEntriesAsync();
        Assert.False(await vm.SaveEntryPropertiesAsync(Assert.Single(vm.Apps), "New", null, @"C:\elsewhere\tool.exe"));
        Assert.Empty(await fixture.Store().ListAsync());
        Assert.Equal(receipt.ExecutablePath, Assert.Single(vm.Apps).ExecutablePath);
        Assert.Equal("Original", Assert.Single(vm.Apps).DisplayName);
        Assert.Equal(fixture.Text["LauncherInvalidPath"], vm.ErrorMessage);
        Assert.False(vm.IsAddingManual);
    }

    [Fact]
    public async Task ManualPropertySaveUpdatesEntryByIdAndRetainsAddedTimeAndUsage()
    {
        using var fixture = new Fixture();
        var manual = fixture.Manual();
        var original = await manual.AddAsync(fixture.Target("old.exe"), "Original");
        await fixture.Store().RecordLaunchAsync(true, original.Id);
        await fixture.Store().RecordLaunchAsync(true, original.Id);
        var replacement = fixture.Target("replacement.lnk");
        using var vm = fixture.ViewModel(new ManagedStub(), manual);
        await vm.RefreshEntriesAsync();
        var category = new CategoryId("system");
        Assert.True(await vm.SaveEntryPropertiesAsync(Assert.Single(vm.Apps), "Renamed", category, replacement));
        var saved = Assert.Single(await fixture.Manual().ListAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.AddedAt, saved.AddedAt);
        Assert.Equal(replacement, saved.Path);
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal(category, saved.Category);
        var item = Assert.Single(vm.Apps);
        Assert.Equal(original.Id, item.InstallId);
        Assert.Equal(2L, item.LaunchCount);
        Assert.Equal(replacement, item.ExecutablePath);
        Assert.Equal(category, item.Category);
        Assert.Equal(2L, Assert.Single(await fixture.Store().ListAsync()).LaunchCount);
        await vm.LaunchEntryAsync(item);
        Assert.Equal(new[] { replacement }, fixture.ManualLaunches);
        Assert.Equal(3L, Assert.Single(vm.Apps).LaunchCount);
    }

    [Fact]
    public async Task CountWriteFailureKeepsLaunchSuccessAndShowsAWarningWithoutInventingUsage()
    {
        using var fixture = new Fixture();
        var managed = new ManagedStub { Entries = [Entry("id", "Tool")] };
        var store = new FaultingStore(fixture.Store()) { FailRecord = true };
        using var vm = fixture.ViewModel(managed, store: store);
        await vm.RefreshEntriesAsync();
        await vm.LaunchEntryAsync(Assert.Single(vm.Apps));
        Assert.Equal(new[] { "id" }, managed.Launched);
        Assert.Equal(fixture.Text.Format("LauncherStarted", "Tool"), vm.StatusMessage);
        Assert.Equal(fixture.Text["LauncherLaunchCountSaveFailed"], vm.ErrorMessage);
        Assert.Equal(0L, Assert.Single(vm.Apps).LaunchCount);
        Assert.Empty(await fixture.Store().ListAsync());
        Assert.False(vm.IsLaunching);
    }

    [Fact]
    public async Task CustomizationReadFailureStillLoadsBothApplicationSources()
    {
        using var fixture = new Fixture();
        var managed = new ManagedStub { Entries = [Entry("id", "Installed")] };
        var manual = fixture.Manual();
        await manual.AddAsync(fixture.Target("local.exe"), "Local");
        var store = new FaultingStore(fixture.Store()) { FailRead = true };
        using var vm = fixture.ViewModel(managed, manual, store);
        await vm.RefreshEntriesAsync();
        Assert.Equal(2, vm.Apps.Count);
        Assert.Single(vm.Apps, item => item.IsManual);
        Assert.Single(vm.Apps, item => !item.IsManual);
        Assert.Equal(fixture.Text["LauncherCustomizationFailed"], vm.ErrorMessage);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task LaterCustomizationReadFailureKeepsKnownCountsAndOverridesWhileRefreshingApplications()
    {
        using var fixture = new Fixture();
        var managed = new ManagedStub { Entries = [Entry("id", "Original")] };
        await fixture.Store().UpdatePropertiesAsync(false, "id", "Renamed", new CategoryId("system"));
        await fixture.Store().RecordLaunchAsync(false, "id");
        var store = new FaultingStore(fixture.Store());
        using var vm = fixture.ViewModel(managed, store: store);
        await vm.RefreshEntriesAsync();
        store.FailRead = true;
        managed.Entries = managed.Entries.Append(Entry("new", "New Tool")).ToArray();
        await vm.RefreshEntriesAsync();
        Assert.Equal(2, vm.Apps.Count);
        var saved = Assert.Single(vm.Apps, item => item.InstallId == "id");
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal(1L, saved.LaunchCount);
        Assert.Equal(new CategoryId("system"), saved.Category);
        Assert.Equal(fixture.Text["LauncherCustomizationFailed"], vm.ErrorMessage);
    }

    private static LauncherApp Entry(string id, string name)
    {
        var app = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "tool"), null,
            SeedFixture.FetchedAt, new CategoryId("system")).App;
        return new LauncherApp
        {
            InstallId = id, AppId = app.Id, DisplayName = name,
            InstallDirectory = @"C:\githubapps\" + id, ExecutablePath = @"C:\githubapps\" + id + @"\tool.exe",
            IsAvailable = true,
        };
    }

    private sealed class ManagedStub : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<LauncherApp> Entries = [];
        public LauncherLaunchResult LaunchResult = new(true);
        public List<string> Launched { get; } = [];
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) => Task.FromResult(Entries);
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default)
        {
            Launched.Add(installId);
            return Task.FromResult(LaunchResult);
        }
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) =>
            throw new InvalidOperationException("Launcher properties must not rewrite installation receipts");
        public Task UnregisterAsync(string installId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Launcher properties must not unregister installations");
    }

    private sealed class FaultingStore(ILauncherCustomizationStore inner) : ILauncherCustomizationStore
    {
        public bool FailRead;
        public bool FailRecord;
        public Task<IReadOnlyList<LauncherCustomization>> ListAsync(CancellationToken ct = default) => FailRead
            ? throw new LauncherException("launcher_customization_read_failed") : inner.ListAsync(ct);
        public Task<LauncherCustomization> RecordLaunchAsync(bool isManual, string entryId, CancellationToken ct = default) => FailRecord
            ? throw new LauncherException("launcher_customization_write_failed") : inner.RecordLaunchAsync(isManual, entryId, ct);
        public Task<LauncherCustomization> UpdatePropertiesAsync(bool isManual, string entryId, string displayName,
            CategoryId? category, CancellationToken ct = default) => inner.UpdatePropertiesAsync(isManual, entryId, displayName, category, ct);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "githubstore-customization-vm-" + Guid.NewGuid().ToString("N"));
        public LocalizationService Text { get; } = new("zh-CN");
        public List<string> ManualLaunches { get; } = [];
        public Fixture() => Directory.CreateDirectory(Root);
        public LauncherCustomizationStore Store() => new(Root);
        public ManualAppLauncher Manual() => new(Root, (path, _) => { ManualLaunches.Add(path); return true; });
        public LauncherViewModel ViewModel(ManagedStub managed, IManualAppLauncher? manual = null, ILauncherCustomizationStore? store = null) =>
            new(managed, Text, manualLauncher: manual, customizationStore: store ?? Store());
        public string Target(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "test target");
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
