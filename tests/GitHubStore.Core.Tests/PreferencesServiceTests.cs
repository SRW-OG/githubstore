using GitHubStore.App.Services.Preferences;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class PreferencesServiceTests
{
    [Fact]
    public async Task Load_OnMissingFile_ReturnsDefaultsWithoutError()
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"));

        await service.LoadAsync();

        Assert.Equal(ThemeMode.System, service.Current.Theme);
        Assert.Equal("system", service.Current.Language);
        Assert.Null(service.Error);
    }

    [Theory]
    [InlineData(ThemeMode.Light, "zh-CN")]
    [InlineData(ThemeMode.Dark, "en-US")]
    [InlineData(ThemeMode.System, "system")]
    public async Task Save_ThenLoad_RoundTripsThemeAndLanguage(ThemeMode theme, string language)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await service.SaveAsync(new AppPreferences { Theme = theme, Language = language });
        var reopened = new PreferencesService(path);
        await reopened.LoadAsync();

        Assert.Equal(theme, reopened.Current.Theme);
        Assert.Equal(language, reopened.Current.Language);
        Assert.Null(reopened.Error);
    }

    [Fact]
    public async Task Save_NormalizesLanguageCaseForStableStorage()
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"));

        await service.SaveAsync(new AppPreferences { Theme = ThemeMode.Light, Language = "EN-us" });

        Assert.Equal("en-US", service.Current.Language);
        Assert.Contains("\"en-US\"", await File.ReadAllTextAsync(directory.Combine("settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_RaisesChangedOnlyWhenTheValueActuallyChanges()
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"));
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark, Language = "zh-CN" });
        Assert.Equal(1, raised);

        await service.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark, Language = "zh-CN" });
        Assert.Equal(1, raised);

        await service.SaveAsync(new AppPreferences { Theme = ThemeMode.Light, Language = "zh-CN" });
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Load_OnCorruptJson_FallsBackToDefaultsAndPreservesTheOriginalFile()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string corrupt = "{ \"theme\": \"not-a-theme\", \"language\": \"zh-CN\" }";
        await File.WriteAllTextAsync(path, corrupt);
        var service = new PreferencesService(path);

        await service.LoadAsync();

        Assert.Equal(ThemeMode.System, service.Current.Theme);
        Assert.Equal("system", service.Current.Language);
        Assert.Equal("SettingsInvalidFile", service.Error);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_OnUnknownProperty_TreatsTheFileAsInvalid()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        await File.WriteAllTextAsync(path, "{ \"theme\": \"Dark\", \"language\": \"en-US\", \"unknown\": 1 }");
        var service = new PreferencesService(path);

        await service.LoadAsync();

        Assert.Equal("SettingsInvalidFile", service.Error);
        Assert.Equal(ThemeMode.System, service.Current.Theme);
    }

    [Fact]
    public async Task Save_ToUnwritableTarget_ThrowsAndLeavesCurrentUnchanged()
    {
        using var directory = new TempDirectory();
        // A directory occupying the file path makes the atomic replace fail.
        var path = directory.Combine("settings.json");
        Directory.CreateDirectory(path);
        var failing = new PreferencesService(path);

        var error = await Record.ExceptionAsync(
            () => failing.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark, Language = "en-US" }));

        // Windows reports a path/shape conflict as either kind depending on the volume.
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected exception: {error}");
        Assert.Equal(ThemeMode.System, failing.Current.Theme);
        Assert.Equal("system", failing.Current.Language);
        Assert.Equal("SettingsSaveFailed", failing.Error);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData("zh")]
    public async Task Save_RejectsUnsupportedLanguageWithoutTouchingTheFile(string language)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(new AppPreferences { Theme = ThemeMode.Light, Language = language }));

        Assert.False(File.Exists(path));
        Assert.Equal("system", service.Current.Language);
    }

    [Fact]
    public async Task Save_RejectsAnUndefinedThemeValue()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(new AppPreferences { Theme = (ThemeMode)99, Language = "zh-CN" }));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Save_DoesNotLeaveTemporaryFilesBehind()
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"));

        await service.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark, Language = "en-US" });

        var leftovers = Directory.GetFiles(directory.DirectoryPath);
        Assert.Equal(directory.Combine("settings.json"), Assert.Single(leftovers));
    }

    [Fact]
    public async Task LegacySettings_UseAiDefaultsWithoutRewriting()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string original = "{\"theme\":\"Dark\",\"language\":\"en-US\"}";
        await File.WriteAllTextAsync(path, original);
        var service = new PreferencesService(path);
        await service.LoadAsync();
        Assert.Null(service.Error);
        Assert.Equal("https://api.openai.com/v1", service.Current.AiEndpoint);
        Assert.Equal("gpt-4o-mini", service.Current.AiModel);
        Assert.Null(service.Current.AiApiKey);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("https://example.test/v1", "https://example.test/v1")]
    [InlineData(" https://example.test/v1/chat/completions/ ", "https://example.test/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1")]
    [InlineData("http://[::1]:1234/v1", "http://[::1]:1234/v1")]
    public async Task AiEndpoint_AcceptsHttpsOrLocalLoopbackAndPreservesFullPath(string endpoint, string expected)
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"));
        await service.SaveAsync(new AppPreferences { AiEndpoint = endpoint, AiModel = " local/model " });
        Assert.Equal(expected, service.Current.AiEndpoint);
        Assert.Equal("local/model", service.Current.AiModel);
        Assert.Null(service.Current.AiApiKey);
        var reopened = new PreferencesService(directory.Combine("settings.json"));
        await reopened.LoadAsync();
        Assert.Equal(service.Current, reopened.Current);
    }

    [Theory]
    [InlineData("http://example.test/v1")]
    [InlineData("http://192.168.1.1/v1")]
    [InlineData("http://localhost.example.test/v1")]
    [InlineData("https://user@test-only.example/v1")]
    [InlineData("https://@example.test/v1")]
    [InlineData("https://example.test/v1?key=secret")]
    [InlineData("https://example.test/v1?")]
    [InlineData("https://example.test/v1#")]
    [InlineData("https://example.test/v1#fragment")]
    [InlineData("file:///C:/models")]
    [InlineData("relative/v1")]
    [InlineData("https://example.test/v1\n")]
    [InlineData("")]
    public async Task AiEndpoint_InvalidInputDoesNotPublishSettings(string endpoint)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);
        await service.SaveAsync(new AppPreferences());
        var before = await File.ReadAllTextAsync(path);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(service.Current with { AiEndpoint = endpoint }));
        Assert.Equal(nameof(AppPreferences.AiEndpoint), exception.ParamName);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal("https://api.openai.com/v1", service.Current.AiEndpoint);
    }

    [Fact]
    public async Task AiModel_RejectsEmptyOversizedOrControlCharacters()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);
        foreach (var model in new[] { "", "   ", new string('x', 201), "model\nvalue" })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences { AiModel = model }));
            Assert.Equal(nameof(AppPreferences.AiModel), exception.ParamName);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences { AiEndpoint = "https://example.test/" + new string('x', 2048) }));
        Assert.False(File.Exists(path));
    }

    // ── 目录设置 ─────────────────────────────────────────────

    [Fact]
    public void NewInstallAndCachePaths_DefaultToNull()
    {
        var preferences = new AppPreferences();

        Assert.Null(preferences.DownloadCacheDirectory);
        Assert.Null(preferences.InstallRootDirectory);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsAbsoluteDirectories()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var installRoot = Path.Combine(directory.DirectoryPath, "apps");
        var cacheRoot = Path.Combine(directory.DirectoryPath, "cache");
        var service = new PreferencesService(path);

        await service.SaveAsync(new AppPreferences
        {
            Theme = ThemeMode.Dark,
            Language = "en-US",
            InstallRootDirectory = installRoot,
            DownloadCacheDirectory = cacheRoot,
        });
        var reopened = new PreferencesService(path);
        await reopened.LoadAsync();

        Assert.Equal(installRoot, reopened.Current.InstallRootDirectory);
        Assert.Equal(cacheRoot, reopened.Current.DownloadCacheDirectory);
    }

    [Theory]
    [InlineData("relative\\apps")]
    [InlineData("apps")]
    [InlineData(".\\apps")]
    public async Task Save_RejectsRelativeInstallRootWithoutWritingTheFile(string relative)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences
        {
            InstallRootDirectory = relative,
        }));

        Assert.False(File.Exists(path));
        Assert.Null(service.Current.InstallRootDirectory);
    }

    [Fact]
    public async Task Save_RejectsRelativeCacheDirectoryWithoutWritingTheFile()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences
        {
            DownloadCacheDirectory = "cache",
        }));

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Save_NormalizesBlankDirectoriesToNull(string? configured)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await service.SaveAsync(new AppPreferences
        {
            InstallRootDirectory = configured,
            DownloadCacheDirectory = configured,
        });

        Assert.Null(service.Current.InstallRootDirectory);
        Assert.Null(service.Current.DownloadCacheDirectory);
        // 显式表达“使用默认值”，不能把空串写进文件。
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_TrimsSurroundingWhitespaceFromDirectories()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var target = Path.Combine(directory.DirectoryPath, "apps");
        var service = new PreferencesService(path);

        await service.SaveAsync(new AppPreferences { InstallRootDirectory = $"  {target}  " });

        Assert.Equal(target, service.Current.InstallRootDirectory);
    }

    [Fact]
    public async Task Save_NormalizesTrailingSeparatorOffDirectory()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var target = Path.Combine(directory.DirectoryPath, "apps");
        var service = new PreferencesService(path);

        await service.SaveAsync(new AppPreferences { InstallRootDirectory = target + Path.DirectorySeparatorChar });

        // 去掉结尾分隔符，避免同一目录出现两种形式导致比较不等。
        Assert.Equal(target, service.Current.InstallRootDirectory);
    }

    [Fact]
    public async Task Save_RejectsRelativePathButKeepsThePreviousSuccessfulValues()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var validRoot = Path.Combine(directory.DirectoryPath, "apps");
        var service = new PreferencesService(path);
        await service.SaveAsync(new AppPreferences { InstallRootDirectory = validRoot });

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences
        {
            InstallRootDirectory = "relative",
        }));

        // 校验失败必须发生在写盘与改 Current 之前，否则用户会丢失已保存的值。
        Assert.Equal(validRoot, service.Current.InstallRootDirectory);
    }
}
