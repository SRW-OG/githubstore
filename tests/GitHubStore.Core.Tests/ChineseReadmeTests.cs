using System.Text.RegularExpressions;
using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

public sealed class ChineseReadmeTests
{
    private static CatalogApp App(string repo = "tool") => new()
    {
        Id = new(new SourceInstanceId(Guid.Parse("22222222-2222-2222-2222-222222222222")), new PackageId(repo)),
        Owner = "fixture", Repo = repo, DisplayName = "Same name", SourceRevision = 1, DefaultBranch = "main",
    };
    private static async Task<PreferencesService> Preferences(TempDirectory directory)
    {
        var preferences = new PreferencesService(directory.Combine("settings.json"));
        await preferences.SaveAsync(new AppPreferences { AiEndpoint = "http://localhost:12345/v1", AiModel = "minimax-cn/MiniMax-M3" });
        return preferences;
    }
    private static string Excerpt(AiClientRequest request)
    {
        var start = request.UserPrompt.IndexOf("<README>\n", StringComparison.Ordinal) + "<README>\n".Length;
        return request.UserPrompt[start..request.UserPrompt.LastIndexOf("\n</README>", StringComparison.Ordinal)];
    }
    private static string LongReadme(int count = 70) => string.Join("\n\n", Enumerable.Range(0, count).Select(i =>
        $"## Section {i}\nThis paragraph describes every important application feature and installation requirement in complete detail. Do not omit this paragraph."));

    [Fact]
    public async Task TranslatesAllChunksPreservesCodeLinksAndRestoresOfflineWithoutCalls()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var source = new ReadmeStub { Markdown = LongReadme() + "\n\n```powershell\nWrite-Host 'unchanged'\n```\n\n[Website](https://example.test/path?q=1) and `command --flag`." };
        var client = new ClientStub(request => Task.FromResult(Excerpt(request).Replace("This paragraph", "本段", StringComparison.Ordinal)));
        var service = new ChineseReadmeService(source, client, preferences, directory.DirectoryPath);
        var progress = new ProgressStub();
        var result = await service.LoadAsync(App(), progress: progress);
        Assert.True(result.IsTranslated);
        Assert.False(result.InputTruncated);
        Assert.Contains("AI 中文翻译（非摘要）", result.Markdown);
        Assert.Contains("Write-Host 'unchanged'", result.Markdown);
        Assert.Contains("[Website](https://example.test/path?q=1)", result.Markdown);
        Assert.Contains("`command --flag`", result.Markdown);
        Assert.DoesNotContain("GHSKEEP", result.Markdown);
        Assert.Contains("Section 69", result.Markdown);
        Assert.True(client.Calls > 1);
        Assert.Equal(client.Calls, progress.Updates.Last().CompletedChunks);
        Assert.Equal(progress.Updates.Last().CompletedChunks, progress.Updates.Last().TotalChunks);
        var calls = client.Calls;
        var reopened = new ChineseReadmeService(source, client, preferences, directory.DirectoryPath);
        var cached = await reopened.GetCachedAsync(App());
        Assert.NotNull(cached);
        Assert.True(cached.FromCache);
        Assert.True(cached.IsTranslated);
        Assert.Equal(result.Markdown, cached.Markdown);
        Assert.Equal(calls, client.Calls);
        Assert.Equal(1, source.Loads);
    }

    [Fact]
    public async Task ChangedSourceModelEndpointAndSameDisplayNameHaveSeparateCacheIdentities()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var source = new ReadmeStub { Markdown = "# First document\nDetailed text." };
        var client = new ClientStub(request => Task.FromResult(Excerpt(request)));
        var service = new ChineseReadmeService(source, client, preferences, directory.DirectoryPath);
        await service.LoadAsync(App("one"));
        Assert.Null(await service.GetCachedAsync(App("two")));
        source.Markdown = "# Second document\nNew details.";
        await service.LoadAsync(App("one"));
        Assert.Contains("Second document", (await service.GetCachedAsync(App("one")))!.Markdown);
        await preferences.SaveAsync(preferences.Current with { AiModel = "another-model" });
        Assert.False((await service.GetCachedAsync(App("one")))!.IsTranslated);
        await service.LoadAsync(App("one"));
        await preferences.SaveAsync(preferences.Current with { AiEndpoint = "http://localhost:54321/v1" });
        Assert.False((await service.GetCachedAsync(App("one")))!.IsTranslated);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task PartialFailureDoesNotPublishFullTranslationAndCancellationPropagates()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var source = new ReadmeStub { Markdown = LongReadme() };
        var calls = 0;
        var client = new ClientStub(request => ++calls == 2
            ? throw new AiSummaryException("ai_failed") : Task.FromResult(Excerpt(request)));
        var service = new ChineseReadmeService(source, client, preferences, directory.DirectoryPath);
        await Assert.ThrowsAsync<AiSummaryException>(() => service.LoadAsync(App()));
        var cached = await service.GetCachedAsync(App());
        Assert.NotNull(cached);
        Assert.False(cached.IsTranslated);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.DirectoryPath, "ai-cache", "chinese-readme"), "translation-*.json"));
        using var cancellation = new CancellationTokenSource();
        var cancelClient = new ClientStub(request => { cancellation.Cancel(); return Task.FromResult(Excerpt(request)); });
        var cancelled = new ChineseReadmeService(source, cancelClient, preferences, directory.DirectoryPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.LoadAsync(App(), ct: cancellation.Token));
        Assert.False((await cancelled.GetCachedAsync(App()))!.IsTranslated);
    }

    [Fact]
    public async Task MissingProtectedCodeOrLinkRejectsTranslationRatherThanCachingModifiedContent()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var source = new ReadmeStub { Markdown = "# Readme\nRun `tool --flag` and [read docs](https://example.test/docs)." };
        var client = new ClientStub(request => Task.FromResult(Regex.Replace(Excerpt(request), @"GHSKEEP\d+X", "removed")));
        var service = new ChineseReadmeService(source, client, preferences, directory.DirectoryPath);
        Assert.Equal("ai_invalid_response", (await Assert.ThrowsAsync<AiSummaryException>(() => service.LoadAsync(App()))).Code);
        Assert.False((await service.GetCachedAsync(App()))!.IsTranslated);
    }

    [Fact]
    public async Task UnconfiguredClientReturnsReadableChineseOriginalWithoutModelCall()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var source = new ReadmeStub { Markdown = "# 中文说明\n这是软件的中文原文，保留全部内容。" };
        var client = new ClientStub(_ => throw new InvalidOperationException()) { IsConfigured = false };
        var result = await new ChineseReadmeService(source, client, preferences, directory.DirectoryPath).LoadAsync(App());
        Assert.False(result.IsTranslated);
        Assert.Equal(source.Markdown, result.Markdown);
        Assert.Equal(source.Markdown, result.OriginalMarkdown);
        Assert.Contains("尚未配置", result.Notice);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task OversizedReadmeExplicitlyMarksExcerptAndKeepsOriginalAndSnapshot()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var initialModel = preferences.Current.AiModel;
        var source = new ReadmeStub { Markdown = LongReadme(400) + "\nFINAL_UNTRANSLATED_MARKER" };
        var calls = 0;
        var client = new ClientStub(async request =>
        {
            Assert.Equal(initialModel, request.SettingsSnapshot!.AiModel);
            Assert.Contains("FULL TRANSLATION, NOT a summary", request.SystemPrompt);
            if (++calls == 1) await preferences.SaveAsync(preferences.Current with { AiModel = "changed-model" });
            return Excerpt(request);
        });
        var result = await new ChineseReadmeService(source, client, preferences, directory.DirectoryPath).LoadAsync(App());
        Assert.True(result.InputTruncated);
        Assert.Contains("节选", result.Notice);
        Assert.DoesNotContain("FINAL_UNTRANSLATED_MARKER", result.Markdown);
        Assert.Contains("FINAL_UNTRANSLATED_MARKER", result.OriginalMarkdown);
    }

    [Fact]
    public void ScreenshotExtractionUsesRealSafeImagesResolvesRelativeAndExcludesBadgesLogos()
    {
        var markdown = "![build](https://img.shields.io/build/status.png)\n![logo](assets/logo.png)\n" +
            "![Screenshot](../images/window.png)\n<img src=\"assets/preview.webp\" alt=\"preview\">\n" +
            "![third](https://github.com/fixture/tool/blob/main/media/demo.gif)\n" +
            "![bad](http://example.test/shot.png)\n![bad](https://example.test/shot.svg)\n![bad](data:image/png;base64,xx)\n" +
            "![bad](https://localhost/a.png)\n![duplicate](../images/window.png)\n![fourth](https://example.test/fourth.jpg)";
        var result = ReadmeScreenshotExtractor.Extract(App(), markdown, 99, new Uri("https://github.com/fixture/tool/blob/main/docs/README.md"));
        Assert.Equal(3, result.Count);
        Assert.Contains("https://raw.githubusercontent.com/fixture/tool/main/images/window.png", result);
        Assert.Contains("https://raw.githubusercontent.com/fixture/tool/main/docs/assets/preview.webp", result);
        Assert.Contains("https://raw.githubusercontent.com/fixture/tool/main/media/demo.gif", result);
        Assert.All(result, value => Assert.StartsWith("https://", value));
        Assert.Empty(ReadmeScreenshotExtractor.Extract(App(), markdown, 0));
    }

    private sealed class ProgressStub : IProgress<ChineseReadmeProgress>
    {
        public List<ChineseReadmeProgress> Updates { get; } = [];
        public void Report(ChineseReadmeProgress value) => Updates.Add(value);
    }
    private sealed class ClientStub(Func<AiClientRequest, Task<string>> complete) : IAiClient
    {
        public bool IsConfigured { get; set; } = true;
        public int Calls;
        public Task<string> CompleteAsync(AiClientRequest request, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls++; return complete(request); }
    }
    private sealed class ReadmeStub : IReadmeSummaryService
    {
        public string Markdown = "# README\nText.";
        public int Loads;
        public bool IsConfigured => true;
        public Task<ReadmeDocument> LoadReadmeAsync(CatalogApp app, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Loads++; return Task.FromResult(new ReadmeDocument(Markdown, "do-not-trust-upstream-hash", new Uri($"https://github.com/{app.Owner}/{app.Repo}/blob/main/README.md"), DateTimeOffset.UtcNow)); }
        public Task<AiReadmeSummary?> GetCachedAsync(CatalogApp app, string language, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AiReadmeSummary> GenerateAsync(CatalogApp app, ReadmeDocument readme, string language, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
