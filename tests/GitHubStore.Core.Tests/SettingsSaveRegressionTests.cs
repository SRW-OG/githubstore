using System.Security.Cryptography;
using System.Text;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class SettingsSaveRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PathsSave_OnlyCommitsPathsAndPreservesNetworkAndAppearanceDrafts(bool validNetwork)
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        var committed = preferences.Current;
        vm.DownloadCacheDirectory = "  " + directory.Combine("new-cache") + "  ";
        vm.InstallRootDirectory = directory.Combine("new-apps");
        vm.ProxyHost = "draft.example";
        vm.ProxyPort = validNetwork ? "9090" : "not-a-port";
        vm.ProxyPassword = "network-draft-secret";
        vm.DownloadEngine = DownloadEngine.Http;
        vm.SelectedTheme = ThemeMode.Dark;
        vm.SelectedLanguage = "zh-CN";
        vm.Aria2ExecutablePath = "hidden-draft";

        await vm.ApplyPathsAsync();

        Assert.Equal(committed with
        {
            DownloadCacheDirectory = directory.Combine("new-cache"),
            InstallRootDirectory = directory.Combine("new-apps"),
        }, preferences.Current);
        Assert.Equal(directory.Combine("new-cache"), vm.DownloadCacheDirectory);
        Assert.Equal("draft.example", vm.ProxyHost);
        Assert.Equal(validNetwork ? "9090" : "not-a-port", vm.ProxyPort);
        Assert.Equal("network-draft-secret", vm.ProxyPassword);
        Assert.Equal(DownloadEngine.Http, vm.DownloadEngine);
        Assert.Equal(ThemeMode.Dark, vm.SelectedTheme);
        Assert.Equal("zh-CN", vm.SelectedLanguage);
        Assert.Equal("hidden-draft", vm.Aria2ExecutablePath);
        Assert.True(vm.Text.IsEnglish);
        Assert.Empty(vm.ErrorMessage);
        var restarted = CreatePreferences(directory);
        await restarted.LoadAsync();
        Assert.Equal(preferences.Current, restarted.Current);
        Assert.DoesNotContain("network-draft-secret", await File.ReadAllTextAsync(directory.Combine("settings.json")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkSave_OnlyCommitsNetworkAndPreservesPathAndAppearanceDrafts(bool validPaths)
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        var committed = preferences.Current;
        var cacheDraft = validPaths ? directory.Combine("draft-cache") : "relative-cache";
        var installDraft = validPaths ? directory.Combine("draft-apps") : "relative-apps";
        vm.DownloadCacheDirectory = cacheDraft;
        vm.InstallRootDirectory = installDraft;
        vm.DownloadEngine = DownloadEngine.Http;
        vm.UseAccelerator = true;
        vm.EnableP2P = true;
        vm.ProxyHost = "  draft.example  ";
        vm.ProxyPort = " 9090 ";
        vm.ProxyUserName = "  draft-user  ";
        vm.ProxyPassword = "network-draft-secret";
        vm.SelectedTheme = ThemeMode.Dark;
        vm.SelectedLanguage = "zh-CN";
        vm.XgetInstance = "hidden-draft";

        await vm.ApplyNetworkAsync();

        Assert.Equal(committed with
        {
            DownloadEngine = DownloadEngine.Http,
            UseAccelerator = true,
            EnableP2P = true,
            ProxyHost = "draft.example",
            ProxyPort = 9090,
            ProxyUserName = "draft-user",
            ProxyPassword = "network-draft-secret",
        }, preferences.Current);
        Assert.Equal("draft.example", vm.ProxyHost);
        Assert.Equal("9090", vm.ProxyPort);
        Assert.Equal("draft-user", vm.ProxyUserName);
        Assert.Equal(cacheDraft, vm.DownloadCacheDirectory);
        Assert.Equal(installDraft, vm.InstallRootDirectory);
        Assert.Equal(ThemeMode.Dark, vm.SelectedTheme);
        Assert.Equal("zh-CN", vm.SelectedLanguage);
        Assert.Equal("hidden-draft", vm.XgetInstance);
        Assert.True(vm.Text.IsEnglish);
        Assert.Empty(vm.ErrorMessage);
        var restarted = CreatePreferences(directory);
        await restarted.LoadAsync();
        Assert.Equal(preferences.Current, restarted.Current);
        Assert.DoesNotContain("network-draft-secret", await File.ReadAllTextAsync(directory.Combine("settings.json")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("network")]
    [InlineData("appearance")]
    [InlineData("all")]
    public async Task ValidationFailure_PreservesDraftsAndCommittedFileClearsSuccessAndAllowsRetry(string section)
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        await vm.ApplyPathsAsync();
        Assert.NotEmpty(vm.StatusMessage);
        var committed = preferences.Current;
        var original = await File.ReadAllTextAsync(directory.Combine("settings.json"));
        vm.DownloadCacheDirectory = section == "paths" ? "relative-cache" : directory.Combine("draft-cache");
        vm.InstallRootDirectory = directory.Combine("draft-apps");
        vm.ProxyHost = "draft.example";
        vm.ProxyPort = section is "network" or "all" ? "invalid-port" : "9090";
        vm.ProxyPassword = "draft-secret";
        vm.SelectedTheme = ThemeMode.Dark;
        vm.SelectedLanguage = section == "appearance" ? "de-DE" : "zh-CN";
        var cacheDraft = vm.DownloadCacheDirectory;
        var portDraft = vm.ProxyPort;
        var languageDraft = vm.SelectedLanguage;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await SaveAsync(vm, section);

        Assert.NotEmpty(vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
        Assert.True(vm.HasFeedback);
        Assert.Contains(nameof(vm.HasFeedback), notifications);
        Assert.Equal(committed, preferences.Current);
        Assert.Equal(original, await File.ReadAllTextAsync(directory.Combine("settings.json")));
        Assert.Equal(cacheDraft, vm.DownloadCacheDirectory);
        Assert.Equal(directory.Combine("draft-apps"), vm.InstallRootDirectory);
        Assert.Equal("draft.example", vm.ProxyHost);
        Assert.Equal(portDraft, vm.ProxyPort);
        Assert.Equal("draft-secret", vm.ProxyPassword);
        Assert.Equal(ThemeMode.Dark, vm.SelectedTheme);
        Assert.Equal(languageDraft, vm.SelectedLanguage);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanEditProxy);

        vm.DownloadCacheDirectory = directory.Combine("draft-cache");
        vm.ProxyPort = "9090";
        vm.SelectedLanguage = "zh-CN";
        await SaveAsync(vm, section);

        Assert.Empty(vm.ErrorMessage);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.True(vm.HasFeedback);
        Assert.NotEqual(committed, preferences.Current);
    }

    [Fact]
    public async Task ProtectionFailure_PreservesSecretAndOtherDraftsAndCanBeRetried()
    {
        using var directory = new TempDirectory();
        var protector = new FakeProtector();
        var preferences = CreatePreferences(directory, protector);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        await vm.ApplyNetworkAsync();
        var committed = preferences.Current;
        var original = await File.ReadAllTextAsync(directory.Combine("settings.json"));
        vm.ProxyPassword = "replacement-secret";
        vm.ProxyPort = "9090";
        vm.DownloadCacheDirectory = "unfinished-path";
        protector.FailProtect = true;

        await vm.ApplyNetworkAsync();

        Assert.Equal(vm.Text["SettingsSecretProtectionFailed"], vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
        Assert.Equal("replacement-secret", vm.ProxyPassword);
        Assert.Equal("9090", vm.ProxyPort);
        Assert.Equal("unfinished-path", vm.DownloadCacheDirectory);
        Assert.Equal(committed, preferences.Current);
        Assert.Equal(original, await File.ReadAllTextAsync(directory.Combine("settings.json")));
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
        Assert.DoesNotContain("replacement-secret", vm.ErrorMessage, StringComparison.Ordinal);
        protector.FailProtect = false;

        await vm.ApplyNetworkAsync();

        Assert.Empty(vm.ErrorMessage);
        Assert.Equal("replacement-secret", preferences.Current.ProxyPassword);
        Assert.Equal(committed.DownloadCacheDirectory, preferences.Current.DownloadCacheDirectory);
        Assert.Equal("unfinished-path", vm.DownloadCacheDirectory);
        Assert.DoesNotContain("replacement-secret", await File.ReadAllTextAsync(directory.Combine("settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishFailure_PreservesPathAndNetworkDraftsAndCommittedFileThenAllowsRetry()
    {
        // A Windows reader without FileShare.Delete blocks atomic publication.
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        await vm.ApplyPathsAsync();
        var committed = preferences.Current;
        var original = await File.ReadAllTextAsync(path);
        vm.DownloadCacheDirectory = directory.Combine("draft-cache");
        vm.InstallRootDirectory = directory.Combine("draft-apps");
        vm.ProxyPort = "unfinished-port";
        vm.ProxyPassword = "unfinished-secret";

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await vm.ApplyPathsAsync();

        Assert.NotEmpty(vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
        Assert.Equal(directory.Combine("draft-cache"), vm.DownloadCacheDirectory);
        Assert.Equal(directory.Combine("draft-apps"), vm.InstallRootDirectory);
        Assert.Equal("unfinished-port", vm.ProxyPort);
        Assert.Equal("unfinished-secret", vm.ProxyPassword);
        Assert.Equal(committed, preferences.Current);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
        Assert.False(vm.IsBusy);

        await vm.ApplyPathsAsync();

        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(directory.Combine("draft-cache"), preferences.Current.DownloadCacheDirectory);
        Assert.Equal(committed.ProxyPassword, preferences.Current.ProxyPassword);
        Assert.Equal("unfinished-port", vm.ProxyPort);
    }

    [Fact]
    public async Task PermissionFailure_UsesPermissionMessageAndPreservesDraftAndOriginalFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        await vm.ApplyAppearanceAsync(ThemeMode.System, "en-US");
        var committed = preferences.Current;
        var original = await File.ReadAllTextAsync(path);
        var attributes = File.GetAttributes(path);
        vm.SelectedTheme = ThemeMode.Dark;
        vm.ProxyPassword = "unfinished-secret";
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);

            await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");

            Assert.Equal(vm.Text["SettingsSavePermissionDenied"], vm.ErrorMessage);
            Assert.Empty(vm.StatusMessage);
            Assert.Equal(ThemeMode.Dark, vm.SelectedTheme);
            Assert.Equal("unfinished-secret", vm.ProxyPassword);
            Assert.Equal(committed, preferences.Current);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.False(vm.IsBusy);
            Assert.Single(Directory.GetFiles(directory.DirectoryPath));
        }
        finally { File.SetAttributes(path, attributes); }

        await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");

        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(ThemeMode.Dark, preferences.Current.Theme);
        Assert.Equal(committed.ProxyPassword, preferences.Current.ProxyPassword);
        Assert.Equal("unfinished-secret", vm.ProxyPassword);
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("network")]
    [InlineData("appearance")]
    [InlineData("all")]
    public async Task Cancellation_PropagatesAndRestoresBusyWithoutDiscardingDrafts(string section)
    {
        using var directory = new TempDirectory();
        var protector = new FakeProtector();
        var preferences = CreatePreferences(directory, protector);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        await vm.ApplyPathsAsync();
        var committed = preferences.Current;
        var original = await File.ReadAllTextAsync(directory.Combine("settings.json"));
        vm.DownloadCacheDirectory = directory.Combine("draft-cache");
        vm.ProxyPassword = "draft-secret";
        vm.SelectedTheme = ThemeMode.Dark;
        vm.SelectedLanguage = "zh-CN";
        using var cancellation = new CancellationTokenSource();
        // Cancel after validation/protection begins, before the service publishes.
        protector.OnProtect = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SaveAsync(vm, section, cancellation.Token));

        Assert.False(vm.IsBusy);
        Assert.True(vm.CanApply);
        Assert.True(vm.CanEditProxy);
        Assert.Empty(vm.ErrorMessage);
        Assert.Empty(vm.StatusMessage);
        Assert.False(vm.HasFeedback);
        Assert.Equal(directory.Combine("draft-cache"), vm.DownloadCacheDirectory);
        Assert.Equal("draft-secret", vm.ProxyPassword);
        Assert.Equal(ThemeMode.Dark, vm.SelectedTheme);
        Assert.Equal("zh-CN", vm.SelectedLanguage);
        Assert.Equal(committed, preferences.Current);
        Assert.Equal(original, await File.ReadAllTextAsync(directory.Combine("settings.json")));
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
    }

    [Fact]
    public async Task ProxyEditing_FollowsInitializationKindAndSaveBusyStateAndRejectsReentrantSaves()
    {
        using var directory = new TempDirectory();
        var protector = new FakeProtector();
        var preferences = CreatePreferences(directory, protector);
        await preferences.SaveAsync(SavedPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        vm.ProxyKind = ProxyKind.Http;
        Assert.True(vm.IsProxyEnabled);
        Assert.False(vm.CanEditProxy);
        await vm.InitializeAsync();
        Assert.True(vm.CanEditProxy);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        vm.ProxyKind = ProxyKind.None;
        Assert.False(vm.CanEditProxy);
        Assert.Contains(nameof(vm.CanEditProxy), notifications);
        vm.ProxyKind = ProxyKind.Http;
        var committed = preferences.Current;
        var checkedBusy = false;
        protector.OnProtect = () =>
        {
            checkedBusy = true;
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsProxyEnabled);
            Assert.False(vm.CanEditProxy);
            Assert.Contains(nameof(vm.CanEditProxy), notifications);
            Assert.True(vm.ApplyPathsAsync().IsCompletedSuccessfully);
            Assert.True(vm.ApplyNetworkAsync().IsCompletedSuccessfully);
            Assert.True(vm.ApplyAppearanceAsync(ThemeMode.Light, "zh-CN").IsCompletedSuccessfully);
            Assert.True(vm.ApplyAsync(ThemeMode.Light, "zh-CN").IsCompletedSuccessfully);
            Assert.Equal(committed, preferences.Current);
        };

        await vm.ApplyNetworkAsync();

        Assert.True(checkedBusy);
        Assert.Empty(vm.ErrorMessage);
        Assert.True(vm.CanEditProxy);
        Assert.Equal(committed, preferences.Current);
    }

    [Fact]
    public async Task ReportFailure_ClearsPreviousSuccessAndNotifiesGlobalFeedback()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("en-US"));
        await vm.InitializeAsync();
        Assert.False(vm.HasFeedback);
        await vm.ApplyPathsAsync();
        Assert.True(vm.HasFeedback);
        Assert.NotEmpty(vm.StatusMessage);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        vm.ReportFailure();

        Assert.Empty(vm.StatusMessage);
        Assert.Equal(vm.Text["SettingsSaveFailed"], vm.ErrorMessage);
        Assert.True(vm.HasFeedback);
        Assert.Contains(nameof(vm.HasFeedback), notifications);
    }

    private static Task SaveAsync(SettingsViewModel vm, string section, CancellationToken ct = default) => section switch
    {
        "paths" => vm.ApplyPathsAsync(ct),
        "network" => vm.ApplyNetworkAsync(ct),
        "appearance" => vm.ApplyAppearanceAsync(vm.SelectedTheme, vm.SelectedLanguage, ct),
        "all" => vm.ApplyAsync(vm.SelectedTheme, vm.SelectedLanguage, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    private static PreferencesService CreatePreferences(TempDirectory directory, FakeProtector? protector = null) =>
        new(directory.Combine("settings.json"), protector ?? new FakeProtector());

    private static AppPreferences SavedPreferences(TempDirectory directory) => new()
    {
        Language = "en-US",
        DownloadCacheDirectory = directory.Combine("saved-cache"),
        InstallRootDirectory = directory.Combine("saved-apps"),
        DownloadEngine = DownloadEngine.Aria2,
        Aria2ExecutablePath = directory.Combine("legacy-aria2.exe"),
        XgetInstance = "https://legacy.example/",
        ProxyKind = ProxyKind.Http,
        ProxyHost = "localhost",
        ProxyPort = 8080,
        ProxyUserName = "saved-user",
        ProxyPassword = "saved-secret",
    };

    private sealed class FakeProtector : ISecretProtector
    {
        public bool FailProtect { get; set; }
        public Action? OnProtect { get; set; }

        public string Protect(string plaintext)
        {
            OnProtect?.Invoke();
            if (FailProtect) throw new CryptographicException("Internal protection failure: " + plaintext);
            return "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }

        public string Unprotect(string protectedValue) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[5..]));
    }
}
