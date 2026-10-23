using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>Independent source feeds; only explicit review commands invoke a model.</summary>
public sealed partial class DiscoveryHomeViewModel : ObservableObject, IDisposable
{
    private readonly IDiscoveryFeedService _feeds;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task? _initialization;
    private bool _disposed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public DiscoveryHomeViewModel(IDiscoveryFeedService feeds, LocalizationService text, IDiscoveryReviewService? reviews = null)
    {
        _feeds = feeds;
        Text = text;
        Daily = new(DiscoveryFeedKind.TrendshiftDaily, text, reviews);
        Weekly = new(DiscoveryFeedKind.StarHistoryWeekly, text, reviews);
        Text.LanguageChanged += OnLanguageChanged;
    }
    public LocalizationService Text { get; }
    public DiscoverySectionViewModel Daily { get; }
    public DiscoverySectionViewModel Weekly { get; }
    public bool CanRefresh => !_disposed && !IsBusy;
    public string Title => Text.IsEnglish ? "Home" : "首页";
    public string Subtitle => Text.IsEnglish ? "Daily discoveries · Weekly star growth" : "每日发现 · 每周 Star 增长";
    public string RefreshLabel => Text.IsEnglish ? "Refresh" : "刷新";
    public string CancelLabel => Text.IsEnglish ? "Cancel refresh" : "取消刷新";

    public Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _initialization ??= LoadAsync(false, ct);
    }
    [RelayCommand(CanExecute = nameof(CanRefresh), AllowConcurrentExecutions = true)]
    public Task RefreshAsync(CancellationToken ct = default) => LoadAsync(true, ct);

    private async Task LoadAsync(bool refresh, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _operation;
        _operation = null;
        _ = CancelSafelyAsync(previous);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _operation = operation;
        IsBusy = true;
        StatusMessage = string.Empty;
        Daily.BeginLoad();
        Weekly.BeginLoad();
        try
        {
            await Task.WhenAll(LoadSectionAsync(Daily), LoadSectionAsync(Weekly));
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                IsBusy = false;
                Daily.EndLoad();
                Weekly.EndLoad();
                if (operation.IsCancellationRequested && !_disposed)
                    StatusMessage = Text.IsEnglish ? "Refresh cancelled." : "刷新已取消。";
            }
        }
        async Task LoadSectionAsync(DiscoverySectionViewModel section)
        {
            try
            {
                var result = await _feeds.GetAsync(section.Kind, refresh, operation.Token);
                if (!IsCurrent()) return;
                if (result.Kind != section.Kind) throw new InvalidDataException("discovery_kind_mismatch");
                section.Apply(result);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (Exception) { if (IsCurrent()) section.Fail(); }
            finally { if (IsCurrent()) section.EndLoad(); }
        }
        bool IsCurrent() => !_disposed && !operation.IsCancellationRequested && ReferenceEquals(_operation, operation);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_disposed || _operation is null) return;
        DetachOperation();
        StatusMessage = Text.IsEnglish ? "Refresh cancelled." : "刷新已取消。";
    }
    private void DetachOperation()
    {
        var previous = _operation;
        _operation = null;
        _ = CancelSafelyAsync(previous);
        IsBusy = false;
        Daily.EndLoad();
        Weekly.EndLoad();
    }
    internal static async Task CancelSafelyAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* Source/provider callbacks cannot escape visual transitions. */ }
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(RefreshLabel), nameof(CancelLabel) }) OnPropertyChanged(name);
        StatusMessage = string.Empty;
        Daily.NotifyLanguageChanged();
        Weekly.NotifyLanguageChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Text.LanguageChanged -= OnLanguageChanged;
        DetachOperation();
        Daily.Dispose();
        Weekly.Dispose();
        _ = CancelSafelyAsync(_lifetime);
    }
}

public sealed partial class DiscoverySectionViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _text;
    private readonly IDiscoveryReviewService? _reviews;
    private DiscoveryFeedResult? _snapshot;
    private bool _failed;
    private bool _retained;
    private bool _disposed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    private bool _isBusy;
    public DiscoverySectionViewModel(DiscoveryFeedKind kind, LocalizationService text, IDiscoveryReviewService? reviews)
    { Kind = kind; _text = text; _reviews = reviews; }
    public DiscoveryFeedKind Kind { get; }
    public ObservableCollection<DiscoveryEntryViewModel> Entries { get; } = [];
    public string Title => Kind == DiscoveryFeedKind.TrendshiftDaily
        ? _text.IsEnglish ? "Daily discoveries" : "每日发现"
        : _text.IsEnglish ? "Weekly stars" : "每周 Star";
    public string SourceName => string.IsNullOrWhiteSpace(_snapshot?.SourceName)
        ? Kind == DiscoveryFeedKind.TrendshiftDaily ? "Trendshift" : "Star History" : _snapshot.SourceName;
    public Uri SourceUri => DiscoveryEntryViewModel.SafeUri(_snapshot?.SourceUri)
        ?? new Uri(Kind == DiscoveryFeedKind.TrendshiftDaily ? "https://trendshift.io/" : "https://www.star-history.com/");
    public string SourceLabel => _text.IsEnglish ? $"Source: {SourceName}" : $"来源：{SourceName}";
    public string OpenSourceLabel => _text.IsEnglish ? "Open source" : "打开源站";
    public string PeriodText
    {
        get
        {
            var prefix = _text.IsEnglish ? "Source period: " : "源站统计周期：";
            if (!string.IsNullOrWhiteSpace(_snapshot?.PeriodLabel)) return prefix + _snapshot.PeriodLabel;
            if (_snapshot?.PeriodStart is { } start)
                return prefix + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + (_snapshot.PeriodEnd is { } end && end != start ? " — " + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty);
            return prefix + (_text.IsEnglish ? "not provided" : "未提供");
        }
    }
    public string CacheStatus => _retained ? (_text.IsEnglish ? "Previous results retained; source refresh failed." : "来源刷新失败，保留上次结果。")
        : _snapshot is null ? string.Empty
        : _snapshot.IsStale ? (_text.IsEnglish ? "Cached source data · Out of date" : "来源缓存 · 数据已过期")
        : _snapshot.FromCache ? (_text.IsEnglish ? "Cached source data" : "来源缓存") : string.Empty;
    public bool HasCacheStatus => !string.IsNullOrWhiteSpace(CacheStatus);
    public string ErrorMessage => !_failed && string.IsNullOrWhiteSpace(_snapshot?.ErrorCode) ? string.Empty
        : _text.IsEnglish ? $"Could not refresh {SourceName}. Retry or open the source website."
        : $"未能刷新 {SourceName}，请重试或打开源站查看。";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasEmptyState => !IsBusy && Entries.Count == 0;
    public string EmptyText => HasError ? (_text.IsEnglish ? "No source results are available." : "暂无可显示的来源数据。")
        : _text.IsEnglish ? "The source has no projects to show for this period." : "源站本期暂无可显示的项目。";

    internal void BeginLoad() { IsBusy = true; _failed = false; NotifyState(); }
    internal void EndLoad() => IsBusy = false;
    internal void Apply(DiscoveryFeedResult result)
    {
        var retain = result.Entries.Count == 0 && !string.IsNullOrWhiteSpace(result.ErrorCode) && Entries.Count > 0;
        if (!retain)
        {
            foreach (var entry in Entries) entry.Dispose();
            Entries.Clear();
            foreach (var entry in result.Entries.DistinctBy(entry => entry.RepositoryFullName, StringComparer.OrdinalIgnoreCase).Take(50))
                Entries.Add(new(Kind, entry, _text, _reviews));
            _snapshot = result;
        }
        _failed = !string.IsNullOrWhiteSpace(result.ErrorCode);
        _retained = retain;
        NotifyState();
    }
    internal void Fail()
    {
        _failed = true;
        _retained = Entries.Count > 0;
        NotifyState();
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(SourceName), nameof(SourceUri), nameof(SourceLabel), nameof(PeriodText), nameof(CacheStatus),
            nameof(HasCacheStatus), nameof(ErrorMessage), nameof(HasError), nameof(HasEmptyState), nameof(EmptyText) }) OnPropertyChanged(name);
    }
    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(OpenSourceLabel));
        NotifyState();
        foreach (var entry in Entries) entry.NotifyLanguageChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in Entries) entry.Dispose();
    }
}

public sealed partial class DiscoveryEntryViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _text;
    private readonly IDiscoveryReviewService? _reviews;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reviewCancellation;
    private bool _disposed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReview), nameof(ReviewButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    private bool _isReviewBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiReview))]
    private string _aiReviewMarkdown = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewError))]
    private string _reviewError = string.Empty;
    public DiscoveryEntryViewModel(DiscoveryFeedKind kind, DiscoveryEntry entry, LocalizationService text, IDiscoveryReviewService? reviews)
    { Kind = kind; Entry = entry; _text = text; _reviews = reviews; }
    public DiscoveryFeedKind Kind { get; }
    public DiscoveryEntry Entry { get; }
    public string RankText => Entry.Rank > 0 ? $"#{Entry.Rank}" : "—";
    public string RepositoryName => Entry.RepositoryFullName;
    public Uri? RepositoryUri => SafeUri(Entry.RepositoryUri);
    public Uri? SourceUri => SafeUri(Entry.SourceUri);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);
    public bool ShowDescription => HasDescription || Kind == DiscoveryFeedKind.TrendshiftDaily;
    public string Description => string.IsNullOrWhiteSpace(Entry.Description)
        ? _text.IsEnglish ? "The source did not provide a description." : "源站未提供项目说明。" : Entry.Description;
    public string Language => Entry.Language ?? string.Empty;
    public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);
    public bool HasTotalStars => Entry.TotalStars.HasValue;
    public bool HasStarsGained => Entry.StarsGained.HasValue;
    public bool HasTrendScore => Entry.TrendScore.HasValue;
    public string TotalStarsText => Entry.TotalStars?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    public string StarsGainedText => Entry.StarsGained is { } value ? value.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture) : "—";
    public string TrendScoreText => Entry.TrendScore?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";
    public string TotalStarsLabel => _text.IsEnglish ? "Total stars" : "累计 Star";
    public string StarsGainedLabel => Kind == DiscoveryFeedKind.StarHistoryWeekly
        ? _text.IsEnglish ? "Weekly stars gained" : "本周新增 Star"
        : _text.IsEnglish ? "Daily stars gained" : "当日新增 Star";
    public string TrendScoreLabel => _text.IsEnglish ? "Trend score" : "趋势评分";
    public bool HasSourceReview => !string.IsNullOrWhiteSpace(Entry.SourceReview);
    public bool HasNoSourceReview => !HasSourceReview;
    public bool ShowNoSourceReviewNotice => Kind == DiscoveryFeedKind.TrendshiftDaily && HasNoSourceReview;
    public string SourceReview => Entry.SourceReview ?? string.Empty;
    public string SourceReviewLabel => _text.IsEnglish ? "Mention / review collected by the source" : "源站收录的提及 / 评价";
    public string SourceReviewAttribution => HasSourceReview ? Entry.ReviewAttribution ?? string.Empty : string.Empty;
    public Uri? ReviewUri => HasSourceReview ? SafeUri(Entry.ReviewUri) : null;
    public bool HasReviewLink => ReviewUri is not null;
    public string ReviewLinkLabel => _text.IsEnglish ? "Original mention / review" : "查看原始提及 / 评价";
    public bool HasReviewPublishedAt => HasSourceReview && Entry.ReviewPublishedAt.HasValue;
    public string ReviewPublishedText => HasReviewPublishedAt
        ? Entry.ReviewPublishedAt!.Value.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) : string.Empty;
    public string NoSourceReviewLabel => _text.IsEnglish ? "The source provides no written review." : "源站未提供文字评价";
    public string OpenRepositoryLabel => _text.IsEnglish ? "Repository" : "打开仓库";
    public string OpenSourceLabel => _text.IsEnglish ? "Source details" : "来源详情";
    public string ReviewButtonLabel => IsReviewBusy ? (_text.IsEnglish ? "Writing commentary…" : "正在生成点评…")
        : _text.IsEnglish ? "AI commentary" : "AI 点评";
    public string CancelReviewLabel => _text.IsEnglish ? "Cancel" : "取消";
    public string AiReviewLabel => _text.IsEnglish ? "AI commentary · Generated on request" : "AI 点评 · 按需生成";
    public bool HasAiReview => !string.IsNullOrWhiteSpace(AiReviewMarkdown);
    public bool HasReviewError => !string.IsNullOrWhiteSpace(ReviewError);
    public bool CanReview => !_disposed && !IsReviewBusy && _reviews is not null;

    internal static Uri? SafeUri(Uri? value) => value is { IsAbsoluteUri: true, Scheme: "https" } && string.IsNullOrEmpty(value.UserInfo) ? value : null;
    [RelayCommand(CanExecute = nameof(CanReview), AllowConcurrentExecutions = true)]
    private async Task ReviewAsync(CancellationToken ct)
    {
        if (!CanReview || _reviews is null) return;
        if (!_reviews.IsConfigured)
        { ReviewError = _text.IsEnglish ? "Configure an AI endpoint and model in Settings first." : "请先在设置中配置 AI 接口和模型。"; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _reviewCancellation = cancellation;
        var language = _text.IsEnglish ? "en-US" : "zh-CN";
        IsReviewBusy = true;
        ReviewError = string.Empty;
        try
        {
            var markdown = await _reviews.ReviewAsync(Kind, Entry, language, cancellation.Token);
            if (IsCurrent()) AiReviewMarkdown = markdown;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        { if (IsCurrent()) ReviewError = _text.IsEnglish ? "AI commentary failed. Check AI settings or retry." : "AI 点评失败，请检查 AI 设置或重试。"; }
        finally
        {
            if (ReferenceEquals(_reviewCancellation, cancellation))
            { _reviewCancellation = null; IsReviewBusy = false; }
        }
        bool IsCurrent() => !_disposed && !cancellation.IsCancellationRequested && ReferenceEquals(_reviewCancellation, cancellation)
            && language == (_text.IsEnglish ? "en-US" : "zh-CN");
    }
    [RelayCommand]
    private void CancelReview()
    {
        var previous = _reviewCancellation;
        _reviewCancellation = null;
        IsReviewBusy = false;
        _ = DiscoveryHomeViewModel.CancelSafelyAsync(previous);
    }
    internal void NotifyLanguageChanged()
    {
        CancelReview();
        AiReviewMarkdown = string.Empty;
        ReviewError = string.Empty;
        foreach (var name in new[] { nameof(Description), nameof(TotalStarsLabel), nameof(StarsGainedLabel), nameof(TrendScoreLabel), nameof(SourceReviewLabel),
            nameof(NoSourceReviewLabel), nameof(ReviewLinkLabel), nameof(OpenRepositoryLabel), nameof(OpenSourceLabel), nameof(ReviewButtonLabel), nameof(CancelReviewLabel), nameof(AiReviewLabel) })
            OnPropertyChanged(name);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelReview();
        _ = DiscoveryHomeViewModel.CancelSafelyAsync(_lifetime);
        OnPropertyChanged(nameof(CanReview));
        ReviewCommand.NotifyCanExecuteChanged();
    }
}
