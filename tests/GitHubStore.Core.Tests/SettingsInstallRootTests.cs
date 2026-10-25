using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class SettingsInstallRootTests
{
    [Fact]
    public async Task ExistingManagedAppsProtectRootButAllowCacheChangeAndKeepDraft()
    {
        using var fixture = new Fixture();
        await fixture.Preferences.SaveAsync(new AppPreferences { Language = "zh-CN" });
        var registry = new Registry { HasApp = true };
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), registry);
        await vm.InitializeAsync();
        vm.InstallRootDirectory = fixture.OtherRoot;
        await vm.ApplyPathsAsync();
        Assert.Null(fixture.Preferences.Current.InstallRootDirectory);
        Assert.Equal(fixture.OtherRoot, vm.InstallRootDirectory);
        Assert.Contains("暂不支持更改", vm.ErrorMessage);
        vm.InstallRootDirectory = string.Empty;
        vm.DownloadCacheDirectory = Path.Combine(fixture.Directory, "cache");
        await vm.ApplyPathsAsync();
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(vm.DownloadCacheDirectory, fixture.Preferences.Current.DownloadCacheDirectory);
    }

    [Fact]
    public async Task RootCanChangeBeforeAnyInstallation()
    {
        using var fixture = new Fixture();
        using var vm = new SettingsViewModel(fixture.Preferences, new LocalizationService("zh-CN"), new Registry());
        await vm.InitializeAsync();
        vm.InstallRootDirectory = fixture.OtherRoot;
        await vm.ApplyPathsAsync();
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(fixture.OtherRoot, fixture.Preferences.Current.InstallRootDirectory);
    }

    [Fact]
    public async Task InProgressInstallationProtectsRootWithoutExistingReceipt()
    {
        using var fixture = new Fixture();
        await fixture.Preferences.SaveAsync(new AppPreferences { Language = "zh-CN" });
        var text = new LocalizationService("zh-CN");
        using var installation = new InstallationViewModel(new PendingInstaller(), text);
        using var settings = new SettingsViewModel(fixture.Preferences, text, new Registry(), installation);
        await settings.InitializeAsync();
        var app = new GitHubStore.Core.Abstractions.App { Id = new(new(Guid.NewGuid()), new("fixture/app")), SourceRevision = 1,
            DisplayName = "Fixture", Owner = "fixture", Repo = "app" };
        var asset = new Asset { InstallerId = "zip", ReleaseKey = "v1", Name = "fixture.zip", DownloadUrl = "https://example.com/fixture.zip" };
        var task = installation.InstallAsync(new AppInstallRequest { App = app, Asset = asset,
            Release = new Release { AppId = app.Id, Key = "v1", TagName = "v1", Assets = [asset] } });
        Assert.True(installation.IsInstalling);
        settings.InstallRootDirectory = fixture.OtherRoot;
        await settings.ApplyPathsAsync();
        Assert.Null(fixture.Preferences.Current.InstallRootDirectory);
        Assert.Contains("暂不支持更改", settings.ErrorMessage);
        await installation.CancelCommand.ExecuteAsync(null);
        await task;
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "GitHubStore-root-" + Guid.NewGuid().ToString("N"));
        public string OtherRoot => Path.Combine(Directory, "programs");
        public PreferencesService Preferences { get; }
        public Fixture() => Preferences = new PreferencesService(Path.Combine(Directory, "settings.json"));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Registry : IManagedAppLauncher
    {
        public bool HasApp { get; init; }
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherApp>>(
            HasApp ? [new LauncherApp { InstallId = "existing", AppId = new(new(Guid.NewGuid()), new("fixture/app")), DisplayName = "Existing",
                InstallDirectory = @"C:\githubapps\Existing", ExecutablePath = @"C:\githubapps\Existing\App.exe" }] : []);
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterAsync(string installId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) => Task.FromResult(new LauncherLaunchResult(false));
    }
    private sealed class PendingInstaller : IAppInstallService
    {
        public string GetTargetDirectory(GitHubStore.Core.Abstractions.App app) => @"C:\githubapps\Fixture";
        public async Task<AppInstallResult> InstallAsync(AppInstallRequest request, IProgress<AppInstallProgress>? progress = null,
            Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new(false);
        }
    }
}
