using System.Net;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.Services.Tools;
using GitHubStore.App.Services.Winget;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ManagedToolIntegrationTests
{
    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("x86", "x86")]
    [InlineData("arm64", "x86")]
    public async Task ActualEmbeddedPackage_PreparesOfflineInApplicationDirectory(string architecture, string selected)
    {
        using var directory = new TempDirectory();
        var packages = new BundledToolPackageSource(typeof(ManagedToolIntegrationTests).Assembly);
        var tools = new ManagedToolManager(directory.DirectoryPath, packages,
            _ => Task.FromResult<string?>(null), architecture, directory.DirectoryPath);
        var resolved = await tools.EnsureAsync(ManagedToolKind.Aria2);
        Assert.True(resolved.IsAvailable, resolved.ErrorCode);
        Assert.Equal("1.37.0", resolved.Version);
        Assert.Equal(Path.Combine(directory.DirectoryPath, "tools", "aria2", "1.37.0", selected, "aria2c.exe"), resolved.ExecutablePath);
        await using var stream = File.OpenRead(resolved.ExecutablePath!);
        var magic = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(magic));
        Assert.Equal(new byte[] { (byte)'M', (byte)'Z' }, magic);
        Assert.True(stream.Length > 1024 * 1024);
        Assert.Equal(architecture == "arm64" ? "bundled-x86-compatibility-non-native" : "bundled", resolved.Origin);
    }

    [Fact]
    public async Task Aria2PreparationFailure_DoesNotUseLegacyExecutableOrFallBackToHttp()
    {
        using var directory = new TempDirectory();
        var legacy = directory.Combine("legacy-aria2.exe");
        await File.WriteAllTextAsync(legacy, "not an executable");
        var preferences = new PreferencesService(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { DownloadEngine = DownloadEngine.Aria2, Aria2ExecutablePath = legacy });
        var tools = new FixtureTools(kind => new() { Kind = kind, ErrorCode = ToolErrorCodes.ExecutableChecksumMismatch });
        var httpCreated = false;
        var service = new ConfiguredDownloadService(preferences, _ =>
        {
            httpCreated = true;
            return new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.OK)));
        }, tools);
        var result = await service.DownloadAsync(Request(directory));
        Assert.False(result.Succeeded);
        Assert.Equal(DownloadEngine.Aria2, result.Engine);
        Assert.Equal(DownloadErrorCodes.Aria2NotFound, result.ErrorCode);
        Assert.Equal(new[] { ManagedToolKind.Aria2 }, tools.Calls);
        Assert.False(httpCreated);
        Assert.False(File.Exists(directory.Combine("payload.bin")));
        Assert.Equal("not an executable", await File.ReadAllTextAsync(legacy));
    }

    [Fact]
    public async Task HttpDownload_DoesNotPrepareAria2OrWinget()
    {
        using var directory = new TempDirectory();
        var preferences = new PreferencesService(directory.Combine("settings.json"));
        var tools = new FixtureTools(kind => throw new InvalidOperationException("Unexpected tool preparation: " + kind));
        var service = new ConfiguredDownloadService(preferences, _ => new HttpClient(new FixtureHandler(_ =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })), tools);
        var result = await service.DownloadAsync(Request(directory));
        Assert.True(result.Succeeded);
        Assert.Equal(DownloadEngine.Http, result.Engine);
        Assert.Empty(tools.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.OutputPath!));
    }

    [Fact]
    public async Task Xget_UsesIntegratedEndpointInsteadOfHiddenLegacyAddress()
    {
        using var directory = new TempDirectory();
        var preferences = new PreferencesService(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { UseAccelerator = true, XgetInstance = "https://legacy.invalid/" });
        var tools = new FixtureTools(kind => new()
        {
            Kind = kind, IsAvailable = true, Endpoint = new Uri("https://integrated.invalid/"), Origin = "built-in",
        });
        Uri? effective = null;
        var service = new ConfiguredDownloadService(preferences, _ => new HttpClient(new FixtureHandler(request =>
        {
            effective = request.RequestUri;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        })), tools);
        var result = await service.DownloadAsync(Request(directory) with
        { Source = new Uri("https://github.com/example/project/releases/download/v1/payload.bin") });
        Assert.True(result.Succeeded);
        Assert.Equal("integrated.invalid", effective!.Host);
        Assert.StartsWith("/gh/", effective.AbsolutePath);
        Assert.Equal(new[] { ManagedToolKind.Xget }, tools.Calls);
    }

    [Fact]
    public async Task PreCancelledDownload_DoesNotPrepareAnyTool()
    {
        using var directory = new TempDirectory();
        var preferences = new PreferencesService(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { DownloadEngine = DownloadEngine.Aria2 });
        var tools = new FixtureTools(kind => throw new InvalidOperationException("Unexpected tool preparation"));
        var service = new ConfiguredDownloadService(preferences, tools: tools);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var result = await service.DownloadAsync(Request(directory), ct: cancellation.Token);
        Assert.Equal(DownloadErrorCodes.Cancelled, result.ErrorCode);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task Winget_UsesAutomaticallyResolvedSystemPathAndExactArguments()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("system-winget.exe");
        var tools = new FixtureTools(kind => new() { Kind = kind, IsAvailable = true, ExecutablePath = path, Origin = "system" });
        var runner = new FixtureProcess();
        var service = new WingetService(runner, wingetPath: directory.Combine("ignored-manual-path.exe"), tools: tools);
        Assert.True(await service.ProbeAsync());
        Assert.Equal(path, runner.Last!.FileName);
        Assert.Equal(new[] { "--version" }, runner.Last.Arguments);
        Assert.Equal(new[] { ManagedToolKind.Winget }, tools.Calls);
    }

    [Fact]
    public async Task MissingSystemWinget_IsReportedWithoutTryingAnUnmanagedExecutable()
    {
        var tools = new FixtureTools(kind => new() { Kind = kind, ErrorCode = ToolErrorCodes.SystemToolMissing });
        var runner = new FixtureProcess();
        var service = new WingetService(runner, tools: tools);
        Assert.False(await service.ProbeAsync());
        Assert.Equal(WingetErrorCodes.NotInstalled, service.LastProbeErrorCode);
        Assert.Null(runner.Last);
    }

    private static DownloadRequest Request(TempDirectory directory) => new()
    {
        Source = new Uri("https://example.invalid/payload.bin"),
        TargetPath = directory.Combine("payload.bin"), ExpectedSize = 3,
    };

    private sealed class FixtureTools(Func<ManagedToolKind, ToolResolution> resolve) : IToolManager
    {
        public string ToolDirectory => "unused";
        public List<ManagedToolKind> Calls { get; } = [];
        public Task<ToolResolution> EnsureAsync(ManagedToolKind kind, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(kind);
            return Task.FromResult(resolve(kind));
        }
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class FixtureProcess : IProcessRunner
    {
        public ProcessRequest? Last { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new ProcessResult { Outcome = ProcessOutcome.Exited, ExitCode = 0, StandardOutput = "v1.29.380" });
        }
    }
}
