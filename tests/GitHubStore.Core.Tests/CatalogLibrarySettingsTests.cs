using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class CatalogLibrarySettingsTests
{
    [Theory]
    [InlineData("http://example.com/catalog.json")]
    [InlineData("https://user:secret@example.com/catalog.json")]
    [InlineData("https://example.com/catalog.json#section")]
    [InlineData("https://github.com/owner/repo/blob/main/catalog.json")]
    [InlineData("not-a-url")]
    public async Task InvalidAddressDoesNotSaveOrStartDownload(string address)
    {
        using var fixture = new Fixture();
        var sync = new FakeSync();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = address;
        await vm.SyncCatalogLibraryAsync();
        Assert.Empty(sync.Urls);
        // 无效地址既不能覆盖已保存的值，也不能覆盖随程序发布的默认库地址。
        Assert.Equal(AppPreferences.DefaultCatalogLibraryUrl, fixture.Preferences.Current.CatalogLibraryUrl);
        Assert.NotEmpty(vm.ErrorMessage);
    }

    [Fact]
    public async Task CatalogSectionPersistsWithoutSavingOtherDrafts()
    {
        using var fixture = new Fixture();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = " https://example.com/catalog.json ";
        vm.SyncCatalogLibraryOnStartup = false;
        vm.AiEndpoint = "invalid unsaved draft";
        vm.InstallRootDirectory = "invalid draft";
        await vm.ApplyCatalogLibraryAsync();
        Assert.Empty(vm.ErrorMessage);
        var restored = new PreferencesService(fixture.Path);
        await restored.LoadAsync();
        Assert.Equal("https://example.com/catalog.json", restored.Current.CatalogLibraryUrl);
        Assert.False(restored.Current.SyncCatalogLibraryOnStartup);
        Assert.Null(restored.Current.InstallRootDirectory);
        Assert.Equal(new AppPreferences().AiEndpoint, restored.Current.AiEndpoint);
    }

    [Fact]
    public async Task SyncSavesAddressAndReportsCountAndRefreshEvent()
    {
        using var fixture = new Fixture();
        await fixture.Preferences.SaveAsync(new AppPreferences { Language = "zh-CN" });
        var sync = new FakeSync();
        var text = new LocalizationService("zh-CN");
        using var vm = new SettingsViewModel(fixture.Preferences, text, librarySync: sync);
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = "https://example.com/catalog.json";
        var changed = 0;
        vm.CatalogLibrarySynchronized += (_, _) => changed++;
        await vm.SyncCatalogLibraryAsync();
        Assert.Equal(new[] { vm.CatalogLibraryUrl }, sync.Urls);
        Assert.Equal(vm.CatalogLibraryUrl, fixture.Preferences.Current.CatalogLibraryUrl);
        Assert.Equal(1, changed);
        Assert.Contains("9", vm.CatalogLibraryStatus);
        Assert.Contains("同步成功", vm.CatalogLibraryStatus);
        Assert.Empty(vm.CatalogLibraryError);
        Assert.False(vm.IsSynchronizingCatalogLibrary);
        text.ApplyLanguage("en-US");
        Assert.Contains("Sync complete", vm.CatalogLibraryStatus);
    }

    [Fact]
    public async Task StartupUsesSavedAddressAndDoesNotSubmitDrafts()
    {
        using var fixture = new Fixture();
        await fixture.Preferences.SaveAsync(new AppPreferences { CatalogLibraryUrl = "https://example.com/saved.json" });
        var sync = new FakeSync();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = "invalid unsaved draft";
        await vm.SyncCatalogLibraryAtStartupAsync();
        Assert.Equal(new[] { "https://example.com/saved.json" }, sync.Urls);
        Assert.Equal("invalid unsaved draft", vm.CatalogLibraryUrl);
    }

    [Fact]
    public async Task FreshInstallUsesTheShippedAddressForStartupSync()
    {
        using var fixture = new Fixture();
        var sync = new FakeSync();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        Assert.Equal(AppPreferences.DefaultCatalogLibraryUrl, vm.CatalogLibraryUrl);
        await vm.SyncCatalogLibraryAtStartupAsync();
        Assert.Equal(new[] { AppPreferences.DefaultCatalogLibraryUrl }, sync.Urls);
    }

    [Fact]
    public async Task ClearedAddressOrOptOutStopsStartupSync()
    {
        using var fixture = new Fixture();
        var sync = new FakeSync();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        // 清空地址并保存：用户明确不再跟随远端库，之后启动不得联网。
        vm.CatalogLibraryUrl = string.Empty;
        await vm.ApplyCatalogLibraryAsync();
        Assert.Null(fixture.Preferences.Current.CatalogLibraryUrl);
        await vm.SyncCatalogLibraryAtStartupAsync();
        Assert.Empty(sync.Urls);
        vm.CatalogLibraryUrl = AppPreferences.DefaultCatalogLibraryUrl;
        vm.SyncCatalogLibraryOnStartup = false;
        await vm.ApplyCatalogLibraryAsync();
        await vm.SyncCatalogLibraryAtStartupAsync();
        Assert.Empty(sync.Urls);
    }

    [Fact]
    public async Task LegacySettingsWithoutTheOptOutFlagFollowTheShippedAddress()
    {
        using var fixture = new Fixture();
        // 旧版本保存的设置文件：库地址为 null，且没有“主动停用”字段。
        // 这里的 null 只是当时的默认值，升级后必须回落到随程序发布的地址，而不是当作停用。
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path)!);
        await File.WriteAllTextAsync(fixture.Path, """
            { "theme": "System", "language": "system", "catalogLibraryUrl": null }
            """);
        var loaded = new PreferencesService(fixture.Path);
        await loaded.LoadAsync();
        Assert.Equal(AppPreferences.DefaultCatalogLibraryUrl, loaded.Current.CatalogLibraryUrl);
        Assert.False(loaded.Current.CatalogLibraryOptedOut);
    }

    [Fact]
    public async Task ExplicitOptOutSurvivesReloadAndKeepsTheLocalCatalog()
    {
        using var fixture = new Fixture();
        var sync = new FakeSync();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = string.Empty;
        await vm.ApplyCatalogLibraryAsync();
        Assert.True(fixture.Preferences.Current.CatalogLibraryOptedOut);

        // 重新加载：用户主动停用的选择必须保留，不能被默认地址覆盖。
        var reopened = new PreferencesService(fixture.Path);
        await reopened.LoadAsync();
        Assert.Null(reopened.Current.CatalogLibraryUrl);
        Assert.True(reopened.Current.CatalogLibraryOptedOut);
        await vm.SyncCatalogLibraryAtStartupAsync();
        Assert.Empty(sync.Urls);
    }

    [Fact]
    public async Task ActiveSyncRejectsDuplicateRequestsAndCanBeCancelled()
    {
        using var fixture = new Fixture();
        await fixture.Preferences.SaveAsync(new AppPreferences { Language = "zh-CN" });
        var sync = new FakeSync
        {
            Run = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new CatalogLibrarySyncResult(1, DateTimeOffset.UtcNow);
            },
        };
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = "https://example.com/catalog.json";
        var running = vm.SyncCatalogLibraryAsync();
        // Saving preferences may yield before the download begins.
        for (var attempt = 0; attempt < 100 && !vm.IsSynchronizingCatalogLibrary; attempt++) await Task.Delay(5);
        Assert.True(vm.IsSynchronizingCatalogLibrary);
        Assert.False(vm.CanApply);
        await vm.SyncCatalogLibraryAsync();
        Assert.Single(sync.Urls);
        vm.CancelCatalogLibrarySyncCommand.Execute(null);
        await running;
        Assert.False(vm.IsSynchronizingCatalogLibrary);
        Assert.True(vm.CanApply);
        Assert.Contains("已取消", vm.CatalogLibraryStatus);
    }

    [Theory]
    [InlineData("invalid_response", "目录文件无效")]
    [InlineData("network_error", "目录下载失败")]
    [InlineData("cache_write_failed", "无法保存")]
    public async Task FailedSyncShowsStableErrorWithoutClaimingSuccess(string code, string message)
    {
        using var fixture = new Fixture();
        await fixture.Preferences.SaveAsync(new AppPreferences { Language = "zh-CN" });
        var sync = new FakeSync { Run = (_, _) => throw new RepositoryCatalogException(code) };
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), librarySync: sync);
        await vm.InitializeAsync();
        vm.CatalogLibraryUrl = "https://example.com/catalog.json";
        var changed = 0;
        vm.CatalogLibrarySynchronized += (_, _) => changed++;
        await vm.SyncCatalogLibraryAsync();
        Assert.Contains(message, vm.CatalogLibraryError);
        Assert.DoesNotContain("同步成功", vm.CatalogLibraryStatus);
        Assert.Equal(0, changed);
        Assert.True(vm.CanApply);
    }

    private sealed class FakeSync : ICatalogLibrarySyncService
    {
        public List<string> Urls { get; } = [];
        public Func<string, CancellationToken, Task<CatalogLibrarySyncResult>> Run { get; init; } =
            (_, _) => Task.FromResult(new CatalogLibrarySyncResult(9, DateTimeOffset.UtcNow));
        public Task<CatalogLibrarySyncResult> SyncLibraryAsync(string url, CancellationToken ct = default)
        {
            Urls.Add(url);
            return Run(url, ct);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitHubStore-catalog-settings-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "settings.json");
        public PreferencesService Preferences { get; }
        public Fixture() => Preferences = new PreferencesService(Path);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
