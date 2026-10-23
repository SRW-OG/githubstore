using System.Net;
using System.Text;
using System.Text.Json;
using GitHubStore.App.Services.Discovery;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class DiscoveryFeedTests
{
    // Recorded source fields from trendshift.io Flight initialData/initialMentions on 2026-10-22.
    private const string Daily = """
    {"initialData":[
      {"rank":1,"score":8568,"full_name":"peach-mitao/peach","repository_id":309486,"repository_language":"Python","repository_description":"Peach 蜜桃：单用户、本地优先的个人媒体系统 · Single-user, local-first personal media system","repository_stars":1363,"repository_stars_gained":1071,"date":"2026-10-22T00:00:00Z"},
      {"rank":14,"score":3189,"full_name":"deepseek-ai/DeepInfer","repository_id":312526,"repository_language":"Python","repository_description":"DeepInfer: High-performance, large-scale inference framework for DeepSeek models","repository_stars":18932,"repository_stars_gained":1286,"date":"2026-10-22T00:00:00Z"}
    ],"initialMentions":[
      {"repository_full_name":"deepseek-ai/DeepInfer","platform":"x","author_handle":"VeknQAQ","link":"https://x.com/VeknQAQ/status/2113208834617454621","posted_at":"2026-10-22T10:00:22Z","content":"DeepSeek 10月20日开源了自用推理框架 DeepInfer，支持 V4.1-Flash、英伟达 SM100 和昇腾950。\n这次放出的是离线推理，完整服务还缺 KV 缓存系统、分离部署和负载均衡。配图是官方内测，不是个人实测。\nhttps://t.co/ahxELoPgqM https://t.co/qCBFmXOLYa"}
    ]}
    """;
    // Verbatim first two rows and published range from official Star History weekly Jina response, 2026-10-22.
    private const string Weekly = """
    Weekly All-time Random Pyramid

    1.   [1–![Image 4](https://github.com/morluto.png?size=32)rea+133.4k](https://www.star-history.com/morluto/rea)morluto/rea +133,434
    2.   [2 N![Image 5](https://github.com/google-research.png?size=32)harness2+16.1k](https://www.star-history.com/google-research/harness2)google-research/harness2 +16,085

    Updated Oct 14, 2026 – Oct 20, 2026
    """;
    private static readonly DateTimeOffset Fetched = DateTimeOffset.Parse("2026-10-23T09:00:00Z");
    private static string Flight(string data)
    {
        using var document = JsonDocument.Parse(data);
        var compact = JsonSerializer.Serialize(document.RootElement);
        return "<script>self.__next_f.push(" + JsonSerializer.Serialize(new object[] { 1, "4e:[\"$\",\"$L55\",null," + compact + "]\n" }) + ")</script>";
    }
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };

    [Fact]
    public void TrendshiftDecodesFlightAndKeepsSourceDateMetricsAndAttributedActualMention()
    {
        var parsed = DiscoveryFeedParser.Parse(DiscoveryFeedKind.TrendshiftDaily, Flight(Daily), Fetched);
        Assert.Equal(new DateOnly(2026, 10, 22), parsed.PeriodStart);
        Assert.Equal(parsed.PeriodStart, parsed.PeriodEnd);
        Assert.Equal(Fetched, parsed.FetchedAt);
        var first = parsed.Entries[0];
        Assert.Equal("peach-mitao/peach", first.RepositoryFullName);
        Assert.Equal(1, first.Rank);
        Assert.Equal(8568m, first.TrendScore);
        Assert.Equal(1363L, first.TotalStars);
        Assert.Equal(1071L, first.StarsGained);
        Assert.Equal("https://trendshift.io/repositories/309486", first.SourceUri.AbsoluteUri);
        Assert.Null(first.SourceReview);
        var mentioned = parsed.Entries[1];
        Assert.Contains("不是个人实测", mentioned.SourceReview);
        Assert.Contains("X 提及", mentioned.ReviewAttribution);
        Assert.Contains("@VeknQAQ", mentioned.ReviewAttribution);
        Assert.Equal("https://x.com/VeknQAQ/status/2113208834617454621", mentioned.ReviewUri!.AbsoluteUri);
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T10:00:22Z"), mentioned.ReviewPublishedAt);
    }

    [Fact]
    public void WeeklyUsesFullIntegerGainsAndPublishedWeekNeverRoundedCountsTotalsOrFetchDate()
    {
        var parsed = DiscoveryFeedParser.Parse(DiscoveryFeedKind.StarHistoryWeekly, Weekly, Fetched);
        Assert.Equal(new DateOnly(2026, 10, 14), parsed.PeriodStart);
        Assert.Equal(new DateOnly(2026, 10, 20), parsed.PeriodEnd);
        Assert.Equal(133434L, parsed.Entries[0].StarsGained);
        Assert.Equal(16085L, parsed.Entries[1].StarsGained);
        Assert.All(parsed.Entries, entry => { Assert.Null(entry.TotalStars); Assert.Null(entry.SourceReview); Assert.Empty(entry.Description); });
    }

    [Theory]
    [InlineData("\"rank\":14", "\"rank\":1")]
    [InlineData("\"repository_stars\":1363", "\"repository_stars\":-1")]
    [InlineData("peach-mitao/peach", "../malicious")]
    [InlineData("\"repository_id\":309486", "\"repository_id\":0")]
    public void RejectsInvalidRanksNumbersIdentitiesAndSourceIds(string oldValue, string newValue)
    {
        var error = Assert.Throws<DiscoveryFeedException>(() => DiscoveryFeedParser.Parse(DiscoveryFeedKind.TrendshiftDaily,
            Daily.Replace(oldValue, newValue, StringComparison.Ordinal), Fetched));
        Assert.Equal("discovery_invalid_response", error.Code);
    }

    [Fact]
    public void RejectsMismatchedDatesMalformedWeeklyRowsAndWrongSourceLinks()
    {
        Assert.Throws<DiscoveryFeedException>(() => DiscoveryFeedParser.Parse(DiscoveryFeedKind.TrendshiftDaily,
            Daily.Replace("\"date\":\"2026-10-22T00:00:00Z\"}", "\"date\":\"2026-10-21T00:00:00Z\"}", StringComparison.Ordinal).Replace("\"date\":\"2026-10-21T00:00:00Z\"},", "\"date\":\"2026-10-22T00:00:00Z\"},", StringComparison.Ordinal), Fetched));
        Assert.Throws<DiscoveryFeedException>(() => DiscoveryFeedParser.Parse(DiscoveryFeedKind.StarHistoryWeekly, Weekly.Replace("+133,434", "+133.4k", StringComparison.Ordinal), Fetched));
        Assert.Throws<DiscoveryFeedException>(() => DiscoveryFeedParser.Parse(DiscoveryFeedKind.StarHistoryWeekly, Weekly.Replace("https://www.star-history.com/morluto/rea", "https://evil.test/morluto/rea", StringComparison.Ordinal), Fetched));
        Assert.Throws<DiscoveryFeedException>(() => DiscoveryFeedParser.Parse(DiscoveryFeedKind.StarHistoryWeekly, Weekly.Replace("Oct 20, 2026", "Oct 10, 2026", StringComparison.Ordinal), Fetched));
    }

    [Fact]
    public async Task NamedReaderFallbackAndLastGoodCacheWorkWithoutFabricatedFallbackEntries()
    {
        using var directory = new TempDirectory();
        var failing = false;
        var requests = new List<Uri>();
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (failing || request.RequestUri!.Host != "r.jina.ai") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Response(Weekly);
        }));
        var service = new DiscoveryFeedService(http, directory.DirectoryPath, () => Fetched);
        var live = await service.GetAsync(DiscoveryFeedKind.StarHistoryWeekly);
        Assert.Equal(2, live.Entries.Count);
        Assert.Contains("r.jina.ai", live.SourceName);
        Assert.False(live.FromCache);
        Assert.Equal("www.star-history.com", live.SourceUri.Host);
        Assert.Equal(2, requests.Count);
        failing = true;
        var stale = await service.GetAsync(DiscoveryFeedKind.StarHistoryWeekly, refresh: true);
        Assert.True(stale.FromCache);
        Assert.True(stale.IsStale);
        Assert.NotNull(stale.ErrorCode);
        Assert.Equal(live.FetchedAt, stale.FetchedAt);
        Assert.Equal(live.Entries[0].RepositoryFullName, stale.Entries[0].RepositoryFullName);
        var calls = requests.Count;
        var reopened = new DiscoveryFeedService(http, directory.DirectoryPath, () => Fetched.AddMinutes(5));
        var disk = await reopened.GetAsync(DiscoveryFeedKind.StarHistoryWeekly);
        Assert.True(disk.FromCache);
        Assert.Equal(calls, requests.Count);
    }

    [Fact]
    public async Task SourcesFailIndependentlyAndTrendshiftFallbackRequestsVerifiedHtmlMode()
    {
        using var directory = new TempDirectory();
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "r.jina.ai" && request.RequestUri.AbsolutePath.Contains("trendshift.io", StringComparison.Ordinal))
            {
                Assert.Equal("html", Assert.Single(request.Headers.GetValues("X-Return-Format")));
                return Response(Flight(Daily));
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        var service = new DiscoveryFeedService(http, directory.DirectoryPath, () => Fetched);
        var results = await Task.WhenAll(service.GetAsync(DiscoveryFeedKind.TrendshiftDaily), service.GetAsync(DiscoveryFeedKind.StarHistoryWeekly));
        Assert.Equal(2, results[0].Entries.Count);
        Assert.Empty(results[1].Entries);
        Assert.NotNull(results[1].ErrorCode);
    }

    [Fact]
    public async Task DiskFailureDoesNotDiscardLiveResultAndCancellationDoesNotCallNetwork()
    {
        using var directory = new TempDirectory();
        var blocked = directory.Combine("not-a-directory");
        await File.WriteAllTextAsync(blocked, "file blocks directory creation");
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => { calls++; return Response(Flight(Daily)); }));
        var service = new DiscoveryFeedService(http, blocked, () => Fetched);
        var live = await service.GetAsync(DiscoveryFeedKind.TrendshiftDaily);
        Assert.Equal(2, live.Entries.Count);
        Assert.False(live.FromCache);
        Assert.True((await service.GetAsync(DiscoveryFeedKind.TrendshiftDaily)).FromCache);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(DiscoveryFeedKind.TrendshiftDaily, true, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task VerifiedSnapshotFallbackPreservesDatesAndRemainsStaleWithoutSuppressingNextLiveAttempt()
    {
        using var directory = new TempDirectory();
        var calls = 0;
        var failing = true;
        var seed = DiscoveryFeedParser.Parse(DiscoveryFeedKind.TrendshiftDaily, Daily, Fetched.AddDays(-1));
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            calls++;
            return failing ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(Flight(Daily));
        }));
        var service = new DiscoveryFeedService(http, directory.DirectoryPath, () => Fetched, _ => seed);
        var fallback = await service.GetAsync(DiscoveryFeedKind.TrendshiftDaily);
        Assert.True(fallback.FromCache);
        Assert.True(fallback.IsStale);
        Assert.NotNull(fallback.ErrorCode);
        Assert.Equal(seed.FetchedAt, fallback.FetchedAt);
        Assert.Equal(seed.PeriodStart, fallback.PeriodStart);
        Assert.Equal(2, calls);
        failing = false;
        var live = await service.GetAsync(DiscoveryFeedKind.TrendshiftDaily);
        Assert.False(live.FromCache);
        Assert.False(live.IsStale);
        Assert.Equal(3, calls);
        Assert.Equal(Fetched, live.FetchedAt);
    }

    [Fact]
    public async Task InvalidCachedSourceAndInvalidSnapshotCannotSupplyFakeRanks()
    {
        using var directory = new TempDirectory();
        var good = DiscoveryFeedParser.Parse(DiscoveryFeedKind.TrendshiftDaily, Daily, Fetched);
        var corrupt = good with { Entries = good.Entries.Select(entry => entry with { SourceUri = new("https://evil.test/repositories/1") }).ToArray() };
        var cacheDirectory = directory.Combine("discovery-cache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "TrendshiftDaily.json"), JsonSerializer.Serialize(corrupt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var result = await new DiscoveryFeedService(http, directory.DirectoryPath, () => Fetched, _ => corrupt).GetAsync(DiscoveryFeedKind.TrendshiftDaily);
        Assert.Empty(result.Entries);
        Assert.True(result.IsStale);
        Assert.NotNull(result.ErrorCode);
    }

    [Fact]
    public async Task OversizedResponsesProduceBoundedFailureWithNoInventedRows()
    {
        using var directory = new TempDirectory();
        using var http = new HttpClient(new StubHttpHandler(_ => Response(new string('x', 8 * 1024 * 1024 + 1))));
        var result = await new DiscoveryFeedService(http, directory.DirectoryPath).GetAsync(DiscoveryFeedKind.TrendshiftDaily);
        Assert.Empty(result.Entries);
        Assert.Equal("discovery_invalid_response", result.ErrorCode);
        Assert.True(result.IsStale);
    }
}
