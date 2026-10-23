using System.Net;
using System.Text;
using System.Text.Json;
using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ReadmeSummaryTests
{
    private static GitHubStore.Core.Abstractions.App App() => GitHubResponseParser.Parse(
        ApiFixture.RepositoryJson("fixture", "ReadmeTool"), null, SeedFixture.FetchedAt, new CategoryId("system")).App;
    private static HttpResponseMessage ReadmeResponse(string markdown = "# Example\nA Windows utility.") => StubHttpHandler.Json(JsonSerializer.Serialize(new
    {
        encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)),
        html_url = "https://github.com/fixture/ReadmeTool/blob/main/README.md",
    }));
    private static HttpResponseMessage ModelResponse(string markdown = "# 简介\n一个 Windows 工具。\n\n- 文件管理") => StubHttpHandler.Json(JsonSerializer.Serialize(new
    { choices = new[] { new { finish_reason = "stop", message = new { content = markdown } } } }));
    private static async Task<PreferencesService> Preferences(TempDirectory directory)
    {
        var preferences = new PreferencesService(directory.Combine("settings.json"), new TestProtector());
        await preferences.SaveAsync(new AppPreferences { AiEndpoint = "https://llm.example/v1", AiModel = "test-model", AiApiKey = "unit-test-token" });
        return preferences;
    }

    [Fact]
    public async Task Readme_LoadsOfficialApiWithoutAiCredentialsAndReusesDiskCache()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("https://api.github.com/repos/fixture/ReadmeTool/readme", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return ReadmeResponse();
        });
        using var http = new HttpClient(handler);
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath);
        var first = await service.LoadReadmeAsync(App());
        var reopened = new ReadmeSummaryService(http, preferences, directory.DirectoryPath);
        var second = await reopened.LoadReadmeAsync(App());
        Assert.Equal(first, second);
        Assert.Contains("Windows utility", first.Markdown);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Model_ReceivesReadmeAndSavedConfiguration_AndRestoresCachedSummaryWithoutNetwork()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        using var http = new HttpClient(new StubHttpHandler(_ => ReadmeResponse()));
        var calls = 0;
        string? sentBody = null;
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath, _ => new StubHttpHandler(request =>
        {
            calls++;
            Assert.Equal("https://llm.example/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("unit-test-token", request.Headers.Authorization!.Parameter);
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return ModelResponse();
        }));
        var readme = await service.LoadReadmeAsync(App());
        var generated = await service.GenerateAsync(App(), readme, "zh-CN");
        using var body = JsonDocument.Parse(sentBody!);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Contains(readme.Markdown, body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Contains("untrusted", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var cached = await service.GetCachedAsync(App(), "zh-CN");
        Assert.NotNull(cached);
        Assert.True(cached.FromCache);
        Assert.Equal(generated.Markdown, cached.Markdown);
        Assert.Equal(1, calls);
        foreach (var file in Directory.GetFiles(Path.Combine(directory.DirectoryPath, "ai-cache")))
            Assert.DoesNotContain("unit-test-token", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Cache_IsSeparatedByLanguageModelEndpointAndReadmeContent_AndForceRegenerates()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        using var http = new HttpClient(new StubHttpHandler(_ => ReadmeResponse()));
        var calls = 0;
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath,
            _ => new StubHttpHandler(_ => { calls++; return ModelResponse(); }));
        var readme = await service.LoadReadmeAsync(App());
        await service.GenerateAsync(App(), readme, "zh-CN");
        Assert.True((await service.GenerateAsync(App(), readme, "zh-CN")).FromCache);
        await service.GenerateAsync(App(), readme, "zh-CN", force: true);
        await service.GenerateAsync(App(), readme, "en-US");
        await preferences.SaveAsync(preferences.Current with { AiModel = "another-model" });
        Assert.Null(await service.GetCachedAsync(App(), "zh-CN"));
        await service.GenerateAsync(App(), readme, "zh-CN");
        await preferences.SaveAsync(preferences.Current with { AiEndpoint = "https://another.example/v1" });
        Assert.Null(await service.GetCachedAsync(App(), "zh-CN"));
        await service.GenerateAsync(App(), readme, "zh-CN");
        var markdown = readme.Markdown + "\nUpdated.";
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();
        await service.GenerateAsync(App(), readme with { Markdown = markdown, Sha = sha }, "zh-CN");
        Assert.Equal(6, calls);
    }

    [Theory]
    [InlineData(401, "ai_auth_failed")]
    [InlineData(429, "rate_limited")]
    [InlineData(500, "ai_failed")]
    [InlineData(302, "ai_failed")]
    public async Task HttpErrorsExposeOnlyStableCodesAndDoNotCache(int status, string code)
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        using var http = new HttpClient(new StubHttpHandler(_ => ReadmeResponse()));
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath, _ => new StubHttpHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("remote body with unit-test-token") }));
        var readme = await service.LoadReadmeAsync(App());
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => service.GenerateAsync(App(), readme, "zh-CN"));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("unit-test-token", error.ToString());
        Assert.Null(await service.GetCachedAsync(App(), "zh-CN"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")]
    public async Task MalformedModelResponseIsNotDisplayedOrCached(string json)
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        using var http = new HttpClient(new StubHttpHandler(_ => ReadmeResponse()));
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath,
            _ => new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var readme = await service.LoadReadmeAsync(App());
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => service.GenerateAsync(App(), readme, "zh-CN"));
        Assert.Equal("ai_invalid_response", error.Code);
        Assert.Null(await service.GetCachedAsync(App(), "zh-CN"));
    }

    [Fact]
    public async Task OversizedReadmeIsRejectedBeforeDecoding()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(new string('x', 1024 * 1024 + 1))));
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath);
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<AiSummaryException>(() => service.LoadReadmeAsync(App()))).Code);
    }

    [Fact]
    public async Task LargeReadmeUsesExplicitExcerptAndCancellationPropagates()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        using var http = new HttpClient(new StubHttpHandler(_ => ReadmeResponse(new string('x', 50000))));
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath, _ => new StubHttpHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("Input truncated: True", body);
            Assert.True(body.Length < 30000);
            return ModelResponse();
        }));
        var readme = await service.LoadReadmeAsync(App());
        Assert.True((await service.GenerateAsync(App(), readme, "zh-CN")).InputTruncated);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(App(), readme, "zh-CN", force: true, cancel.Token));
    }

    [Fact]
    public async Task DefaultOpenAiWithoutKeyIsReportedAsNotConfigured()
    {
        using var directory = new TempDirectory();
        var preferences = new PreferencesService(directory.Combine("settings.json"), new TestProtector());
        using var http = new HttpClient(new StubHttpHandler(_ => ReadmeResponse()));
        var calls = 0;
        var service = new ReadmeSummaryService(http, preferences, directory.DirectoryPath,
            _ => new StubHttpHandler(_ => { calls++; return ModelResponse(); }));
        var readme = await service.LoadReadmeAsync(App());
        Assert.Equal("ai_not_configured", (await Assert.ThrowsAsync<AiSummaryException>(() => service.GenerateAsync(App(), readme, "zh-CN"))).Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://api.example/v1/", "https://api.example/v1/chat/completions")]
    [InlineData("https://api.example/v1/chat/completions", "https://api.example/v1/chat/completions")]
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/chat/completions")]
    public void EndpointSupportsBaseAndCompleteUrls(string input, string expected)
    {
        Assert.True(ReadmeSummaryService.TryEndpoint(input, out var endpoint));
        Assert.Equal(expected, endpoint!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://api.example/v1")]
    [InlineData("https://token@api.example/v1")]
    [InlineData("https://api.example/v1?key=secret")]
    [InlineData("https://api.example/v1?")]
    [InlineData("file:///tmp/model")]
    public void InvalidEndpointIsRejected(string input) => Assert.False(ReadmeSummaryService.TryEndpoint(input, out _));

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
    }
}
