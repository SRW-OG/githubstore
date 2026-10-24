using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Discovery;

/// <summary>Independent bounded discovery feeds with validated last-good cache; never calls AI.</summary>
public sealed class DiscoveryFeedService : IDiscoveryFeedService
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<DiscoveryFeedKind, DiscoveryFeedResult?>? _seedProvider;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DiscoveryFeedKind, DiscoveryFeedResult> _memory = new();
    private readonly SemaphoreSlim[] _gates = [new(1, 1), new(1, 1)];

    public DiscoveryFeedService(HttpClient http, string dataDirectory, Func<DateTimeOffset>? clock = null,
        Func<DiscoveryFeedKind, DiscoveryFeedResult?>? seedProvider = null)
    {
        _http = http;
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "discovery-cache");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _seedProvider = seedProvider;
    }

    public async Task<DiscoveryFeedResult> GetAsync(DiscoveryFeedKind kind, bool refresh = false, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        await _gates[(int)kind].WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = await ReadCacheAsync(kind, ct).ConfigureAwait(false);
            if (!refresh && previous is not null && _clock() - previous.FetchedAt < TimeSpan.FromHours(1))
                return previous with { FromCache = true, IsStale = false, ErrorCode = null };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            string error = "discovery_unavailable";
            foreach (var proxy in new[] { false, true })
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(proxy ? 15 : 10));
                    var source = DiscoveryFeedParser.SourceUri(kind);
                    var fetchUri = proxy ? new Uri("https://r.jina.ai/" + source.AbsoluteUri) : source;
                    using var request = new HttpRequestMessage(HttpMethod.Get, fetchUri);
                    request.Headers.UserAgent.ParseAdd("GitHubStore/0.1");
                    request.Headers.Accept.ParseAdd(proxy ? "text/plain" : "text/html, application/json");
                    if (proxy && kind == DiscoveryFeedKind.TrendshiftDaily) request.Headers.TryAddWithoutValidation("X-Return-Format", "html");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) { error = "discovery_http_error"; continue; }
                    var finalUri = response.RequestMessage?.RequestUri ?? fetchUri;
                    if (finalUri.Scheme != "https" || !finalUri.Host.Equals(fetchUri.Host, StringComparison.OrdinalIgnoreCase))
                    { error = "discovery_invalid_response"; continue; }
                    var body = await ReadBoundedAsync(response.Content, attempt.Token).ConfigureAwait(false);
                    DiscoveryFeedResult parsed;
                    try
                    {
                        parsed = DiscoveryFeedParser.Parse(kind, body, _clock());
                    }
                    catch (DiscoveryFeedException) when (kind == DiscoveryFeedKind.StarHistoryWeekly && !proxy)
                    {
                        // 源站把每周排行内联在同源的页面脚本里，首页 HTML 本身已不含数据。
                        // 只跟随本页面自己引用的 https 同源脚本，绝不访问任意第三方地址。
                        var scriptBody = await TryFetchWeeklyScriptAsync(body, attempt.Token).ConfigureAwait(false);
                        if (scriptBody is null) throw;
                        parsed = DiscoveryFeedParser.Parse(kind, scriptBody, _clock());
                    }
                    if (proxy) parsed = parsed with { SourceName = parsed.SourceName + " · r.jina.ai" };
                    _memory[kind] = parsed;
                    await WriteCacheAsync(kind, parsed, ct).ConfigureAwait(false);
                    return parsed;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { error = "discovery_timeout"; if (timeout.IsCancellationRequested) break; }
                catch (HttpRequestException) { error = "discovery_network_error"; }
                catch (IOException) { error = "discovery_network_error"; }
                catch (DiscoveryFeedException ex) { error = ex.Code; }
            }
            ct.ThrowIfCancellationRequested();
            var fallback = previous ?? await ReadSeedAsync(kind, ct).ConfigureAwait(false);
            return fallback is not null
                ? fallback with { FromCache = true, IsStale = true, ErrorCode = error }
                : new DiscoveryFeedResult { Kind = kind, SourceName = DiscoveryFeedParser.SourceName(kind), SourceUri = DiscoveryFeedParser.SourceUri(kind), ErrorCode = error, IsStale = true };
        }
        finally { _gates[(int)kind].Release(); }
    }

    /// <summary>
    /// 取首页自己引用的同源脚本，用于读取内联的每周排行数据。
    /// 只接受 https、同主机、路径以 /assets/ 开头且以 .js 结尾的引用；找不到或不合格一律返回 null，
    /// 绝不去猜测或访问第三方地址。
    /// </summary>
    private async Task<string?> TryFetchWeeklyScriptAsync(string html, CancellationToken ct)
    {
        var match = Regex.Match(html, "(?i)src=\"(?<src>/assets/[A-Za-z0-9._-]{1,120}\\.js)\"", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var baseUri = DiscoveryFeedParser.SourceUri(DiscoveryFeedKind.StarHistoryWeekly);
        if (!Uri.TryCreate(baseUri, match.Groups["src"].Value, out var scriptUri)) return null;
        if (scriptUri.Scheme != Uri.UriSchemeHttps || !scriptUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, scriptUri);
        request.Headers.UserAgent.ParseAdd("GitHubStore/0.1");
        request.Headers.Accept.ParseAdd("application/javascript");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var finalUri = response.RequestMessage?.RequestUri ?? scriptUri;
        if (finalUri.Scheme != Uri.UriSchemeHttps || !finalUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) return null;
        return await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false);
    }

    private async Task<DiscoveryFeedResult?> ReadSeedAsync(DiscoveryFeedKind kind, CancellationToken ct)
    {
        try
        {
            DiscoveryFeedResult? seed;
            if (_seedProvider is not null) seed = _seedProvider(kind);
            else
            {
                var name = kind == DiscoveryFeedKind.TrendshiftDaily
                    ? "GitHubStore.Discovery.trendshift-daily.json" : "GitHubStore.Discovery.star-history-weekly.json";
                using var stream = typeof(DiscoveryFeedService).Assembly.GetManifestResourceStream(name);
                if (stream is null) return null;
                using var content = new StreamContent(stream);
                seed = JsonSerializer.Deserialize<DiscoveryFeedResult>(await ReadBoundedAsync(content, ct).ConfigureAwait(false), Json);
            }
            if (seed is null || seed.Kind != kind || seed.FetchedAt == default || seed.FetchedAt > _clock().AddMinutes(5)) return null;
            DiscoveryFeedParser.Validate(seed);
            // Do not store in memory/disk: every future request still attempts live fetching.
            return seed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or DiscoveryFeedException)
        { return null; }
    }

    private async Task<DiscoveryFeedResult?> ReadCacheAsync(DiscoveryFeedKind kind, CancellationToken ct)
    {
        if (_memory.TryGetValue(kind, out var live)) return live;
        try
        {
            var path = CachePath(kind);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            var cached = JsonSerializer.Deserialize<DiscoveryFeedResult>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Json);
            if (cached is null || cached.Kind != kind || cached.FetchedAt > _clock().AddMinutes(5) || cached.FetchedAt == default) return null;
            DiscoveryFeedParser.Validate(cached);
            _memory[kind] = cached;
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or DiscoveryFeedException) { return null; }
    }

    private async Task WriteCacheAsync(DiscoveryFeedKind kind, DiscoveryFeedResult value, CancellationToken ct)
    {
        var path = CachePath(kind);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Live results are not discarded on cache failure. */ }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private string CachePath(DiscoveryFeedKind kind) => Path.Combine(_directory, kind + ".json");
    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumBytes) throw new DiscoveryFeedException("discovery_invalid_response");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new DiscoveryFeedException("discovery_invalid_response");
            await buffer.WriteAsync(block.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException) { throw new DiscoveryFeedException("discovery_invalid_response"); }
    }
}

public sealed class DiscoveryFeedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
