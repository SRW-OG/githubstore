using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Ai;

namespace GitHubStore.App.ViewModels;

public sealed partial class CatalogViewModel
{
    private readonly IChineseReadmeService? _chineseReadme;
    private CancellationTokenSource? _chineseReadmeCancellation;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateChineseReadme))]
    [NotifyCanExecuteChangedFor(nameof(GenerateChineseReadmeCommand))]
    private bool _isChineseReadmeBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayReadmeMarkdown), nameof(HasChineseReadme))]
    private string _chineseReadmeMarkdown = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayReadmeMarkdown), nameof(HasOriginalReadme))]
    private string _originalReadmeMarkdown = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayReadmeMarkdown))]
    private bool _showOriginalReadme;
    [ObservableProperty] private string _chineseReadmeStatus = string.Empty;
    [ObservableProperty] private string _chineseReadmeError = string.Empty;
    [ObservableProperty] private Uri? _chineseReadmeSourceUri;
    public bool HasChineseReadme => !string.IsNullOrWhiteSpace(ChineseReadmeMarkdown);
    public bool HasOriginalReadme => !string.IsNullOrWhiteSpace(OriginalReadmeMarkdown);
    public bool CanGenerateChineseReadme => !_disposed && !IsChineseReadmeBusy && SelectedDetail is not null && _chineseReadme is not null;
    public string DisplayReadmeMarkdown => ShowOriginalReadme ? OriginalReadmeMarkdown : ChineseReadmeMarkdown;
    public string ChineseReadmeTitle => Text.IsEnglish ? "Chinese README" : "中文 README";
    public string ChineseReadmeLoadLabel => Text.IsEnglish ? "Regenerate Chinese translation" : "重新生成中文译文";
    public string ChineseReadmeOriginalLabel => Text.IsEnglish ? "Show original" : "查看原文";
    public string ChineseReadmeCancelLabel => Text.IsEnglish ? "Cancel translation" : "取消翻译";
    public string ChineseReadmeCloseLabel => Text.IsEnglish ? "Close" : "关闭";
    public IRelayCommand CancelChineseReadmeCommand => CancelChineseReadmeRequestCommand;

    private void ResetChineseReadmeState()
    {
        CancelChineseReadme();
        ChineseReadmeMarkdown = string.Empty;
        OriginalReadmeMarkdown = string.Empty;
        ChineseReadmeSourceUri = null;
        ShowOriginalReadme = false;
        ChineseReadmeStatus = string.Empty;
        ChineseReadmeError = string.Empty;
        OnPropertyChanged(nameof(CanGenerateChineseReadme));
        GenerateChineseReadmeCommand.NotifyCanExecuteChanged();
    }

    public void CancelChineseReadme()
    {
        var previous = _chineseReadmeCancellation;
        _chineseReadmeCancellation = null;
        IsChineseReadmeBusy = false;
        if (previous is not null) _ = CancelChineseReadmeSafelyAsync(previous);
    }

    private static async Task CancelChineseReadmeSafelyAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        catch (Exception) { /* Cancellation callbacks cannot escape UI transitions. */ }
    }

    [RelayCommand(CanExecute = nameof(CanGenerateChineseReadme), AllowConcurrentExecutions = true)]
    private Task GenerateChineseReadmeAsync(CancellationToken ct) => LoadChineseReadmeForSelectedAsync(true, ct);

    [RelayCommand]
    private void CancelChineseReadmeRequest()
    {
        CancelChineseReadme();
        ChineseReadmeStatus = Text.IsEnglish ? "Translation cancelled." : "翻译已取消。";
    }

    public async Task LoadChineseReadmeForSelectedAsync(bool force = false, CancellationToken ct = default)
    {
        if (_disposed || SelectedDetail is not { } detail) return;
        CancelChineseReadme();
        if (_chineseReadme is null)
        {
            ChineseReadmeError = Text.IsEnglish ? "The Chinese README service is unavailable." : "中文 README 服务暂不可用。";
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _chineseReadmeCancellation = cancellation;
        IsChineseReadmeBusy = true;
        ChineseReadmeError = string.Empty;
        ChineseReadmeStatus = Text.IsEnglish ? "Loading README and Chinese translation…" : "正在加载 README 与中文译文…";
        try
        {
            if (!force && await _chineseReadme.GetCachedAsync(detail.App, cancellation.Token) is { } cached)
            {
                if (IsCurrent()) Display(cached);
                if (cached.IsTranslated || !_chineseReadme.IsConfigured) return;
            }
            if (_readmeSummary is not null)
            {
                var original = await _readmeSummary.LoadReadmeAsync(detail.App, cancellation.Token);
                if (!IsCurrent()) return;
                OriginalReadmeMarkdown = original.Markdown;
                ChineseReadmeSourceUri = original.SourceUrl;
            }
            var progress = new Progress<ChineseReadmeProgress>(update =>
            {
                if (IsCurrent()) ChineseReadmeStatus = Text.IsEnglish
                    ? $"Translating README: {update.CompletedChunks} / {update.TotalChunks}"
                    : $"正在翻译 README：{update.CompletedChunks} / {update.TotalChunks}";
            });
            var result = await _chineseReadme.LoadAsync(detail.App, force, progress, cancellation.Token);
            if (IsCurrent()) Display(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                ChineseReadmeStatus = string.Empty;
                ChineseReadmeError = ex is AiSummaryException { Code: "readme_not_found" }
                    ? (Text.IsEnglish ? "This repository has no accessible README." : "该仓库没有可读取的 README。")
                    : ex is AiSummaryException { Code: "ai_not_configured" } ? Text["AiNotConfigured"]
                    : Text.IsEnglish ? "Could not load the Chinese README. Check the network and AI settings, then retry."
                    : "中文 README 加载失败，请检查网络和 AI 设置后重试。";
            }
        }
        finally
        {
            if (ReferenceEquals(_chineseReadmeCancellation, cancellation))
            {
                _chineseReadmeCancellation = null;
                IsChineseReadmeBusy = false;
            }
        }
        bool IsCurrent() => !_disposed && !cancellation.IsCancellationRequested
            && ReferenceEquals(_chineseReadmeCancellation, cancellation) && ReferenceEquals(SelectedDetail, detail);
        void Display(ChineseReadmeDocument document)
        {
            OriginalReadmeMarkdown = document.OriginalMarkdown;
            ChineseReadmeMarkdown = document.IsTranslated ? document.Markdown : string.Empty;
            ShowOriginalReadme = !document.IsTranslated;
            ChineseReadmeSourceUri = document.SourceUrl;
            ChineseReadmeStatus = document.Notice ?? (document.IsTranslated
                ? Text.IsEnglish ? "AI Chinese translation. Check the original for exact details." : "AI 中文译文，具体内容请核对原文。"
                : Text.IsEnglish ? "Original README. Configure AI to translate." : "当前显示原文，请配置 AI 后生成中文译文。");
            if (document.FromCache) ChineseReadmeStatus += Text.IsEnglish ? " · Cached" : " · 来自缓存";
            if (document.InputTruncated) ChineseReadmeStatus += Text.IsEnglish ? " · Excerpt only" : " · 仅翻译已提供的片段";
        }
    }

    private void NotifyChineseReadmeLabels()
    {
        foreach (var name in new[] { nameof(ChineseReadmeTitle), nameof(ChineseReadmeLoadLabel),
            nameof(ChineseReadmeOriginalLabel), nameof(ChineseReadmeCancelLabel), nameof(ChineseReadmeCloseLabel) })
            OnPropertyChanged(name);
    }
}
