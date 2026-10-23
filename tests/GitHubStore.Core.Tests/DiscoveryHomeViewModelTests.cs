using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class DiscoveryHomeViewModelTests
{
    [Fact]
    public async Task InitializationIsIdempotentLoadsBothSourcesAndNeverCallsModel()
    {
        var feeds = new FeedStub();
        var reviews = new ReviewStub();
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese(), reviews);
        var first = vm.InitializeAsync();
        var second = vm.InitializeAsync();
        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        Assert.Equal(2, feeds.Calls.Count);
        Assert.All(feeds.Calls, call => Assert.False(call.Refresh));
        Assert.Single(vm.Daily.Entries);
        Assert.Single(vm.Weekly.Entries);
        Assert.Empty(reviews.Calls);
        Assert.False(vm.IsBusy);
        Assert.Equal("首页", vm.Title);
        Assert.Equal("每日发现", vm.Daily.Title);
    }

    [Theory]
    [InlineData(25, 25)]
    [InlineData(55, 50)]
    public async Task FullDailyRankingIsPreservedWithinBoundAndWeeklyStaysCompact(int dailyCount, int expected)
    {
        var feeds = new FeedStub { Get = (kind, _, _) => Task.FromResult(Feed(kind, "ranking") with
        {
            Entries = Enumerable.Range(1, kind == DiscoveryFeedKind.TrendshiftDaily ? dailyCount : 20)
                .Select(index => Entry("item-" + index)).ToArray(),
        }) };
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        await vm.InitializeAsync();
        Assert.Equal(expected, vm.Daily.Entries.Count);
        Assert.Equal(20, vm.Weekly.Entries.Count);
        Assert.True(vm.Daily.Entries[0].ShowDescription);
        Assert.True(vm.Daily.Entries[0].ShowNoSourceReviewNotice);
        Assert.All(vm.Weekly.Entries, entry =>
        {
            Assert.False(entry.HasDescription);
            Assert.False(entry.ShowDescription);
            Assert.False(entry.ShowNoSourceReviewNotice);
        });
    }

    [Fact]
    public async Task DailyResultsAppearBeforeWeeklyCompletesAndWeeklyFailureDoesNotHideDaily()
    {
        var weekly = Pending<DiscoveryFeedResult>();
        var feeds = new FeedStub { Get = (kind, _, _) => kind == DiscoveryFeedKind.TrendshiftDaily
            ? Task.FromResult(Feed(kind, "daily")) : weekly.Task };
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        var initialization = vm.InitializeAsync();
        Assert.Single(vm.Daily.Entries);
        Assert.False(vm.Daily.IsBusy);
        Assert.True(vm.Weekly.IsBusy);
        Assert.True(vm.IsBusy);
        weekly.SetException(new HttpRequestException("private transport detail"));
        await initialization;
        Assert.Single(vm.Daily.Entries);
        Assert.False(vm.Daily.HasError);
        Assert.True(vm.Weekly.HasError);
        Assert.True(vm.Weekly.HasEmptyState);
        Assert.DoesNotContain("private transport", vm.Weekly.ErrorMessage);
        feeds.Get = (kind, _, _) => Task.FromResult(Feed(kind, "recovered"));
        await vm.RefreshAsync();
        Assert.False(vm.Weekly.HasError);
        Assert.Equal("fixture/recovered", Assert.Single(vm.Weekly.Entries).RepositoryName);
        Assert.All(feeds.Calls.Skip(2), call => Assert.True(call.Refresh));
    }

    [Fact]
    public async Task PeriodUsesSourcePublishedRangeNeverFetchDateOrCurrentWeek()
    {
        var feeds = new FeedStub { Get = (kind, _, _) => Task.FromResult(Feed(kind, "period") with
        {
            PeriodLabel = null, PeriodStart = new DateOnly(2026, 10, 14), PeriodEnd = new DateOnly(2026, 10, 20),
            FetchedAt = new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero),
        }) };
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        await vm.InitializeAsync();
        Assert.Contains("2026-10-14", vm.Weekly.PeriodText);
        Assert.Contains("2026-10-20", vm.Weekly.PeriodText);
        Assert.DoesNotContain("2035", vm.Weekly.PeriodText);
        feeds.Get = (kind, _, _) => Task.FromResult(Feed(kind, "unknown-period") with
        { PeriodLabel = null, PeriodStart = null, PeriodEnd = null, FetchedAt = DateTimeOffset.UtcNow });
        await vm.RefreshAsync();
        Assert.Contains("未提供", vm.Weekly.PeriodText);
    }

    [Fact]
    public async Task CachedStaleDataShowsCacheAndRefreshFailureAlongsideActualEntries()
    {
        var feeds = new FeedStub { Get = (kind, _, _) => Task.FromResult(Feed(kind, "cached") with
        { FromCache = true, IsStale = true, ErrorCode = "network_error", PeriodLabel = "source-defined period" }) };
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        await vm.InitializeAsync();
        Assert.Single(vm.Daily.Entries);
        Assert.True(vm.Daily.HasError);
        Assert.True(vm.Daily.HasCacheStatus);
        Assert.Contains("缓存", vm.Daily.CacheStatus);
        Assert.Contains("过期", vm.Daily.CacheStatus);
        Assert.Contains("source-defined period", vm.Daily.PeriodText);
        Assert.False(vm.Daily.HasEmptyState);
    }

    [Fact]
    public async Task RefreshFailureRetainsPreviousResultsAndTheirSourcePeriod()
    {
        var feeds = new FeedStub();
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        await vm.InitializeAsync();
        var entry = Assert.Single(vm.Daily.Entries);
        var period = vm.Daily.PeriodText;
        feeds.Get = (_, _, _) => throw new IOException("do not expose local paths");
        await vm.RefreshAsync();
        Assert.Same(entry, Assert.Single(vm.Daily.Entries));
        Assert.Equal(period, vm.Daily.PeriodText);
        Assert.True(vm.Daily.HasError);
        Assert.Contains("保留上次结果", vm.Daily.CacheStatus);
        Assert.DoesNotContain("local paths", vm.Daily.ErrorMessage);
    }

    [Fact]
    public async Task NewerRefreshWinsWhenOlderProvidersIgnoreCancellation()
    {
        var feeds = new FeedStub();
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        await vm.InitializeAsync();
        var oldDaily = Pending<DiscoveryFeedResult>();
        var oldWeekly = Pending<DiscoveryFeedResult>();
        CancellationToken oldToken = default;
        feeds.Get = (kind, _, ct) => { oldToken = ct; return kind == DiscoveryFeedKind.TrendshiftDaily ? oldDaily.Task : oldWeekly.Task; };
        var oldRefresh = vm.RefreshAsync();
        feeds.Get = (kind, _, _) => Task.FromResult(Feed(kind, "current"));
        await vm.RefreshAsync();
        Assert.True(oldToken.IsCancellationRequested);
        oldDaily.SetResult(Feed(DiscoveryFeedKind.TrendshiftDaily, "stale"));
        oldWeekly.SetResult(Feed(DiscoveryFeedKind.StarHistoryWeekly, "stale"));
        await oldRefresh;
        Assert.Equal("fixture/current", Assert.Single(vm.Daily.Entries).RepositoryName);
        Assert.Equal("fixture/current", Assert.Single(vm.Weekly.Entries).RepositoryName);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SharedCancelHandlesThrowingProviderCallbacksAndRejectsLateResults()
    {
        var daily = Pending<DiscoveryFeedResult>();
        var weekly = Pending<DiscoveryFeedResult>();
        var callbacks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        CancellationToken token = default;
        var feeds = new FeedStub { Get = (kind, _, ct) =>
        {
            token = ct;
            ct.Register(() =>
            {
                if (Interlocked.Increment(ref callbackCount) == 2) callbacks.TrySetResult();
                throw new InvalidOperationException("throwing provider callback");
            });
            return kind == DiscoveryFeedKind.TrendshiftDaily ? daily.Task : weekly.Task;
        } };
        using var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        var initialization = vm.InitializeAsync();
        Assert.Null(Record.Exception(() => vm.CancelCommand.Execute(null)));
        Assert.True(token.IsCancellationRequested);
        Assert.False(vm.IsBusy);
        Assert.False(vm.Daily.IsBusy);
        Assert.False(vm.Weekly.IsBusy);
        await callbacks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        daily.SetResult(Feed(DiscoveryFeedKind.TrendshiftDaily, "late"));
        weekly.SetResult(Feed(DiscoveryFeedKind.StarHistoryWeekly, "late"));
        await initialization;
        Assert.Empty(vm.Daily.Entries);
        Assert.Empty(vm.Weekly.Entries);
        Assert.Contains("取消", vm.StatusMessage);
    }

    [Fact]
    public async Task ManualAiReviewUsesActualEntryAndNeverBecomesSourceReview()
    {
        var reviews = new ReviewStub();
        using var vm = new DiscoveryHomeViewModel(new FeedStub(), Chinese(), reviews);
        await vm.InitializeAsync();
        var entry = Assert.Single(vm.Daily.Entries);
        Assert.Empty(reviews.Calls);
        Assert.True(entry.HasNoSourceReview);
        Assert.Contains("未提供文字评价", entry.NoSourceReviewLabel);
        await entry.ReviewCommand.ExecuteAsync(null);
        var call = Assert.Single(reviews.Calls);
        Assert.Equal(DiscoveryFeedKind.TrendshiftDaily, call.Kind);
        Assert.Same(entry.Entry, call.Entry);
        Assert.Equal("zh-CN", call.Language);
        Assert.Equal("## AI result", entry.AiReviewMarkdown);
        Assert.True(entry.HasAiReview);
        Assert.False(entry.HasSourceReview);
        Assert.Empty(entry.SourceReview);
        Assert.Contains("AI", entry.AiReviewLabel);
    }

    [Fact]
    public void WeeklyGainsAndTrendScoreAreDistinctFromTotalStarsAndAbsentValuesStayUnknown()
    {
        var data = Entry("metrics") with { TotalStars = 200000, StarsGained = 133434, TrendScore = 8568 };
        using var weekly = new DiscoveryEntryViewModel(DiscoveryFeedKind.StarHistoryWeekly, data, Chinese(), null);
        Assert.Equal("本周新增 Star", weekly.StarsGainedLabel);
        Assert.Equal("+133,434", weekly.StarsGainedText);
        Assert.Equal("200,000", weekly.TotalStarsText);
        Assert.Equal("累计 Star", weekly.TotalStarsLabel);
        Assert.Equal("趋势评分", weekly.TrendScoreLabel);
        Assert.Equal("8568", weekly.TrendScoreText);
        using var unknown = new DiscoveryEntryViewModel(DiscoveryFeedKind.StarHistoryWeekly, Entry("unknown"), Chinese(), null);
        Assert.False(unknown.HasTotalStars);
        Assert.False(unknown.HasStarsGained);
        Assert.False(unknown.HasTrendScore);
        Assert.Equal("—", unknown.TotalStarsText);
        Assert.Contains("未提供项目说明", unknown.Description);
    }

    [Fact]
    public void RealSourceReviewHasSeparateAttributionLinkAndSourceTimestamp()
    {
        var published = new DateTimeOffset(2026, 10, 20, 10, 30, 0, TimeSpan.FromHours(8));
        using var entry = new DiscoveryEntryViewModel(DiscoveryFeedKind.TrendshiftDaily, Entry("review") with
        {
            SourceReview = "A real source quotation in this fixture.", ReviewAttribution = "Source author",
            ReviewUri = new Uri("https://example.com/post/123"), ReviewPublishedAt = published,
        }, Chinese(), null);
        Assert.True(entry.HasSourceReview);
        Assert.False(entry.HasNoSourceReview);
        Assert.Contains("源站收录", entry.SourceReviewLabel);
        Assert.Equal("Source author", entry.SourceReviewAttribution);
        Assert.Equal("https://example.com/post/123", entry.ReviewUri?.AbsoluteUri);
        Assert.True(entry.HasReviewLink);
        Assert.Contains("2026-10-20", entry.ReviewPublishedText);
        Assert.Contains("+08:00", entry.ReviewPublishedText);
        using var absent = new DiscoveryEntryViewModel(DiscoveryFeedKind.TrendshiftDaily, Entry("no-review") with
        { ReviewUri = new Uri("https://example.com/post/123"), ReviewPublishedAt = published, ReviewAttribution = "Unmatched author" }, Chinese(), null);
        Assert.False(absent.HasSourceReview);
        Assert.False(absent.HasReviewLink);
        Assert.False(absent.HasReviewPublishedAt);
        Assert.Empty(absent.SourceReviewAttribution);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/post")]
    [InlineData("https://user:password@example.com/post")]
    public void UnsafeExternalLinksAreNotActionable(string value)
    {
        using var entry = new DiscoveryEntryViewModel(DiscoveryFeedKind.TrendshiftDaily, Entry("unsafe") with
        { RepositoryUri = new Uri(value), SourceUri = new Uri(value), SourceReview = "Source text", ReviewUri = new Uri(value) }, Chinese(), null);
        Assert.Null(entry.RepositoryUri);
        Assert.Null(entry.SourceUri);
        Assert.Null(entry.ReviewUri);
        Assert.False(entry.HasReviewLink);
    }

    [Fact]
    public async Task MissingAiConfigurationDoesNotCallProviderAndReviewErrorsDoNotLeakDetails()
    {
        var reviews = new ReviewStub { IsConfigured = false };
        using var vm = new DiscoveryHomeViewModel(new FeedStub(), Chinese(), reviews);
        await vm.InitializeAsync();
        var entry = Assert.Single(vm.Daily.Entries);
        await entry.ReviewCommand.ExecuteAsync(null);
        Assert.Empty(reviews.Calls);
        Assert.Contains("设置", entry.ReviewError);
        reviews.IsConfigured = true;
        reviews.Review = (_, _, _, _) => throw new HttpRequestException("secret-api-token");
        await entry.ReviewCommand.ExecuteAsync(null);
        Assert.NotEmpty(entry.ReviewError);
        Assert.DoesNotContain("secret-api-token", entry.ReviewError);
        Assert.False(entry.HasAiReview);
        Assert.False(entry.IsReviewBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrDisposedReviewCannotPublishLateProviderOutput(bool dispose)
    {
        var pending = Pending<string>();
        CancellationToken token = default;
        var reviews = new ReviewStub { Review = (_, _, _, ct) => { token = ct; return pending.Task; } };
        using var vm = new DiscoveryHomeViewModel(new FeedStub(), Chinese(), reviews);
        await vm.InitializeAsync();
        var entry = Assert.Single(vm.Daily.Entries);
        var operation = entry.ReviewCommand.ExecuteAsync(null);
        if (dispose) vm.Dispose(); else entry.CancelReviewCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        pending.SetResult("stale model result");
        await operation;
        Assert.False(entry.IsReviewBusy);
        Assert.Empty(entry.AiReviewMarkdown);
        Assert.Empty(entry.ReviewError);
    }

    [Fact]
    public async Task FeedRefreshDisposesReplacedEntryAndRejectsItsLateReview()
    {
        var pending = Pending<string>();
        CancellationToken token = default;
        var reviews = new ReviewStub { Review = (_, _, _, ct) => { token = ct; return pending.Task; } };
        using var vm = new DiscoveryHomeViewModel(new FeedStub(), Chinese(), reviews);
        await vm.InitializeAsync();
        var old = Assert.Single(vm.Daily.Entries);
        var operation = old.ReviewCommand.ExecuteAsync(null);
        await vm.RefreshAsync();
        Assert.NotSame(old, Assert.Single(vm.Daily.Entries));
        Assert.True(token.IsCancellationRequested);
        pending.SetResult("late review for old card");
        await operation;
        Assert.Empty(old.AiReviewMarkdown);
        Assert.Empty(Assert.Single(vm.Daily.Entries).AiReviewMarkdown);
    }

    [Fact]
    public async Task LanguageChangeCancelsReviewAndUpdatesBilingualLabels()
    {
        var text = Chinese();
        var pending = Pending<string>();
        CancellationToken token = default;
        var reviews = new ReviewStub { Review = (_, _, _, ct) => { token = ct; return pending.Task; } };
        using var vm = new DiscoveryHomeViewModel(new FeedStub(), text, reviews);
        await vm.InitializeAsync();
        var entry = Assert.Single(vm.Weekly.Entries);
        var operation = entry.ReviewCommand.ExecuteAsync(null);
        text.ApplyLanguage("en-US");
        Assert.True(token.IsCancellationRequested);
        pending.SetResult("old language result");
        await operation;
        Assert.Equal("Home", vm.Title);
        Assert.Equal("Weekly stars gained", entry.StarsGainedLabel);
        Assert.Equal("AI commentary", entry.ReviewButtonLabel);
        Assert.Empty(entry.AiReviewMarkdown);
    }

    [Fact]
    public async Task DisposeCancelsBothFeedsAndRejectsLateWritesAndFurtherRefresh()
    {
        var daily = Pending<DiscoveryFeedResult>();
        var weekly = Pending<DiscoveryFeedResult>();
        CancellationToken token = default;
        var feeds = new FeedStub { Get = (kind, _, ct) => { token = ct; return kind == DiscoveryFeedKind.TrendshiftDaily ? daily.Task : weekly.Task; } };
        var vm = new DiscoveryHomeViewModel(feeds, Chinese());
        var initialization = vm.InitializeAsync();
        vm.Dispose();
        Assert.True(token.IsCancellationRequested);
        daily.SetResult(Feed(DiscoveryFeedKind.TrendshiftDaily, "late"));
        weekly.SetResult(Feed(DiscoveryFeedKind.StarHistoryWeekly, "late"));
        await initialization;
        Assert.Empty(vm.Daily.Entries);
        Assert.Empty(vm.Weekly.Entries);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanRefresh);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.RefreshAsync());
    }

    private static LocalizationService Chinese() => new("zh-CN");
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DiscoveryEntry Entry(string name) => new()
    {
        RepositoryFullName = "fixture/" + name, RepositoryUri = new Uri("https://github.com/fixture/" + name),
        SourceUri = new Uri("https://example.com/source/" + name), Rank = 1,
    };
    private static DiscoveryFeedResult Feed(DiscoveryFeedKind kind, string name) => new()
    {
        Kind = kind, SourceName = kind == DiscoveryFeedKind.TrendshiftDaily ? "Trendshift" : "Star History",
        SourceUri = new Uri(kind == DiscoveryFeedKind.TrendshiftDaily ? "https://trendshift.io/" : "https://www.star-history.com/"),
        PeriodLabel = kind == DiscoveryFeedKind.TrendshiftDaily ? "2026-10-22" : "2026-10-14 — 2026-10-20",
        Entries = [Entry(name)], FetchedAt = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero),
    };
    private sealed class FeedStub : IDiscoveryFeedService
    {
        public List<(DiscoveryFeedKind Kind, bool Refresh)> Calls { get; } = [];
        public Func<DiscoveryFeedKind, bool, CancellationToken, Task<DiscoveryFeedResult>> Get { get; set; }
            = (kind, _, _) => Task.FromResult(Feed(kind, kind == DiscoveryFeedKind.TrendshiftDaily ? "daily" : "weekly"));
        public Task<DiscoveryFeedResult> GetAsync(DiscoveryFeedKind kind, bool refresh = false, CancellationToken ct = default)
        { Calls.Add((kind, refresh)); return Get(kind, refresh, ct); }
    }
    private sealed class ReviewStub : IDiscoveryReviewService
    {
        public bool IsConfigured { get; set; } = true;
        public List<(DiscoveryFeedKind Kind, DiscoveryEntry Entry, string Language)> Calls { get; } = [];
        public Func<DiscoveryFeedKind, DiscoveryEntry, string, CancellationToken, Task<string>> Review { get; set; }
            = (_, _, _, _) => Task.FromResult("## AI result");
        public Task<string> ReviewAsync(DiscoveryFeedKind kind, DiscoveryEntry entry, string language, CancellationToken ct = default)
        { Calls.Add((kind, entry, language)); return Review(kind, entry, language, ct); }
    }
}
