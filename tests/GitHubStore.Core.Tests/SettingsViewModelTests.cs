using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Initialize_AdoptsPersistedValuesAndAppliesTheSavedLanguage()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark, Language = "en-US" });
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsInitialized);
        Assert.Equal(ThemeMode.Dark, viewModel.SelectedTheme);
        Assert.Equal("en-US", viewModel.SelectedLanguage);
        Assert.True(viewModel.Text.IsEnglish);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Initialize_IsIdempotentAndSafeToCallConcurrently()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));

        await Task.WhenAll(viewModel.InitializeAsync(), viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsInitialized);
        Assert.Equal(ThemeMode.System, viewModel.SelectedTheme);
    }

    [Fact]
    public async Task Initialize_SurfacesACorruptSettingsFileWithoutOverwritingIt()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string corrupt = "not json at all";
        await File.WriteAllTextAsync(path, corrupt);
        var preferences = CreatePreferences(path);
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));

        await viewModel.InitializeAsync();

        Assert.False(string.IsNullOrEmpty(viewModel.ErrorMessage));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Apply_PersistsSelectionAndUpdatesTheLiveLanguage()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();

        await viewModel.ApplyAsync(ThemeMode.Light, "en-US");

        Assert.Equal(ThemeMode.Light, preferences.Current.Theme);
        Assert.Equal("en-US", preferences.Current.Language);
        Assert.Equal(ThemeMode.Light, viewModel.SelectedTheme);
        Assert.True(viewModel.Text.IsEnglish);
        Assert.False(string.IsNullOrEmpty(viewModel.StatusMessage));
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Apply_WithUnsupportedLanguage_ReportsAnErrorAndKeepsExistingSettings()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { Theme = ThemeMode.System, Language = "en-US" });
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await viewModel.InitializeAsync();
        viewModel.SelectedTheme = ThemeMode.Dark;
        viewModel.SelectedLanguage = "de-DE";

        await viewModel.ApplyAsync(ThemeMode.Dark, "de-DE");

        Assert.False(string.IsNullOrEmpty(viewModel.ErrorMessage));
        Assert.Equal(ThemeMode.System, preferences.Current.Theme);
        Assert.Equal("en-US", preferences.Current.Language);
        Assert.Equal(ThemeMode.Dark, viewModel.SelectedTheme);
        Assert.Equal("de-DE", viewModel.SelectedLanguage);
    }

    [Fact]
    public async Task Apply_WithUnwritableTarget_ReportsFailureAndKeepsCurrentSettings()
    {
        using var directory = new TempDirectory();
        var blockedPath = directory.Combine("blocked.json");
        Directory.CreateDirectory(blockedPath);
        var preferences = CreatePreferences(blockedPath);
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedTheme = ThemeMode.Dark;

        await viewModel.ApplyAsync(ThemeMode.Dark, "zh-CN");

        Assert.False(string.IsNullOrEmpty(viewModel.ErrorMessage));
        Assert.Equal(ThemeMode.System, preferences.Current.Theme);
        Assert.Equal(ThemeMode.Dark, viewModel.SelectedTheme);
    }

    [Fact]
    public async Task Apply_BroadcastsLocalizedMessagesWhenTheLanguageChanges()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        using var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is "ErrorMessage" or "StatusMessage") notified.Add(args.PropertyName);
        };

        await viewModel.ApplyAsync(ThemeMode.System, "en-US");

        Assert.Contains("StatusMessage", notified);
        // The same status key now renders through the English table.
        Assert.StartsWith("Settings saved", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_ThenInitialize_ThrowsObjectDisposed()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        var viewModel = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        viewModel.Dispose();
        viewModel.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync());
    }

    [Fact]
    public async Task AiDraft_IsNotSubmittedOrLostByOtherSaveSections()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { AiApiKey = "saved-test-key" });
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        vm.AiEndpoint = "not a valid endpoint";
        vm.AiModel = "";
        vm.AiApiKey = "unsaved-test-key";
        await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");
        await vm.ApplyNetworkAsync();
        await vm.ApplyPathsAsync();
        await vm.ApplyAsync(ThemeMode.Light, "en-US");
        Assert.Equal("https://api.openai.com/v1", preferences.Current.AiEndpoint);
        Assert.Equal("gpt-4o-mini", preferences.Current.AiModel);
        Assert.Equal("saved-test-key", preferences.Current.AiApiKey);
        Assert.Equal("not a valid endpoint", vm.AiEndpoint);
        Assert.Equal("", vm.AiModel);
        Assert.Equal("unsaved-test-key", vm.AiApiKey);
        Assert.Equal("", vm.ErrorMessage);
    }

    [Fact]
    public async Task AiSave_OnlyCommitsAiFieldsAndKeepsOtherDrafts()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var preferences = CreatePreferences(path);
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        vm.ProxyKind = GitHubStore.Core.Abstractions.ProxyKind.Http;
        vm.ProxyHost = "invalid/network/draft";
        vm.InstallRootDirectory = "relative-path";
        vm.SelectedTheme = ThemeMode.Dark;
        vm.AiEndpoint = "http://localhost:11434/v1/chat/completions";
        vm.AiModel = " local-model ";
        vm.AiApiKey = "test-only-key";
        await vm.ApplyAiAsync();
        Assert.Equal("", vm.ErrorMessage);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.Equal("local-model", preferences.Current.AiModel);
        Assert.Equal("test-only-key", preferences.Current.AiApiKey);
        Assert.Equal(ThemeMode.System, preferences.Current.Theme);
        Assert.Null(preferences.Current.InstallRootDirectory);
        Assert.Equal(GitHubStore.Core.Abstractions.ProxyKind.None, preferences.Current.ProxyKind);
        Assert.Equal("relative-path", vm.InstallRootDirectory);
        Assert.Equal("invalid/network/draft", vm.ProxyHost);
        var reopened = CreatePreferences(path);
        await reopened.LoadAsync();
        Assert.Equal(preferences.Current, reopened.Current);
    }

    [Fact]
    public async Task AiSave_InvalidEndpointOrModelShowsSpecificErrorRetainsDraftAndAllowsRetry()
    {
        using var directory = new TempDirectory();
        var preferences = CreatePreferences(directory.Combine("settings.json"));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        vm.AiEndpoint = "http://example.test/v1?token=test-only-value";
        vm.AiApiKey = "test-draft-key";
        await vm.ApplyAiAsync();
        Assert.Equal(vm.Text["SettingsAiEndpointInvalid"], vm.ErrorMessage);
        Assert.DoesNotContain("test-only-value", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("test-draft-key", vm.AiApiKey);
        Assert.False(vm.IsBusy);
        vm.AiEndpoint = "http://localhost:11434/v1";
        vm.AiModel = "";
        await vm.ApplyAiAsync();
        Assert.Equal(vm.Text["SettingsAiModelInvalid"], vm.ErrorMessage);
        vm.AiModel = "local-model";
        vm.AiApiKey = "";
        await vm.ApplyAiAsync();
        Assert.Equal("", vm.ErrorMessage);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.Null(preferences.Current.AiApiKey);
    }

    [Fact]
    public async Task AiDecryptionFailure_BlocksEverySettingsSectionAndKeepsFile()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string original = "{\"protectedAiApiKey\":\"bad-key\"}";
        await File.WriteAllTextAsync(path, original);
        var preferences = CreatePreferences(path);
        using var vm = new SettingsViewModel(preferences, new LocalizationService("en-US"));
        await vm.InitializeAsync();
        await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");
        Assert.Equal(vm.Text["SettingsSecretDecryptionFailed"], vm.ErrorMessage);
        await vm.ApplyNetworkAsync();
        Assert.Equal(vm.Text["SettingsSecretDecryptionFailed"], vm.ErrorMessage);
        await vm.ApplyAiAsync();
        Assert.Equal(vm.Text["SettingsSecretDecryptionFailed"], vm.ErrorMessage);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("bad-key", vm.ErrorMessage, StringComparison.Ordinal);
    }

    private static PreferencesService CreatePreferences(string path) => new(path, new FakeProtector());

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) =>
            "test:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[5..]));
    }
}