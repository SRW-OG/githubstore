using System.Text.Json;
using GitHubStore.App.Services.Ai;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

public sealed class AiAssistantTests
{
    private static CatalogApp App(int number) => new()
    {
        Id = new(new SourceInstanceId(Guid.Parse("11111111-1111-1111-1111-111111111111")), new PackageId("app" + number)),
        SourceRevision = 1, Owner = "fixture", Repo = "app" + number, DisplayName = "Tool " + number,
        Description = number == 129 ? "Offline batch image editor" : "Windows utility", WindowsReady = true,
    };

    [Fact]
    public async Task SemanticSearchIncludesEntirePagedCatalogRatherThanKeywordPrefilter()
    {
        var apps = Enumerable.Range(0, 130).Select(App).ToArray();
        var catalog = new CatalogStub(apps);
        var model = new ClientStub(request =>
        {
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var candidates = doc.RootElement.GetProperty("candidates");
            Assert.Equal(130, candidates.GetArrayLength());
            Assert.Contains("Offline batch image editor", request.UserPrompt);
            Assert.Contains("ENTIRE", request.SystemPrompt);
            return JsonSerializer.Serialize(new { appIds = new[] { apps[129].Id.ToString(), apps[0].Id.ToString(), apps[129].Id.ToString() } });
        });
        var service = new AiAssistantService(model, catalog, new LauncherStub());
        var result = await service.SearchAsync("离线批量修图", "zh-CN");
        Assert.Equal(new[] { apps[129], apps[0] }, result);
        Assert.True(catalog.BrowseCalls > 1);
    }

    [Theory]
    [InlineData("{\"appIds\":[\"invented-id\"]}")]
    [InlineData("{\"appIds\":[123]}")]
    [InlineData("{\"apps\":[]}")]
    [InlineData("not json")]
    public async Task SearchRejectsInventedIdsAndMalformedModelOutput(string response)
    {
        var service = new AiAssistantService(new ClientStub(_ => response), new CatalogStub(new[] { App(1) }), new LauncherStub());
        Assert.Equal("ai_invalid_response", (await Assert.ThrowsAsync<AiSummaryException>(() => service.SearchAsync("image", "en"))).Code);
    }

    [Fact]
    public async Task SearchSupportsEmptyResultsAndDoesNotCallModelForEmptyLibrary()
    {
        var model = new ClientStub(_ => "{\"appIds\":[]}");
        var service = new AiAssistantService(model, new CatalogStub(Array.Empty<CatalogApp>()), new LauncherStub());
        Assert.Empty(await service.SearchAsync("anything", "en"));
        Assert.Equal(0, model.Calls);
        service = new AiAssistantService(model, new CatalogStub(new[] { App(1) }), new LauncherStub());
        Assert.Empty(await service.SearchAsync("nothing suitable", "en"));
    }

    [Fact]
    public async Task InstalledQuestionUsesActualReceiptIdentitiesAndCatalogFactsWithoutPaths()
    {
        var app = App(129);
        var launcher = new LauncherStub(Receipt(app, "receipt-1"));
        var model = new ClientStub(request =>
        {
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var installed = doc.RootElement.GetProperty("installedApps");
            Assert.Single(installed.EnumerateArray());
            Assert.Equal("receipt-1", installed[0].GetProperty("installId").GetString());
            Assert.Contains("Offline batch image editor", request.UserPrompt);
            Assert.DoesNotContain("private-user", request.UserPrompt);
            Assert.Contains("not all Windows software", request.UserPrompt);
            return "# Installed answer";
        });
        var service = new AiAssistantService(model, new CatalogStub(new[] { app, App(2) }), launcher);
        Assert.Equal("# Installed answer", await service.AnalyzeAsync(new(AiAssistantTask.InstalledQuestion) { Question = "Which edits images?" }, "en"));
    }

    [Fact]
    public async Task UninstallExplanationResolvesOnlySelectedRealReceiptAndRejectsUnknownReceipt()
    {
        var app = App(1);
        var other = App(2);
        var model = new ClientStub(request =>
        {
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var installed = doc.RootElement.GetProperty("installedApps");
            Assert.Single(installed.EnumerateArray());
            Assert.Equal("selected", installed[0].GetProperty("installId").GetString());
            Assert.False(doc.RootElement.GetProperty("verifiedUninstallMetadata").GetBoolean());
            // 提示词必须按登记区分三种移除情形，且不得再声称完全没有卸载能力。
            Assert.Contains("storeExtractedInstallation", request.SystemPrompt);
            Assert.Contains("storePerformedMsi", request.SystemPrompt);
            Assert.Contains("removalAvailableInStore", request.SystemPrompt);
            Assert.Contains("Do not invent config paths", request.SystemPrompt);
            return "Removal depends on how the app was installed.";
        });
        var service = new AiAssistantService(model, new CatalogStub(new[] { app, other }),
            new LauncherStub(Receipt(app, "selected"), Receipt(other, "other")));
        await service.AnalyzeAsync(new(AiAssistantTask.UninstallExplanation) { InstallId = "selected" }, "en");
        Assert.Equal("ai_invalid_request", (await Assert.ThrowsAsync<AiSummaryException>(() =>
            service.AnalyzeAsync(new(AiAssistantTask.UninstallExplanation) { InstallId = "invented" }, "en"))).Code);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task AssetExplanationContainsActualAlternativesAndExplicitCapabilityLimits()
    {
        var app = App(1);
        var asset = new Asset { InstallerId = "portable", ReleaseKey = "release", Name = "tool-windows-x64-portable.zip", DownloadUrl = "https://example.test/tool.zip", Kind = AssetKind.ArchiveZip, Arch = Arch.Amd64 };
        var other = asset with { InstallerId = "installer", Name = "tool-windows-x64.exe", Kind = AssetKind.InstallerExe };
        var release = new Release { AppId = app.Id, Key = "release", TagName = "v2", Body = "Requires Windows 11", Assets = new[] { asset, other } };
        var model = new ClientStub(request =>
        {
            Assert.Contains("tool-windows-x64-portable.zip", request.UserPrompt);
            Assert.Contains("tool-windows-x64.exe", request.UserPrompt);
            Assert.Contains("Requires Windows 11", request.UserPrompt);
            // External tar cannot enforce the required pre-extraction safety limits.
            Assert.Contains("ZIP archives (no 7z extraction)", request.UserPrompt);
            Assert.Contains("do not claim they are available", request.UserPrompt);
            Assert.Contains("not verified compatibility", request.SystemPrompt);
            return "Package explanation";
        });
        var service = new AiAssistantService(model, new CatalogStub(new[] { app }), new LauncherStub());
        await service.AnalyzeAsync(new(AiAssistantTask.AssetExplanation) { App = app, Asset = asset, Release = release }, "en");
    }

    [Fact]
    public async Task DiagnosisAndReleaseImpactKeepEvidenceLimitsAndKnownDiagnosticCodes()
    {
        var app = App(1);
        var requests = new List<AiClientRequest>();
        var model = new ClientStub(request => { requests.Add(request); return "Explanation"; });
        var service = new AiAssistantService(model, new CatalogStub(new[] { app }), new LauncherStub(Receipt(app, "receipt")));
        await service.AnalyzeAsync(new(AiAssistantTask.InstallDiagnosis) { Context = "{\"installerExitCode\":1603,\"phase\":\"RunningInstaller\"}" }, "en");
        Assert.Contains("1603", requests[0].UserPrompt);
        Assert.Contains("Do not infer HTTP status", requests[0].SystemPrompt);
        await service.AnalyzeAsync(new(AiAssistantTask.ReleaseImpact) { App = app, Release = new Release { AppId = app.Id, Key = "r", TagName = "v3", Body = "Plugin API changed" } }, "en");
        Assert.Contains("Plugin API changed", requests[1].UserPrompt);
        Assert.Contains("v1", requests[1].UserPrompt);
        Assert.Contains("do not claim full comparison", requests[1].SystemPrompt);
    }

    [Fact]
    public async Task CancellationAndUnconfiguredServiceDoNotCallModel()
    {
        var model = new ClientStub(_ => "answer") { IsConfigured = false };
        var service = new AiAssistantService(model, new CatalogStub(new[] { App(1) }), new LauncherStub());
        Assert.Equal("ai_not_configured", (await Assert.ThrowsAsync<AiSummaryException>(() => service.SearchAsync("query", "en"))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(new(AiAssistantTask.InstallDiagnosis), "en", cancellation.Token));
        Assert.Equal(0, model.Calls);
    }

    private static LauncherApp Receipt(CatalogApp app, string id) => new()
    {
        InstallId = id, AppId = app.Id, DisplayName = app.DisplayName, DisplayVersion = "v1", IsAvailable = true,
        InstallDirectory = "C:\\Users\\private-user\\apps", ExecutablePath = "C:\\Users\\private-user\\apps\\tool.exe",
    };
    private sealed class ClientStub(Func<AiClientRequest, string> complete) : IAiClient
    {
        public bool IsConfigured { get; set; } = true;
        public int Calls;
        public Task<string> CompleteAsync(AiClientRequest request, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(complete(request)); }
    }
    private sealed class LauncherStub(params LauncherApp[] apps) : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherApp>>(apps);
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnregisterAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class CatalogStub(IReadOnlyList<CatalogApp> apps) : ICatalogService
    {
        public int BrowseCalls;
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default)
        {
            BrowseCalls++;
            var items = apps.Skip(query.Offset).Take(Math.Min(query.Limit, 17)).ToArray();
            return Task.FromResult(new CatalogPage { Items = items, Total = apps.Count, HasMore = query.Offset + items.Length < apps.Count });
        }
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>(Array.Empty<Category>());
        public Task<IReadOnlyList<CatalogApp>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) => Task.FromResult(new AppDetail { App = apps.Single(a => a.Id == id) });
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
