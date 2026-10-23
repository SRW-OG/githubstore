using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Preferences;

namespace GitHubStore.App.Services.Ai;

public sealed record AiClientRequest(string SystemPrompt, string UserPrompt)
{
    public int MaxOutputTokens { get; init; } = 2200;
    public int MaxOutputCharacters { get; init; } = 12000;
    internal AppPreferences? SettingsSnapshot { get; init; }
}

public interface IAiClient
{
    bool IsConfigured { get; }
    Task<string> CompleteAsync(AiClientRequest request, CancellationToken ct = default);
}

/// <summary>Bounded, cancellable OpenAI-compatible Chat Completions transport shared by AI features.</summary>
public sealed class OpenAiClient : IAiClient
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxInputCharacters = 240000;
    private readonly PreferencesService _preferences;
    private readonly Func<AppPreferences, HttpMessageHandler> _handlerFactory;

    public OpenAiClient(PreferencesService preferences, Func<AppPreferences, HttpMessageHandler>? handlerFactory = null)
    {
        _preferences = preferences;
        _handlerFactory = handlerFactory ?? (settings =>
        {
            var handler = ConfiguredDownloadService.CreateHandler(settings);
            handler.AllowAutoRedirect = false;
            handler.AutomaticDecompression = DecompressionMethods.All;
            return handler;
        });
    }

    public bool IsConfigured => Configured(_preferences.Current);

    private static bool Configured(AppPreferences settings) =>
        TryEndpoint(settings.AiEndpoint, out var endpoint) && !string.IsNullOrWhiteSpace(settings.AiModel)
        && (!endpoint!.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(settings.AiApiKey));

    public async Task<string> CompleteAsync(AiClientRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var settings = request.SettingsSnapshot ?? _preferences.Current;
        if (!Configured(settings)) throw new AiSummaryException("ai_not_configured");
        if (string.IsNullOrWhiteSpace(request.SystemPrompt) || string.IsNullOrWhiteSpace(request.UserPrompt)
            || request.SystemPrompt.Length + (long)request.UserPrompt.Length > MaxInputCharacters
            || request.MaxOutputCharacters is < 1 or > 24000 || request.MaxOutputTokens is < 1 or > 12000)
            throw new AiSummaryException("ai_invalid_request");
        TryEndpoint(settings.AiEndpoint, out var endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var http = new HttpClient(_handlerFactory(settings)) { Timeout = Timeout.InfiniteTimeSpan };
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Headers.UserAgent.ParseAdd("GitHubStore/0.1");
            if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey);
            // MiniMax counts inline reasoning against max_tokens; retain the caller's visible-character budget.
            var outputTokens = settings.AiModel.Contains("MiniMax", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(4096, request.MaxOutputTokens) : request.MaxOutputTokens;
            message.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = settings.AiModel.Trim(), stream = false, max_tokens = outputTokens,
                messages = new[] { new { role = "system", content = request.SystemPrompt }, new { role = "user", content = request.UserPrompt } },
            }), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AiSummaryException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "ai_auth_failed",
                HttpStatusCode.TooManyRequests => "rate_limited",
                _ => "ai_failed",
            });
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var choice = document.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() is "length" or "content_filter")
                throw new AiSummaryException("ai_incomplete");
            var text = choice.GetProperty("message").GetProperty("content").GetString()?.Trim();
            text = StripReasoning(text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > request.MaxOutputCharacters)
                throw new AiSummaryException("ai_invalid_response");
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AiSummaryException("request_timeout"); }
        catch (HttpRequestException) { throw new AiSummaryException("network_error"); }
        catch (IOException) { throw new AiSummaryException("network_error"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException or FormatException)
        { throw new AiSummaryException("ai_invalid_response"); }
    }

    public static bool TryEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        Uri uri;
        try { uri = new Uri(PreferencesService.NormalizeAiEndpoint(value)); }
        catch (ArgumentException) { return false; }
        var path = uri.AbsolutePath.TrimEnd('/');
        endpoint = new Uri(uri.GetLeftPart(UriPartial.Authority) + path +
            (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? "" : "/chat/completions"));
        return true;
    }

    private static string? StripReasoning(string? text)
    {
        while (text?.StartsWith("<think>", StringComparison.OrdinalIgnoreCase) == true)
        {
            var end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (end < 0) throw new AiSummaryException("ai_invalid_response");
            text = text[(end + 8)..].Trim();
        }
        // Never expose a stray reasoning delimiter or a model's incomplete private block.
        if (text?.Contains("<think>", StringComparison.OrdinalIgnoreCase) == true ||
            text?.Contains("</think>", StringComparison.OrdinalIgnoreCase) == true)
            throw new AiSummaryException("ai_invalid_response");
        return text;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new AiSummaryException("response_too_large");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxResponseBytes) throw new AiSummaryException("response_too_large");
            await memory.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return memory.ToArray();
    }
}
