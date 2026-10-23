using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Ai;

namespace GitHubStore.App.ViewModels;

public sealed partial class CatalogViewModel
{
    private readonly IReadmeSummaryService? _readmeSummary;
    private CancellationTokenSource? _aiCancellation;
    private ReadmeDocument? _readmeDocument;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAi), nameof(GenerateSummaryLabel))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSummaryCommand), nameof(LoadReadmeCommand))]
    private bool _isAiBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiSummary), nameof(GenerateSummaryLabel))]
    private string _aiSummaryMarkdown = string.Empty;
    [ObservableProperty] private string _readmeMarkdown = string.Empty;
    [ObservableProperty] private Uri? _readmeSourceUri;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiStatus))]
    private string _aiStatus = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiError))]
    private string _aiError = string.Empty;
    public bool HasAiStatus => !string.IsNullOrWhiteSpace(AiStatus);
    public bool HasAiError => !string.IsNullOrWhiteSpace(AiError);
    public bool HasAiSummary => !string.IsNullOrWhiteSpace(AiSummaryMarkdown);
    public bool CanRunAi => !_disposed && SelectedDetail is not null && _readmeSummary is not null && !IsAiBusy;
    public string GenerateSummaryLabel => Text[IsAiBusy ? "AiGenerating" : HasAiSummary ? "AiRegenerate" : "AiGenerate"];

    private void ResetAiState()
    {
        _aiCancellation?.Cancel();
        _aiCancellation = null;
        _readmeDocument = null;
        IsAiBusy = false;
        AiSummaryMarkdown = string.Empty;
        ReadmeMarkdown = string.Empty;
        ReadmeSourceUri = null;
        AiStatus = string.Empty;
        AiError = string.Empty;
        OnPropertyChanged(nameof(CanRunAi));
        OnPropertyChanged(nameof(GenerateSummaryLabel));
        GenerateSummaryCommand.NotifyCanExecuteChanged();
        LoadReadmeCommand.NotifyCanExecuteChanged();
    }

    private async Task RestoreSummaryAsync(AppDetailIdentity identity, CancellationToken ct)
    {
        if (_readmeSummary is null || SelectedDetail is not { } detail) return;
        try
        {
            var language = Text.IsEnglish ? "en-US" : "zh-CN";
            var cached = await _readmeSummary.GetCachedAsync(detail.App, language, ct);
            if (!_disposed && !ct.IsCancellationRequested && SelectedDetail?.App.Id == identity.Id && cached is not null
                && !IsAiBusy && !HasAiSummary && language == (Text.IsEnglish ? "en-US" : "zh-CN"))
                ShowSummary(cached);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { /* Cache restoration is optional and must not block catalog browsing. */ }
    }

    private void ShowSummary(AiReadmeSummary summary)
    {
        AiSummaryMarkdown = summary.Markdown;
        AiStatus = $"{(summary.FromCache ? Text["AiSummaryCached"] : Text["AiSummaryTitle"])} · {summary.Model} · {summary.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        if (summary.InputTruncated)
            AiStatus += Text.IsEnglish ? " · README excerpt" : " · README 已截取前段";
    }

    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private Task GenerateSummaryAsync(CancellationToken ct) => RunAiAsync(generate: true, ct);

    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private Task LoadReadmeAsync(CancellationToken ct) => RunAiAsync(generate: false, ct);

    [RelayCommand]
    private void CancelAi() => _aiCancellation?.Cancel();

    private async Task RunAiAsync(bool generate, CancellationToken ct)
    {
        if (!CanRunAi || SelectedDetail is not { } detail || _readmeSummary is null) return;
        if (generate && !_readmeSummary.IsConfigured)
        {
            AiError = Text["AiNotConfigured"];
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _aiCancellation = cancellation;
        var token = cancellation.Token;
        var language = Text.IsEnglish ? "en-US" : "zh-CN";
        var force = HasAiSummary;
        IsAiBusy = true;
        AiError = string.Empty;
        AiStatus = generate ? Text["AiGenerating"] : Text["AiReadmeLoad"];
        try
        {
            var readme = _readmeDocument ?? await _readmeSummary.LoadReadmeAsync(detail.App, token);
            if (!IsCurrent()) return;
            _readmeDocument = readme;
            ReadmeSourceUri = readme.SourceUrl;
            ReadmeMarkdown = readme.Markdown;
            if (generate)
            {
                var summary = await _readmeSummary.GenerateAsync(detail.App, readme, language, force, token);
                if (IsCurrent()) ShowSummary(summary);
            }
            else AiStatus = string.Empty;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ReferenceEquals(_aiCancellation, cancellation)) AiStatus = Text.IsEnglish ? "Cancelled" : "已取消";
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                AiStatus = string.Empty;
                AiError = AiFailureText(ex, generate);
            }
        }
        finally
        {
            if (ReferenceEquals(_aiCancellation, cancellation))
            {
                _aiCancellation = null;
                IsAiBusy = false;
            }
        }
        bool IsCurrent() => !_disposed && !token.IsCancellationRequested
            && ReferenceEquals(_aiCancellation, cancellation) && SelectedDetail?.App.Id == detail.App.Id;
    }

    private string AiFailureText(Exception exception, bool generate)
    {
        var english = Text.IsEnglish;
        return (exception as AiSummaryException)?.Code switch
        {
            "ai_not_configured" => Text["AiNotConfigured"],
            "ai_auth_failed" => english ? "AI authentication failed. Check your API key and endpoint." : "AI 身份验证失败，请检查 API 密钥和接口地址。",
            "rate_limited" => english ? "The service is rate limited or its quota is exhausted. Try again later." : "服务限流或额度不足，请稍后重试。",
            "readme_not_found" => english ? "This repository has no accessible README." : "该仓库没有可读取的 README。",
            "readme_failed" or "readme_invalid" => Text["AiReadmeFailed"],
            "request_timeout" => english ? "The request timed out. Please retry." : "请求超时，请重试。",
            "network_error" => english ? "Unable to connect. Check your network and proxy settings." : "无法连接服务，请检查网络及代理设置。",
            "ai_incomplete" => english ? "The model response was incomplete. Try another model or regenerate." : "模型回复不完整，请更换模型或重新生成。",
            "response_too_large" => english ? "The response exceeds the supported size." : "服务返回内容过大，超出可处理范围。",
            _ => Text[generate ? "AiSummaryFailed" : "AiReadmeFailed"],
        };
    }

    // Capture the selection identity across an asynchronous cache read without retaining visual objects.
    private readonly record struct AppDetailIdentity(GitHubStore.Core.Abstractions.AppId Id);
}
