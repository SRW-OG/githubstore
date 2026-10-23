using System.Net;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class RuntimeConfigurationRegressionTests
{
    [Fact]
    public void HttpHandler_UsesExplicitProxyAndCredentialsWithoutEmbeddingUserInfo()
    {
        using var handler = ConfiguredDownloadService.CreateHandler(new AppPreferences
        {
            ProxyKind = ProxyKind.Http, ProxyHost = "127.0.0.1", ProxyPort = 18888,
            ProxyUserName = "fixture-user", ProxyPassword = "fixture-secret",
        });
        Assert.True(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("127.0.0.1", proxy.Address!.Host);
        Assert.Equal(18888, proxy.Address.Port);
        Assert.Empty(proxy.Address.UserInfo);
        var credential = proxy.Credentials!.GetCredential(proxy.Address, "Basic");
        Assert.Equal("fixture-user", credential!.UserName);
    }

    [Fact]
    public void HttpHandler_SystemProxyAndIpv6AreUnambiguous()
    {
        using var system = ConfiguredDownloadService.CreateHandler(new AppPreferences());
        Assert.True(system.UseProxy);
        Assert.Null(system.Proxy);
        var proxy = new ProxySettings { Kind = ProxyKind.Socks5, Host = "::1", Port = 1080 };
        Assert.Equal("socks5://[::1]:1080", proxy.ToProxyUri());
        Assert.Equal("system", new ProxySettings().Describe());
    }

    [Fact]
    public async Task ConfiguredDownload_CapturesSavedSettingsForTheWholeOperation()
    {
        using var temp = new TempDirectory();
        var preferences = new PreferencesService(Path.Combine(temp.DirectoryPath, "settings.json"));
        await preferences.SaveAsync(new AppPreferences { ProxyKind = ProxyKind.Http, ProxyHost = "127.0.0.1", ProxyPort = 1081 });
        AppPreferences? captured = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ConfiguredDownloadService(preferences, snapshot =>
        {
            captured = snapshot;
            return new HttpClient(new FixtureHandler(async () =>
            {
                entered.SetResult();
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }));
        });
        var operation = service.DownloadAsync(new DownloadRequest
        {
            Source = new Uri("https://example.invalid/fixture.bin"),
            TargetPath = Path.Combine(temp.DirectoryPath, "fixture.bin"), ExpectedSize = 3,
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await preferences.SaveAsync(preferences.Current with { ProxyPort = 1082 });
        release.SetResult();
        var result = await operation;
        Assert.True(result.Succeeded);
        Assert.Equal(1081, captured!.ProxyPort);
        Assert.Equal(1082, preferences.Current.ProxyPort);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.OutputPath!));
    }

    [Fact]
    public async Task AppearanceChanges_DoNotCommitUnfinishedNetworkOrPathDrafts()
    {
        using var temp = new TempDirectory();
        var preferences = new PreferencesService(Path.Combine(temp.DirectoryPath, "settings.json"));
        using var vm = new SettingsViewModel(preferences, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        vm.ProxyKind = ProxyKind.Http;
        vm.ProxyPort = "unfinished";
        vm.DownloadCacheDirectory = "relative-draft";
        await vm.ApplyAppearanceAsync(ThemeMode.Dark, "en-US");
        Assert.Equal(ThemeMode.Dark, preferences.Current.Theme);
        Assert.Equal("en-US", preferences.Current.Language);
        Assert.Equal(ProxyKind.None, preferences.Current.ProxyKind);
        Assert.Null(preferences.Current.DownloadCacheDirectory);
        Assert.Equal("unfinished", vm.ProxyPort);
        Assert.Equal("relative-draft", vm.DownloadCacheDirectory);
    }

    [Fact]
    public async Task DownloadsViewModel_UsesCacheDirectoryAndAssetVerificationMetadata()
    {
        using var temp = new TempDirectory();
        var preferences = new PreferencesService(Path.Combine(temp.DirectoryPath, "settings.json"));
        await preferences.SaveAsync(new AppPreferences { DownloadCacheDirectory = temp.DirectoryPath });
        var fake = new FixtureDownload();
        using var vm = new DownloadsViewModel(fake, preferences, new LocalizationService("zh-CN"));
        await vm.DownloadAsync(new Asset
        {
            InstallerId = "fixture", ReleaseKey = "stable:1", Name = "setup.exe", Size = 3,
            DownloadUrl = "https://example.invalid/setup.exe", Digest = "sha256:" + new string('a', 64),
        });
        Assert.NotNull(fake.Last);
        Assert.StartsWith(temp.DirectoryPath, fake.Last!.TargetPath);
        Assert.Equal("setup.exe", Path.GetFileName(fake.Last.TargetPath));
        Assert.Equal(3, fake.Last.ExpectedSize);
        Assert.Equal("sha256:" + new string('a', 64), fake.Last.ExpectedSha256);
        Assert.False(vm.IsDownloading);
        Assert.Equal(1, fake.Calls);
        // 上游摘要与替身写出的字节不一致，因此不得写入可复用的缓存收据。
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fake.Last!.TargetPath)!, ".cache-receipt-*.json"));
    }

    [Fact]
    public async Task DownloadsViewModel_RetainsVerifiedPackageAndReusesItWithoutDownloadingAgain()
    {
        using var temp = new TempDirectory();
        var preferences = new PreferencesService(Path.Combine(temp.DirectoryPath, "settings.json"));
        await preferences.SaveAsync(new AppPreferences { DownloadCacheDirectory = temp.DirectoryPath });
        var fake = new FixtureDownload();
        using var vm = new DownloadsViewModel(fake, preferences, new LocalizationService("zh-CN"));
        // 摘要必须与替身实际写出的字节一致，缓存才可能命中。
        var digest = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
        var asset = new Asset
        {
            InstallerId = "fixture", ReleaseKey = "stable:1", Name = "setup.exe", Size = 3,
            DownloadUrl = "https://example.invalid/setup.exe", Digest = digest,
        };
        await vm.DownloadAsync(asset);
        Assert.Equal(1, fake.Calls);
        var downloaded = fake.Last!.TargetPath;
        Assert.True(File.Exists(downloaded));
        Assert.Contains("下载完成", vm.Status);

        // 再次下载同一资产：校验命中缓存，直接复用且不再调用下载服务。
        await vm.DownloadAsync(asset);
        Assert.Equal(1, fake.Calls);
        Assert.Contains("复用", vm.Status);
        Assert.True(File.Exists(downloaded));
    }

    [Theory]
    [InlineData("../unsafe.exe")]
    [InlineData("folder\\unsafe.exe")]
    public async Task DownloadsViewModel_RejectsUnsafeAssetNames(string name)
    {
        using var temp = new TempDirectory();
        var preferences = new PreferencesService(Path.Combine(temp.DirectoryPath, "settings.json"));
        var fake = new FixtureDownload();
        using var vm = new DownloadsViewModel(fake, preferences, new LocalizationService("zh-CN"));
        await vm.DownloadAsync(new Asset { InstallerId = "fixture", ReleaseKey = "stable:1", Name = name, DownloadUrl = "https://example.invalid/f.exe" });
        Assert.Null(fake.Last);
        Assert.Contains("无效", vm.Status);
    }

    [Fact]
    public async Task Sources_ProbesOnlyWhenAwaitedAndDistinguishesTimeoutFromNoResults()
    {
        var fake = new FixtureWinget();
        using var vm = new SourcesViewModel(fake, new LocalizationService("zh-CN"));
        Assert.Equal(0, fake.Probes);
        Assert.True(Assert.Single(vm.Packages).IsStatic);
        await vm.InitializeAsync();
        Assert.Equal(1, fake.Probes);
        vm.WingetQuery = "fixture";
        await vm.SearchAsync();
        Assert.Contains("超时", vm.ErrorMessage);
        Assert.Single(vm.Packages);
        Assert.False(vm.IsEmpty);
        Assert.Contains("静态", vm.ResultNotice);
    }

    private sealed class FixtureHandler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond();
    }

    private sealed class FixtureDownload : IDownloadService
    {
        public DownloadRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);
        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Last = request;
            Calls++;
            // 缓存现在会核验落盘产物，因此替身必须真的写出文件。
            var bytes = new byte[] { 1, 2, 3 };
            Directory.CreateDirectory(Path.GetDirectoryName(request.TargetPath)!);
            File.WriteAllBytes(request.TargetPath, bytes);
            return Task.FromResult(DownloadResult.Success(request.Source, request.TargetPath, bytes.Length, DownloadEngine.Http));
        }
    }

    private sealed class FixtureWinget : IWingetService
    {
        public int Probes { get; private set; }
        public string? LastProbeError => null;
        public Task<bool> ProbeAsync(CancellationToken ct = default) { Probes++; return Task.FromResult(true); }
        public IReadOnlyList<WingetPackage> ListFeatured() => [new() { Id = "Fixture.Tool", Name = "Fixture", IsStatic = true, IsFeatured = true }];
        public Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default) =>
            Task.FromResult(new WingetSearchResult { Succeeded = false, ErrorCode = WingetErrorCodes.TimedOut });
    }
}
