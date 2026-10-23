using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class PreferencesSecurityRegressionTests
{
    private const string Password = "private-proxy-password-中文";

    [Fact]
    public async Task Save_ProtectsPasswordAndRestartDecryptsWithInjectedProtector()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await service.SaveAsync(CustomProxy());

        var json = await File.ReadAllTextAsync(path);
        var stored = JsonNode.Parse(json)!.AsObject();
        Assert.False(stored.ContainsKey("proxyPassword"));
        Assert.False(string.IsNullOrEmpty(stored["protectedProxyPassword"]!.GetValue<string>()));
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        Assert.Equal(Password, service.Current.ProxyPassword);
        Assert.Equal(Password, service.Current.ToProxySettings().Password);

        var restarted = new PreferencesService(path, new FakeProtector());
        await restarted.LoadAsync();
        Assert.Null(restarted.Error);
        Assert.Equal(Password, restarted.Current.ProxyPassword);
        Assert.Equal(service.Current, restarted.Current);
    }

    [Fact]
    public async Task LegacyPlaintext_LoadDoesNotRewrite_AndNextSuccessfulSaveMigrates()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string legacy = """
            { "proxyKind": 1, "proxyHost": "localhost", "proxyPort": 8080,
              "proxyPassword": "legacy-plaintext", "language": "en-US" }
            """;
        await File.WriteAllTextAsync(path, legacy);
        var service = new PreferencesService(path, new FakeProtector());
        await service.LoadAsync();

        Assert.Null(service.Error);
        Assert.Equal("legacy-plaintext", service.Current.ProxyPassword);
        Assert.Equal(legacy, await File.ReadAllTextAsync(path));
        await service.SaveAsync(service.Current with { Theme = ThemeMode.Dark });

        var migrated = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("legacy-plaintext", migrated, StringComparison.Ordinal);
        Assert.False(JsonNode.Parse(migrated)!.AsObject().ContainsKey("proxyPassword"));
        var restarted = new PreferencesService(path, new FakeProtector());
        await restarted.LoadAsync();
        Assert.Null(restarted.Error);
        Assert.Equal("legacy-plaintext", restarted.Current.ProxyPassword);
    }

    [Fact]
    public async Task LegacyMigration_ProtectionFailurePreservesPlaintextFileAndCurrent()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string legacy = """
            { "proxyKind": 1, "proxyHost": "localhost", "proxyPort": 8080, "proxyPassword": "legacy" }
            """;
        await File.WriteAllTextAsync(path, legacy);
        var service = new PreferencesService(path, new FakeProtector { FailProtect = true });
        await service.LoadAsync();
        var previous = service.Current;

        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(previous with { Theme = ThemeMode.Dark }));

        Assert.Equal("SettingsSecretProtectionFailed", service.Error);
        Assert.Equal(previous, service.Current);
        Assert.Equal(legacy, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
    }

    [Theory]
    [InlineData("\"broken-ciphertext\"")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    public async Task CorruptProtectedPassword_LoadReportsErrorAndSaveCannotOverwrite(string protectedJson)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var original = "{\"proxyKind\":1,\"proxyHost\":\"localhost\",\"proxyPort\":8080,\"protectedProxyPassword\":" + protectedJson + "}";
        await File.WriteAllTextAsync(path, original);
        var service = new PreferencesService(path, new FakeProtector());
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.LoadAsync();
        Assert.Equal("SettingsSecretDecryptionFailed", service.Error);
        Assert.NotNull(service.ErrorDetail);
        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark }));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(ThemeMode.System, service.Current.Theme);
        Assert.Equal(0, raised);
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
    }

    [Fact]
    public async Task MixedLegacyAndProtectedPassword_IsRejectedWithoutPlaintextFallback()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string original = """
            { "proxyKind": 1, "proxyHost": "localhost", "proxyPort": 8080,
              "proxyPassword": "legacy", "protectedProxyPassword": "broken" }
            """;
        await File.WriteAllTextAsync(path, original);
        var service = new PreferencesService(path, new FakeProtector());

        await service.LoadAsync();

        Assert.Equal("SettingsSecretDecryptionFailed", service.Error);
        Assert.Null(service.Current.ProxyPassword);
        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(new AppPreferences()));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveWithoutLoad_DoesNotOverwriteExistingUnreadableSecret()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        const string original = "{\"protectedProxyPassword\":\"broken\"}";
        await File.WriteAllTextAsync(path, original);
        var service = new PreferencesService(path, new FakeProtector());

        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(new AppPreferences()));

        Assert.Equal("SettingsSecretDecryptionFailed", service.Error);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DecryptionFailure_PreservesLastKnownCurrent_AndSuccessfulReloadUnblocksSave()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var protector = new FakeProtector();
        var service = new PreferencesService(path, protector);
        await service.SaveAsync(CustomProxy());
        var original = await File.ReadAllTextAsync(path);
        var previous = service.Current;
        protector.FailUnprotect = true;

        await service.LoadAsync();
        Assert.Equal("SettingsSecretDecryptionFailed", service.Error);
        Assert.Equal(previous, service.Current);
        Assert.Equal(original, await File.ReadAllTextAsync(path));

        protector.FailUnprotect = false;
        await service.LoadAsync();
        await service.SaveAsync(service.Current with { Theme = ThemeMode.Dark });
        Assert.Null(service.Error);
        Assert.Equal(ThemeMode.Dark, service.Current.Theme);
    }

    [Fact]
    public async Task FailedProtection_PreservesCommittedFileCurrentAndChangedCount()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var protector = new FakeProtector();
        var service = new PreferencesService(path, protector);
        await service.SaveAsync(CustomProxy());
        var original = await File.ReadAllTextAsync(path);
        var previous = service.Current;
        var raised = 0;
        service.Changed += (_, _) => raised++;
        protector.FailProtect = true;

        var error = await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(previous with { ProxyPassword = "replacement" }));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(previous, service.Current);
        Assert.Equal("SettingsSecretProtectionFailed", service.Error);
        Assert.DoesNotContain("replacement", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement", service.ErrorDetail!, StringComparison.Ordinal);
        Assert.Equal(0, raised);
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
    }

    [Fact]
    public async Task PublishFailure_PreservesCommittedFileAndCurrent()
    {
        // On Windows a reader without FileShare.Delete prevents atomic replacement.
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await service.SaveAsync(CustomProxy());
        var original = await File.ReadAllTextAsync(path);
        var previous = service.Current;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => service.SaveAsync(previous with { Theme = ThemeMode.Dark }));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected exception: {error}");
        }

        Assert.Equal("SettingsSaveFailed", service.Error);
        Assert.Equal(previous, service.Current);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
    }

    [Theory]
    [InlineData(ProxyKind.None, Password)]
    [InlineData(ProxyKind.Http, null)]
    [InlineData(ProxyKind.Http, "")]
    public async Task NoCustomPassword_DoesNotInvokeProtector(ProxyKind kind, string? password)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var protector = new FakeProtector { FailProtect = true, FailUnprotect = true };
        var service = new PreferencesService(path, protector);

        await service.SaveAsync(CustomProxy() with { ProxyKind = kind, ProxyPassword = password });
        var restarted = new PreferencesService(path, protector);
        await restarted.LoadAsync();

        Assert.Null(restarted.Error);
        Assert.Null(restarted.Current.ProxyPassword);
        Assert.False(JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject().ContainsKey("protectedProxyPassword"));
        if (kind == ProxyKind.None)
        {
            Assert.Null(restarted.Current.ProxyHost);
            Assert.Null(restarted.Current.ProxyUserName);
            Assert.Equal(0, restarted.Current.ProxyPort);
            Assert.Equal(ProxyKind.None, restarted.Current.ToProxySettings().Kind);
        }
    }

    [Fact]
    public async Task NonWindows_DefaultProtectorExplicitlyFailsForPassword()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path);

        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(CustomProxy()));

        Assert.Equal("SettingsSecretProtectionFailed", service.Error);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:/", @"C:\")]
    [InlineData(@"\\server\share\", @"\\server\share\")]
    public async Task WindowsRoots_RetainAbsoluteRootSemantics(string configured, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"), new FakeProtector());
        await service.SaveAsync(new AppPreferences { InstallRootDirectory = configured, DownloadCacheDirectory = configured });

        Assert.Equal(expected, service.Current.InstallRootDirectory);
        Assert.Equal(expected, service.Current.DownloadCacheDirectory);
        Assert.True(Path.IsPathFullyQualified(service.Current.InstallRootDirectory!));
        Assert.Equal(expected, StorageLocations.ResolveInstallRoot(service.Current));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData("\t")]
    [InlineData(" \r\n ")]
    [InlineData("C:\\apps\u0000data")]
    [InlineData("C:\\apps\u001fdata")]
    [InlineData("C:\\apps\u007fdata")]
    public async Task InvalidDirectoryInput_IsRejectedWithoutChangingCommittedFile(string invalid)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await service.SaveAsync(new AppPreferences());
        var original = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(service.Current with { InstallRootDirectory = invalid }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(service.Current with { DownloadCacheDirectory = invalid }));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Null(service.Current.InstallRootDirectory);
        Assert.Null(service.Current.DownloadCacheDirectory);
    }

    [Fact]
    public async Task DirectoryConfiguration_DoesNotCreateConfiguredDirectories()
    {
        using var directory = new TempDirectory();
        var install = directory.Combine("uncreated-apps");
        var cache = directory.Combine("uncreated-cache");
        var service = new PreferencesService(directory.Combine("settings.json"), new FakeProtector());
        await service.SaveAsync(new AppPreferences { InstallRootDirectory = install, DownloadCacheDirectory = cache });
        Assert.False(Directory.Exists(install));
        Assert.False(Directory.Exists(cache));
    }

    [Theory]
    [InlineData("localhost", "localhost")]
    [InlineData("proxy.example.com", "proxy.example.com")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    public async Task ProxyHost_AcceptsOnlyHostValuesIncludingIpv6(string configured, string expected)
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"), new FakeProtector());
        await service.SaveAsync(CustomProxy() with { ProxyHost = configured, ProxyPassword = null });
        Assert.Equal(expected, service.Current.ProxyHost);
    }

    [Theory]
    [InlineData("https://proxy.example.com")]
    [InlineData("user:secret@proxy.example.com")]
    [InlineData("proxy.example.com/path")]
    [InlineData("proxy.example.com?secret=x")]
    [InlineData("proxy.example.com#fragment")]
    [InlineData("proxy.example.com:8080")]
    [InlineData("[::1]:8080")]
    [InlineData("proxy name")]
    [InlineData("proxy\\path")]
    [InlineData("\r\nlocalhost")]
    [InlineData("")]
    public async Task InvalidProxyHost_IsRejectedBeforeWriting(string host)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(CustomProxy() with { ProxyHost = host }));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task InvalidCustomProxyPort_IsRejectedBeforeWriting(int port)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(CustomProxy() with { ProxyPort = port }));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task CustomProxyPort_BoundariesAreAccepted(int port)
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"), new FakeProtector());
        await service.SaveAsync(CustomProxy() with { ProxyPort = port, ProxyPassword = null });
        Assert.Equal(port, service.Current.ProxyPort);
    }

    [Fact]
    public async Task EngineConfiguration_RoundTripsAbsoluteExeAndHttpsInstanceRoot()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var executable = directory.Combine("aria2c.exe");
        var service = new PreferencesService(path, new FakeProtector());
        await service.SaveAsync(new AppPreferences { Aria2ExecutablePath = executable, XgetInstance = "https://xget.example.com" });
        var restarted = new PreferencesService(path, new FakeProtector());
        await restarted.LoadAsync();

        Assert.Null(restarted.Error);
        Assert.Equal(executable, restarted.Current.Aria2ExecutablePath);
        Assert.Equal("https://xget.example.com/", restarted.Current.XgetInstance);
        Assert.False(File.Exists(executable));
    }

    [Theory]
    [InlineData("aria2c.exe")]
    [InlineData("relative/aria2c.exe")]
    [InlineData("\taria2c.exe")]
    public async Task InvalidExecutablePath_IsRejected(string executable)
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"), new FakeProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences { Aria2ExecutablePath = executable }));
    }

    [Fact]
    public async Task AbsoluteNonExePath_IsRejected()
    {
        using var directory = new TempDirectory();
        var service = new PreferencesService(directory.Combine("settings.json"), new FakeProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences { Aria2ExecutablePath = directory.Combine("aria2c.cmd") }));
    }

    [Theory]
    [InlineData("http://xget.example.com")]
    [InlineData("https://account@xget.example.com/")]
    [InlineData("https://@xget.example.com/")]
    [InlineData("https://xget.example.com/path/")]
    [InlineData("https://xget.example.com/?query=value")]
    [InlineData("https://xget.example.com/#fragment")]
    [InlineData("https://xget.example.com/?")]
    [InlineData("https://xget.example.com/#")]
    [InlineData("https://xget.example.com/\r\n")]
    [InlineData("relative")]
    public async Task InvalidXgetInstance_IsRejected(string instance)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new AppPreferences { XgetInstance = instance }));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AiKey_ProtectedRoundTripAndClearingNeverWritesPlaintext()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        const string key = "test-only-ai-key";
        await service.SaveAsync(new AppPreferences { AiApiKey = key });
        var stored = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(key, stored, StringComparison.Ordinal);
        Assert.False(JsonNode.Parse(stored)!.AsObject().ContainsKey("aiApiKey"));
        Assert.NotNull(JsonNode.Parse(stored)!["protectedAiApiKey"]);
        var reopened = new PreferencesService(path, new FakeProtector());
        await reopened.LoadAsync();
        Assert.Equal(key, reopened.Current.AiApiKey);
        await reopened.SaveAsync(reopened.Current with { AiApiKey = null });
        Assert.False(JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject().ContainsKey("protectedAiApiKey"));
    }

    [Theory]
    [InlineData("{\"protectedAiApiKey\":\"broken\"}")]
    [InlineData("{\"protectedAiApiKey\":null}")]
    [InlineData("{\"protectedAiApiKey\":123}")]
    [InlineData("{\"aiApiKey\":\"plaintext-secret\"}")]
    [InlineData("{\"protectedAiApiKey\":\"broken\",\"aiApiKey\":\"plaintext-secret\"}")]
    public async Task AiKey_InvalidCiphertextOrPlaintextBlocksAllSavesEvenWithoutLoad(string original)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        await File.WriteAllTextAsync(path, original);
        var service = new PreferencesService(path, new FakeProtector());
        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(new AppPreferences { Theme = ThemeMode.Dark }));
        await service.LoadAsync();
        Assert.Equal("SettingsSecretDecryptionFailed", service.Error);
        Assert.Null(service.Current.AiApiKey);
        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(new AppPreferences()));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("plaintext-secret", service.ErrorDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiKey_ProtectionFailureIsAtomicAndDoesNotLeakTheInput()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var protector = new FakeProtector();
        var service = new PreferencesService(path, protector);
        await service.SaveAsync(new AppPreferences());
        var before = await File.ReadAllTextAsync(path);
        protector.FailProtect = true;
        var exception = await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(service.Current with { AiApiKey = "sensitive-ai-input" }));
        Assert.DoesNotContain("sensitive-ai-input", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-ai-input", service.ErrorDetail!, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Null(service.Current.AiApiKey);
        Assert.Single(Directory.GetFiles(directory.DirectoryPath));
    }

    [Fact]
    public async Task AiKey_AndProxyPasswordRoundTripIndependently()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector());
        await service.SaveAsync(CustomProxy() with { AiApiKey = "test-ai-key" });
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.NotNull(stored["protectedAiApiKey"]);
        Assert.NotNull(stored["protectedProxyPassword"]);
        var reopened = new PreferencesService(path, new FakeProtector());
        await reopened.LoadAsync();
        Assert.Equal("test-ai-key", reopened.Current.AiApiKey);
        Assert.Equal(Password, reopened.Current.ProxyPassword);
        await reopened.SaveAsync(reopened.Current with { AiApiKey = null });
        var again = new PreferencesService(path, new FakeProtector());
        await again.LoadAsync();
        Assert.Null(again.Current.AiApiKey);
        Assert.Equal(Password, again.Current.ProxyPassword);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AiKey_EmptyDoesNotRequireCredentialProtection(string? key)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var service = new PreferencesService(path, new FakeProtector { FailProtect = true, FailUnprotect = true });
        await service.SaveAsync(new AppPreferences { AiEndpoint = "http://localhost:11434/v1", AiApiKey = key });
        Assert.Null(service.Current.AiApiKey);
        Assert.False(JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject().ContainsKey("protectedAiApiKey"));
    }

    [Fact]
    public async Task AiKey_FailedReloadPreservesKnownValuesAndSuccessfulReloadUnblocksSave()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("settings.json");
        var protector = new FakeProtector();
        var service = new PreferencesService(path, protector);
        await service.SaveAsync(new AppPreferences { AiApiKey = "test-only-key" });
        var previous = service.Current;
        protector.FailUnprotect = true;
        await service.LoadAsync();
        Assert.Equal(previous, service.Current);
        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(previous with { Theme = ThemeMode.Dark }));
        protector.FailUnprotect = false;
        await service.LoadAsync();
        await service.SaveAsync(service.Current with { Theme = ThemeMode.Dark });
        Assert.Null(service.Error);
        Assert.Equal("test-only-key", service.Current.AiApiKey);
    }

    private static AppPreferences CustomProxy() => new()
    {
        ProxyKind = ProxyKind.Http,
        ProxyHost = "localhost",
        ProxyPort = 8080,
        ProxyUserName = "proxy-user",
        ProxyPassword = Password,
    };

    // Test-only reversible encoding, deliberately distinct from the production protector.
    private sealed class FakeProtector : ISecretProtector
    {
        private const string Prefix = "fake:v1:";
        public bool FailProtect { get; set; }
        public bool FailUnprotect { get; set; }

        public string Protect(string plaintext)
        {
            if (FailProtect) throw new CryptographicException("Input: " + plaintext);
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext).Select(value => (byte)(value ^ 0xa5)).ToArray());
        }

        public string Unprotect(string protectedValue)
        {
            if (FailUnprotect || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
                throw new CryptographicException("Invalid fake credential.");
            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]).Select(value => (byte)(value ^ 0xa5)).ToArray());
        }
    }
}
