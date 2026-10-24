using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Ai;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

public sealed partial class CatalogViewModel
{
    private readonly IAiAssistantService? _assistant;
    private CancellationTokenSource? _assistantSearchCancellation;
    private CancellationTokenSource? _assistantDetailCancellation;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearchWithAi))]
    [NotifyCanExecuteChangedFor(nameof(SearchWithAiCommand))]
    private bool _isAssistantSearchBusy;
    [ObservableProperty] private string _assistantSearchStatus = string.Empty;
    [ObservableProperty] private string _assistantSearchError = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExplainAsset), nameof(CanExplainRelease))]
    [NotifyCanExecuteChangedFor(nameof(ExplainAssetCommand), nameof(ExplainReleaseCommand))]
    private bool _isAssistantDetailBusy;
    [ObservableProperty] private string _assistantDetailStatus = string.Empty;
    [ObservableProperty] private string _assistantDetailError = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssetExplanation))]
    private string _assetExplanationMarkdown = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleaseImpact))]
    private string _releaseImpactMarkdown = string.Empty;
    public bool HasAssetExplanation => !string.IsNullOrWhiteSpace(AssetExplanationMarkdown);
    public bool HasReleaseImpact => !string.IsNullOrWhiteSpace(ReleaseImpactMarkdown);

    public bool CanSearchWithAi => !_disposed && _assistant is not null && !IsWorking && !IsAssistantSearchBusy
        && !string.IsNullOrWhiteSpace(SearchText);
    public bool CanExplainAsset => !_disposed && _assistant is not null && !IsAssistantDetailBusy
        && SelectedDetail is not null && SelectedAsset is not null;
    public bool CanExplainRelease => !_disposed && _assistant is not null && !IsAssistantDetailBusy
        && LatestRelease is not null;
    public string SearchWithAiLabel => Text.IsEnglish ? "AI search" : "AI 搜索";
    public string SearchWithAiHint => Text.IsEnglish ? "Describe what you need, then click AI search to search the full catalog."
        : "描述你需要的功能，再点击 AI 搜索，查找整个软件库。";
    public string ExplainAssetLabel => Text.IsEnglish ? "Explain selected package" : "AI 解释所选安装包";
    public string ExplainReleaseLabel => Text.IsEnglish ? "Explain release impact" : "AI 解读版本变化";
    public string AssetExplanationLabel => Text.IsEnglish ? "AI package explanation" : "AI 安装包说明";
    public string ReleaseImpactLabel => Text.IsEnglish ? "AI release explanation" : "AI 版本变化说明";
    public string CancelAssistantLabel => Text.IsEnglish ? "Cancel" : "取消";

    partial void OnSearchTextChanged(string value)
    {
        ResetAssistantSearch();
        OnPropertyChanged(nameof(CanSearchWithAi));
        SearchWithAiCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) => NotifyAssistantSearchCommand();
    partial void OnIsRefreshingChanged(bool value)
    {
        if (value) ResetAssistantSearch();
        NotifyAssistantSearchCommand();
    }
    partial void OnSelectedAssetChanged(ReleaseAssetViewModel? value) => ResetAssistantDetail();

    private void NotifyAssistantSearchCommand()
    {
        OnPropertyChanged(nameof(CanSearchWithAi));
        SearchWithAiCommand.NotifyCanExecuteChanged();
    }

    private void ResetAssistantSearch()
    {
        var previous = _assistantSearchCancellation;
        _assistantSearchCancellation = null;
        _ = CancelAssistantOperationAsync(previous);
        IsAssistantSearchBusy = false;
        AssistantSearchStatus = string.Empty;
        AssistantSearchError = string.Empty;
    }

    private void ResetAssistantDetail()
    {
        var previous = _assistantDetailCancellation;
        _assistantDetailCancellation = null;
        _ = CancelAssistantOperationAsync(previous);
        IsAssistantDetailBusy = false;
        AssetExplanationMarkdown = string.Empty;
        ReleaseImpactMarkdown = string.Empty;
        AssistantDetailStatus = string.Empty;
        AssistantDetailError = string.Empty;
        OnPropertyChanged(nameof(CanExplainAsset));
        OnPropertyChanged(nameof(CanExplainRelease));
        ExplainAssetCommand.NotifyCanExecuteChanged();
        ExplainReleaseCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAssistantLabels()
    {
        foreach (var name in new[] { nameof(SearchWithAiLabel), nameof(SearchWithAiHint), nameof(ExplainAssetLabel),
            nameof(ExplainReleaseLabel), nameof(AssetExplanationLabel), nameof(ReleaseImpactLabel), nameof(CancelAssistantLabel) })
            OnPropertyChanged(name);
    }

    [RelayCommand(CanExecute = nameof(CanSearchWithAi), AllowConcurrentExecutions = true)]
    private async Task SearchWithAiAsync(CancellationToken ct)
    {
        if (!CanSearchWithAi || _assistant is null) return;
        if (!_assistant.IsConfigured) { AssistantSearchError = Text["AiNotConfigured"]; return; }
        CancelCards();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _assistantSearchCancellation = cancellation;
        var query = SearchText.Trim();
        _ranking = false;
        SelectedCategory = null;
        UpdateTitle();
        var version = ++_loadVersion;
        var language = Text.IsEnglish ? "en-US" : "zh-CN";
        var selectedId = SelectedDetail?.App.Id;
        IsAssistantSearchBusy = true;
        AssistantSearchError = string.Empty;
        AssistantSearchStatus = Text.IsEnglish ? "Finding matching software…" : "正在查找符合需求的软件…";
        try
        {
            var results = await _assistant.SearchAsync(query, language, cancellation.Token);
            if (!IsCurrent()) return;
            var items = results.DistinctBy(app => app.Id).ToArray();
            Apps.Clear();
            foreach (var app in items) Apps.Add(app);
            _searchResults = true;
            _total = items.Length;
            HasMore = false;
            UpdateStatus();
            await RefreshCardsAsync(cancellation.Token);
            if (!IsCurrent()) return;
            AssistantSearchStatus = Text.IsEnglish ? $"AI search: {items.Length} matching apps. Select an app to continue."
                : $"AI 搜索找到 {items.Length} 款软件，点击软件可继续操作。";
            await SelectAsync(items.FirstOrDefault(app => app.Id == selectedId) ?? items.FirstOrDefault(), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_assistantSearchCancellation, cancellation) && !_disposed)
                AssistantSearchStatus = Text.IsEnglish ? "Search cancelled." : "搜索已取消。";
        }
        catch (Exception ex)
        {
            if (IsCurrent()) { AssistantSearchStatus = string.Empty; AssistantSearchError = AssistantFailureText(ex); }
        }
        finally
        {
            if (ReferenceEquals(_assistantSearchCancellation, cancellation))
            { _assistantSearchCancellation = null; IsAssistantSearchBusy = false; }
        }
        bool IsCurrent() => !_disposed && !cancellation.IsCancellationRequested && version == _loadVersion
            && ReferenceEquals(_assistantSearchCancellation, cancellation) && SearchText.Trim() == query
            && SelectedCategory is null && language == (Text.IsEnglish ? "en-US" : "zh-CN");
    }

    private string AssistantFailureText(Exception exception)
    {
        if (exception is AiSummaryException { Code: "ai_not_configured" or "ai_auth_failed" or "rate_limited"
            or "request_timeout" or "network_error" or "ai_incomplete" or "response_too_large" })
            return AiFailureText(exception, true);
        return Text.IsEnglish ? "AI could not complete this request. Please retry."
            : "AI 未能完成本次请求，请重试。";
    }

    // Cancellation callbacks belong to services and must not run on the UI transition or escape it.
    private static async Task CancelAssistantOperationAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* The operation is already detached; callback/disposal failures cannot alter UI state. */ }
    }

    [RelayCommand]
    private void CancelAssistantSearch()
    {
        if (_disposed || _assistantSearchCancellation is null) return;
        ResetAssistantSearch();
        AssistantSearchStatus = Text.IsEnglish ? "Search cancelled." : "搜索已取消。";
    }
    [RelayCommand]
    private void CancelAssistantDetail()
    {
        if (_disposed || _assistantDetailCancellation is null) return;
        ResetAssistantDetail();
        AssistantDetailStatus = Text.IsEnglish ? "Cancelled." : "已取消。";
    }
    [RelayCommand(CanExecute = nameof(CanExplainAsset), AllowConcurrentExecutions = true)]
    private Task ExplainAssetAsync(CancellationToken ct) => ExplainAsync(AiAssistantTask.AssetExplanation, ct);
    [RelayCommand(CanExecute = nameof(CanExplainRelease), AllowConcurrentExecutions = true)]
    private Task ExplainReleaseAsync(CancellationToken ct) => ExplainAsync(AiAssistantTask.ReleaseImpact, ct);

    private async Task ExplainAsync(AiAssistantTask task, CancellationToken ct)
    {
        if (_assistant is null || SelectedDetail is not { } detail || LatestRelease is not { } release
            || IsAssistantDetailBusy || _disposed || (task == AiAssistantTask.AssetExplanation && SelectedAsset is null)) return;
        if (!_assistant.IsConfigured) { AssistantDetailError = Text["AiNotConfigured"]; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _assistantDetailCancellation = cancellation;
        var asset = SelectedAsset?.Asset;
        var language = Text.IsEnglish ? "en-US" : "zh-CN";
        IsAssistantDetailBusy = true;
        AssistantDetailError = string.Empty;
        AssistantDetailStatus = Text.IsEnglish ? "Generating explanation…" : "正在生成说明…";
        try
        {
            var request = new AiAssistantRequest(task)
            {
                App = detail.App, Asset = asset, Release = release, PackagePolicy = detail.PackagePolicy,
                Context = $"System architecture: {SystemArchitecture}. Default package recommendation: {AssetRecommendation}. "
                    + "Current application supports first installation of ZIP, portable EXE and interactive EXE/MSI; 7z extraction is unsupported. "
                    + (task == AiAssistantTask.ReleaseImpact
                        ? "This view supplies no installed baseline. Use only actual installed-version facts attached by the service; if absent, explain target release notes without claiming a version comparison. Do not claim an upgrade, rollback or compatibility verification has occurred."
                        : "Explain the selected asset using the supplied metadata and release notes. Package recommendation is not proof of compatibility or security verification."),
            };
            var markdown = await _assistant.AnalyzeAsync(request, language, cancellation.Token);
            if (!IsCurrent()) return;
            if (task == AiAssistantTask.AssetExplanation) AssetExplanationMarkdown = markdown;
            else ReleaseImpactMarkdown = markdown;
            AssistantDetailStatus = Text.IsEnglish ? "AI explanation based on the supplied software information."
                : "AI 说明基于本次提供的软件资料。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_assistantDetailCancellation, cancellation) && !_disposed)
                AssistantDetailStatus = Text.IsEnglish ? "Cancelled." : "已取消。";
        }
        catch (Exception ex)
        {
            if (IsCurrent()) { AssistantDetailStatus = string.Empty; AssistantDetailError = AssistantFailureText(ex); }
        }
        finally
        {
            if (ReferenceEquals(_assistantDetailCancellation, cancellation))
            { _assistantDetailCancellation = null; IsAssistantDetailBusy = false; }
        }
        bool IsCurrent() => !_disposed && !cancellation.IsCancellationRequested
            && ReferenceEquals(_assistantDetailCancellation, cancellation) && ReferenceEquals(SelectedDetail, detail)
            && ReferenceEquals(SelectedAsset?.Asset, asset) && language == (Text.IsEnglish ? "en-US" : "zh-CN");
    }
}
