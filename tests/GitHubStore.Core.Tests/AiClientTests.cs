using System.Net;
using System.Text;
using System.Text.Json;
using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Preferences;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class AiClientTests
{
    private static async Task<PreferencesService> Preferences(TempDirectory directory)
    {
        var preferences = new PreferencesService(directory.Combine("settings.json"), new TestProtector());
        await preferences.SaveAsync(new AppPreferences { AiEndpoint = "http://127.0.0.1:12345/v1", AiModel = "minimax-cn/MiniMax-M3" });
        return preferences;
    }
    private static HttpResponseMessage Response(string content, string finish = "stop") => StubHttpHandler.Json(JsonSerializer.Serialize(new
    { choices = new[] { new { finish_reason = finish, message = new { content } } } }));

    [Fact]
    public async Task UsesSavedLocalEndpointAndModelWithoutHardcodedCredentials()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var client = new OpenAiClient(preferences, _ => new StubHttpHandler(request =>
        {
            Assert.Equal("http://127.0.0.1:12345/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("minimax-cn/MiniMax-M3", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("reference", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            return Response("Visible answer");
        }));
        Assert.True(client.IsConfigured);
        Assert.Equal("Visible answer", await client.CompleteAsync(new("instruction", "reference")));
    }

    [Theory]
    [InlineData("<think>private chain</think>\n# Answer", "# Answer")]
    [InlineData("<think>first</think> <think>second</think>answer", "answer")]
    [InlineData("plain answer", "plain answer")]
    public async Task RemovesLeadingReasoningBlocks(string response, string expected)
    {
        using var directory = new TempDirectory();
        var client = new OpenAiClient(await Preferences(directory), _ => new StubHttpHandler(_ => Response(response)));
        Assert.Equal(expected, await client.CompleteAsync(new("system", "input")));
    }

    [Theory]
    [InlineData("<think>private only</think>")]
    [InlineData("<think>unclosed")]
    [InlineData("answer<think>private</think>")]
    public async Task RejectsReasoningOnlyOrMalformedBlocks(string response)
    {
        using var directory = new TempDirectory();
        var client = new OpenAiClient(await Preferences(directory), _ => new StubHttpHandler(_ => Response(response)));
        Assert.Equal("ai_invalid_response", (await Assert.ThrowsAsync<AiSummaryException>(() => client.CompleteAsync(new("system", "input")))).Code);
    }

    [Theory]
    [InlineData(401, "ai_auth_failed")]
    [InlineData(429, "rate_limited")]
    [InlineData(302, "ai_failed")]
    public async Task ErrorsNeverIncludeRemoteBody(int status, string expected)
    {
        using var directory = new TempDirectory();
        var client = new OpenAiClient(await Preferences(directory), _ => new StubHttpHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private server details") }));
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => client.CompleteAsync(new("system", "input")));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private server details", error.ToString());
    }

    [Fact]
    public async Task RejectsOversizedIncompleteAndMalformedResponses()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        foreach (var response in new[] { "{}", "{\"choices\":[]}", "{\"choices\":[{\"message\":{\"content\":17}}]}" })
        {
            var malformed = new OpenAiClient(preferences, _ => new StubHttpHandler(_ => StubHttpHandler.Json(response)));
            Assert.Equal("ai_invalid_response", (await Assert.ThrowsAsync<AiSummaryException>(() => malformed.CompleteAsync(new("system", "input")))).Code);
        }
        var oversized = new OpenAiClient(preferences, _ => new StubHttpHandler(_ => StubHttpHandler.Json(new string('x', 1024 * 1024 + 1))));
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<AiSummaryException>(() => oversized.CompleteAsync(new("system", "input")))).Code);
        var incomplete = new OpenAiClient(preferences, _ => new StubHttpHandler(_ => Response("partial", "length")));
        Assert.Equal("ai_incomplete", (await Assert.ThrowsAsync<AiSummaryException>(() => incomplete.CompleteAsync(new("system", "input")))).Code);
    }

    [Fact]
    public async Task CancellationPropagatesAndLargeInputDoesNotSend()
    {
        using var directory = new TempDirectory();
        var calls = 0;
        var client = new OpenAiClient(await Preferences(directory), _ => new StubHttpHandler(_ => { calls++; return Response("answer"); }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(new("system", "input"), cancellation.Token));
        Assert.Equal("ai_invalid_request", (await Assert.ThrowsAsync<AiSummaryException>(() => client.CompleteAsync(new("system", new string('x', 240001))))).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExplicitSnapshotKeepsModelAndEndpointConsistentWithCacheIdentity()
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        var original = preferences.Current;
        await preferences.SaveAsync(original with { AiModel = "changed-model", AiEndpoint = "http://localhost:54321/v1" });
        var client = new OpenAiClient(preferences, settings => new StubHttpHandler(request =>
        {
            Assert.Equal(original.AiModel, settings.AiModel);
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            return Response("answer");
        }));
        await client.CompleteAsync(new("system", "input") { SettingsSnapshot = original });
    }

    [Theory]
    [InlineData("minimax-cn/MiniMax-M3", 2200, 4096)]
    [InlineData("MINIMAX-M3", 1600, 4096)]
    [InlineData("minimax-cn/MiniMax-M3", 6000, 6000)]
    [InlineData("other-model", 1600, 1600)]
    [InlineData("other-model", 2200, 2200)]
    public async Task MiniMaxReservesReasoningTokensWithoutChangingVisibleCharacterBudget(string model, int requested, int expected)
    {
        using var directory = new TempDirectory();
        var preferences = await Preferences(directory);
        await preferences.SaveAsync(preferences.Current with { AiModel = model });
        var client = new OpenAiClient(preferences, _ => new StubHttpHandler(request =>
        {
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(expected, json.RootElement.GetProperty("max_tokens").GetInt32());
            return Response("<think>private reasoning consumes separate tokens</think>ok");
        }));
        Assert.Equal("ok", await client.CompleteAsync(new("system", "input")
        { MaxOutputTokens = requested, MaxOutputCharacters = 2 }));
        var tooLong = new OpenAiClient(preferences, _ => new StubHttpHandler(_ => Response("<think>reasoning</think>long")));
        Assert.Equal("ai_invalid_response", (await Assert.ThrowsAsync<AiSummaryException>(() => tooLong.CompleteAsync(new("system", "input")
        { MaxOutputTokens = requested, MaxOutputCharacters = 2 }))).Code);
    }

    [Fact]
    public async Task InFlightCancellationPropagatesToTransportWithoutBecomingTimeout()
    {
        using var directory = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new OpenAiClient(await Preferences(directory), _ => new AsyncHandler(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response("unreachable");
        }));
        using var cancellation = new CancellationTokenSource();
        var pending = client.CompleteAsync(new("system", "input"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task TransportFailureDoesNotLeakExceptionDetails()
    {
        using var directory = new TempDirectory();
        var client = new OpenAiClient(await Preferences(directory), _ => new AsyncHandler(_ =>
            throw new HttpRequestException("private-host-and-token")));
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => client.CompleteAsync(new("system", "input")));
        Assert.Equal("network_error", error.Code);
        Assert.DoesNotContain("private-host-and-token", error.ToString());
    }

    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value[5..]));
    }
}
