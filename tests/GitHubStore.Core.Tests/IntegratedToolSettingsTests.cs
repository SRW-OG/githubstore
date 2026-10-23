using System.Text;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>Exercises the settings presentation state without WinUI, a process or a network connection.</summary>
[Trait("Category", "Unit")]
public sealed class IntegratedToolSettingsTests
{
    [Fact]
    public async Task Probe_AwaitsTheInjectedApiAndOnlyThenReportsReady()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        vm.DownloadEngine = DownloadEngine.Aria2;
        vm.ProxyKind = ProxyKind.Http;
        Assert.True(vm.CanEditProxy);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        var checking = vm.ProbeDownloadEngineAsync(token =>
        {
            Assert.Equal(cancellation.Token, token);
            calls++;
            return completion.Task;
        }, cancellation.Token);

        Assert.Equal(1, calls);
        Assert.False(checking.IsCompleted);
        Assert.True(vm.IsProbing);
        Assert.False(vm.CanApply);
        Assert.True(vm.IsProxyEnabled);
        Assert.False(vm.CanEditProxy);
        Assert.Equal(vm.Text["DownloadTesting"], vm.Aria2Status);
        Assert.Contains(nameof(vm.CanApply), notifications);
        Assert.Contains(nameof(vm.CanEditProxy), notifications);

        completion.SetResult(true);
        await checking;

        Assert.False(vm.IsProbing);
        Assert.True(vm.CanApply);
        Assert.True(vm.CanEditProxy);
        Assert.Equal(vm.Text["DownloadAria2Found"], vm.Aria2Status);
        Assert.Equal(DownloadEngine.Aria2, vm.DownloadEngine);
        Assert.Equal(DownloadEngine.Http, preferences.Current.DownloadEngine);
    }

    [Fact]
    public async Task Probe_UnavailableReportsIntegratedComponentWithoutChangingEngine()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        vm.DownloadEngine = DownloadEngine.Aria2;

        await vm.ProbeDownloadEngineAsync(_ => Task.FromResult(false));

        Assert.Equal(vm.Text["DownloadAria2Missing"], vm.Aria2Status);
        Assert.Equal(DownloadEngine.Aria2, vm.DownloadEngine);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Probe_WithoutDelegateDoesNotInventMissingOrReadyResults()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        await vm.ProbeDownloadEngineAsync(_ => Task.FromResult(true));

        await vm.ProbeDownloadEngineAsync(null);

        Assert.Equal(vm.Text["DownloadComponentsInitializing"], vm.Aria2Status);
        Assert.NotEqual(vm.Text["DownloadAria2Found"], vm.Aria2Status);
        Assert.NotEqual(vm.Text["DownloadAria2Missing"], vm.Aria2Status);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Probe_AlreadyCancelledDoesNotCallDelegateOrKeepStaleReadyStatus()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        await vm.ProbeDownloadEngineAsync(_ => Task.FromResult(true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;

        await vm.ProbeDownloadEngineAsync(_ =>
        {
            called = true;
            return Task.FromResult(true);
        }, cancellation.Token);

        Assert.False(called);
        Assert.Equal(vm.Text["DownloadComponentCheckCancelled"], vm.Aria2Status);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Probe_CancelledWhileAwaitingCannotReportReadyFromALateResult()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var checking = vm.ProbeDownloadEngineAsync(_ => completion.Task, cancellation.Token);

        cancellation.Cancel();
        completion.SetResult(true);
        await checking;

        Assert.Equal(vm.Text["DownloadComponentCheckCancelled"], vm.Aria2Status);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Probe_DelegateCancellationIsReportedAsCancellation()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("en-US"));
        await vm.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await vm.ProbeDownloadEngineAsync(_ => Task.FromCanceled<bool>(cancellation.Token));

        Assert.Equal(vm.Text["DownloadComponentCheckCancelled"], vm.Aria2Status);
        Assert.NotEqual(vm.Text["DownloadAria2Missing"], vm.Aria2Status);
        Assert.False(vm.IsProbing);
    }

    [Fact]
    public async Task Probe_ExceptionReportsCheckFailureAndAllowsRetry()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(CreatePreferences(directory), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        vm.DownloadEngine = DownloadEngine.Aria2;
        await vm.ProbeDownloadEngineAsync(_ => Task.FromResult(true));

        await vm.ProbeDownloadEngineAsync(_ => Task.FromException<bool>(new IOException("internal-path-must-not-be-shown")));

        Assert.Equal(vm.Text["DownloadComponentCheckFailed"], vm.Aria2Status);
        Assert.DoesNotContain("internal-path-must-not-be-shown", vm.Aria2Status, StringComparison.Ordinal);
        Assert.NotEqual(vm.Text["DownloadAria2Missing"], vm.Aria2Status);
        Assert.Equal(DownloadEngine.Aria2, vm.DownloadEngine);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanApply);

        await vm.ProbeDownloadEngineAsync(_ => Task.FromResult(true));
        Assert.Equal(vm.Text["DownloadAria2Found"], vm.Aria2Status);
    }

    [Fact]
    public async Task Probe_DoesNotReenterOrAllowSaveWhileTheApiIsPending()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        var committed = preferences.Current;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<bool> Probe(CancellationToken _)
        {
            calls++;
            return completion.Task;
        }
        var checking = vm.ProbeDownloadEngineAsync(Probe);

        await vm.ProbeDownloadEngineAsync(Probe);
        await vm.ApplyAsync(ThemeMode.Dark, "en-US");
        await vm.ApplyAppearanceAsync(ThemeMode.Light, "en-US");
        await vm.ApplyPathsAsync();
        await vm.ApplyNetworkAsync();

        Assert.Equal(1, calls);
        Assert.True(vm.IsProbing);
        Assert.False(vm.CanApply);
        Assert.Equal(committed, preferences.Current);
        Assert.Equal(vm.Text["DownloadTesting"], vm.Aria2Status);

        completion.SetResult(true);
        await checking;
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task AppearanceSave_PreservesCommittedNetworkAndHiddenFieldsWithoutSubmittingDrafts()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(LegacyPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        var committed = preferences.Current;
        vm.DownloadEngine = DownloadEngine.Http;
        vm.UseAccelerator = true;
        vm.ProxyKind = ProxyKind.None;
        vm.ProxyHost = "unfinished draft";
        vm.ProxyPort = "invalid port";
        vm.ProxyPassword = "unfinished-password";
        vm.DownloadCacheDirectory = "relative-draft";
        vm.InstallRootDirectory = "relative-draft";
        vm.Aria2ExecutablePath = string.Empty;
        vm.XgetInstance = string.Empty;

        await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");

        Assert.Equal(committed with { Theme = ThemeMode.Dark, Language = "en-US" }, preferences.Current);
        Assert.Equal("unfinished draft", vm.ProxyHost);
        Assert.Equal("invalid port", vm.ProxyPort);
        Assert.Equal("unfinished-password", vm.ProxyPassword);
        Assert.Equal("relative-draft", vm.DownloadCacheDirectory);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        var restarted = CreatePreferences(directory);
        await restarted.LoadAsync();
        Assert.Equal(preferences.Current, restarted.Current);
        var json = await File.ReadAllTextAsync(directory.Combine("settings.json"));
        Assert.DoesNotContain(committed.ProxyPassword!, json, StringComparison.Ordinal);
        Assert.DoesNotContain("unfinished-password", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-hidden-draft")]
    public async Task Save_IgnoresHiddenDraftsAndPreservesTheUsersProxy(string hiddenDraft)
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(LegacyPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        vm.Aria2ExecutablePath = hiddenDraft;
        vm.XgetInstance = hiddenDraft;
        // Another consumer may have refreshed hidden migration values after initialization.
        await preferences.SaveAsync(preferences.Current with
        {
            Aria2ExecutablePath = directory.Combine("updated-legacy-aria2.exe"),
            XgetInstance = "https://updated-legacy.example/",
        });
        var committed = preferences.Current;

        await vm.ApplyAsync(ThemeMode.Light, "en-US");

        Assert.Equal(committed with { Theme = ThemeMode.Light, Language = "en-US" }, preferences.Current);
        Assert.Equal(committed.Aria2ExecutablePath, vm.Aria2ExecutablePath);
        Assert.Equal(committed.XgetInstance, vm.XgetInstance);
        Assert.Equal(committed.ProxyHost, vm.ProxyHost);
        Assert.Equal(committed.ProxyPassword, vm.ProxyPassword);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        var restarted = CreatePreferences(directory);
        await restarted.LoadAsync();
        Assert.Equal(preferences.Current, restarted.Current);
        Assert.DoesNotContain(committed.ProxyPassword!, await File.ReadAllTextAsync(directory.Combine("settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_BeforeInitializationCannotOverwriteCommittedPreferencesWithDefaults()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(LegacyPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        var committed = preferences.Current;
        var originalJson = await File.ReadAllTextAsync(directory.Combine("settings.json"));

        await vm.ApplyAsync(ThemeMode.Dark, "en-US");
        await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");
        await vm.ApplyPathsAsync();
        await vm.ApplyNetworkAsync();

        Assert.False(vm.CanApply);
        Assert.Equal(committed, preferences.Current);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(directory.Combine("settings.json")));
    }

    [Fact]
    public async Task InvalidNetworkSave_PreservesProxyAndLegacyDraftsWithoutChangingPreferences()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        await preferences.SaveAsync(LegacyPreferences(directory));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        var committed = preferences.Current;
        vm.ProxyPort = "not-a-port";
        vm.ProxyPassword = string.Empty;
        vm.Aria2ExecutablePath = string.Empty;
        vm.XgetInstance = string.Empty;

        await vm.ApplyAsync(ThemeMode.Dark, "en-US");

        Assert.Equal(committed, preferences.Current);
        Assert.Equal(vm.Text["SettingsProxyInvalid"], vm.ErrorMessage);
        Assert.Equal("not-a-port", vm.ProxyPort);
        Assert.Equal(string.Empty, vm.ProxyPassword);
        Assert.Equal(string.Empty, vm.Aria2ExecutablePath);
        Assert.Equal(string.Empty, vm.XgetInstance);
    }

    [Fact]
    public async Task Accelerator_RemainsOffByDefaultAndIsOnlySavedWhenSelected()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory);
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();

        Assert.False(vm.UseAccelerator);
        Assert.False(preferences.Current.UseAccelerator);
        await vm.ApplyAsync(ThemeMode.System, "zh-CN");
        Assert.False(preferences.Current.UseAccelerator);

        vm.UseAccelerator = true;
        await vm.ApplyAsync(ThemeMode.System, "zh-CN");
        Assert.True(preferences.Current.UseAccelerator);
    }

    [Fact]
    public async Task ProbeStatus_FollowsLanguageChanges()
    {
        using var directory = new TempDirectory();
        var text = new LocalizationService("zh-CN");
        using var vm = new SettingsViewModel(CreatePreferences(directory), text);
        await vm.InitializeAsync();
        text.ApplyLanguage("zh-CN");
        await vm.ProbeDownloadEngineAsync(_ => Task.FromResult(true));
        Assert.Equal("内置 aria2 已就绪", vm.Aria2Status);

        text.ApplyLanguage("en-US");

        Assert.Equal("Integrated aria2 is ready", vm.Aria2Status);
    }

    [Theory]
    [InlineData("SettingsIntegratedToolsHint")]
    [InlineData("SettingsAcceleratorHint")]
    [InlineData("DownloadTest")]
    [InlineData("DownloadTesting")]
    [InlineData("DownloadAria2Found")]
    [InlineData("DownloadAria2Missing")]
    [InlineData("DownloadAria2NotConfigured")]
    [InlineData("DownloadComponentsInitializing")]
    [InlineData("DownloadComponentCheckCancelled")]
    [InlineData("DownloadComponentCheckFailed")]
    [InlineData("EngineAria2Unavailable")]
    public void IntegratedComponentMessages_HavePairedChineseAndEnglishTranslations(string key)
    {
        var chinese = new LocalizationService("zh-CN")[key];
        var english = new LocalizationService("en-US")[key];

        Assert.NotEqual(key, chinese);
        Assert.NotEqual(key, english);
        Assert.NotEqual(chinese, english);
        Assert.DoesNotContain("exe", chinese, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executable path", english, StringComparison.OrdinalIgnoreCase);
    }

    private static PreferencesService CreatePreferences(TempDirectory directory) =>
        new(directory.Combine("settings.json"), new TestSecretProtector());

    private static AppPreferences LegacyPreferences(TempDirectory directory) => new()
    {
        Language = "zh-CN",
        DownloadCacheDirectory = directory.Combine("cache"),
        InstallRootDirectory = directory.Combine("apps"),
        DownloadEngine = DownloadEngine.Aria2,
        Aria2ExecutablePath = directory.Combine("legacy-aria2.exe"),
        XgetInstance = "https://legacy-xget.example/",
        ProxyKind = ProxyKind.Http,
        ProxyHost = "localhost",
        ProxyPort = 8080,
        ProxyUserName = "proxy-user",
        ProxyPassword = "saved-proxy-secret",
    };

    // A deterministic test adapter avoids Windows DPAPI and never starts a real process.
    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[5..]));
    }
}
