using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Ai;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

public sealed partial class InstalledViewModel
{
    private readonly IAiAssistantService? _aiAssistant;
    private CancellationTokenSource? _aiCancellation;
    private int _aiVersion;
    private string? _aiErrorCode;
    private bool _aiCancelled;

    [ObservableProperty] private string _aiQuestion = string.Empty;
    [ObservableProperty] private string _aiResponse = string.Empty;
    [ObservableProperty] private string _aiResultSubject = string.Empty;
    [ObservableProperty] private bool _isAiBusy;

    public string AiHeading => AiLabel("已安装应用 AI 助手", "Installed-app AI assistant");
    public string AiQuestionHint => AiLabel("例如：哪些已安装应用能处理图片？这两个软件有什么区别？", "For example: Which installed apps edit images? How do two apps differ?");
    public string AiAskLabel => AiLabel("提问", "Ask");
    public string AiCompareLabel => AiLabel("对比已安装应用", "Compare installed apps");
    public string AiUninstallLabel => AiLabel("说明所选应用的卸载影响", "Explain selected app removal");
    public string AiCancelLabel => AiLabel("取消分析", "Cancel analysis");
    public string AiScopeNote => AiLabel(
        "提问和对比使用全部已登记应用及可用的目录介绍，不受列表筛选影响。卸载说明仅基于所选应用的安装登记，并区分本程序自建安装与其他程序安装；AI 本身不执行卸载、升级或清理。",
        "Questions and comparisons use all registered apps and available catalog descriptions, regardless of the list filter. Removal explanations use the selected receipt only and distinguish this store's own installations from other programs' installs. The AI itself performs no uninstall, upgrade, or cleanup.");
    public string AiConfigurationNote => _aiAssistant?.IsConfigured == true ? string.Empty : AiLabel(
        "请先在设置中配置可用的 AI 接口与模型。", "Configure an available AI endpoint and model in Settings first.");
    public string AiStatus => IsAiBusy ? AiLabel("正在分析…", "Analyzing…")
        : _aiCancelled ? AiLabel("分析已取消。", "Analysis cancelled.") : string.Empty;
    public string AiErrorMessage => _aiErrorCode switch
    {
        null => string.Empty,
        "ai_not_configured" => AiLabel("请先在设置中配置 AI 接口。", "Configure AI in Settings first."),
        "ai_auth_failed" => AiLabel("AI 接口鉴权失败，请检查密钥和接口设置。", "AI authentication failed. Check the key and endpoint settings."),
        "rate_limited" => AiLabel("AI 服务请求过于频繁，请稍后重试。", "The AI service is rate limited. Try again later."),
        "request_timeout" => AiLabel("AI 分析超时，请稍后重试。", "AI analysis timed out. Try again later."),
        "network_error" => AiLabel("无法连接 AI 服务，请检查网络后重试。", "Could not reach the AI service. Check your network and retry."),
        _ => AiLabel("AI 分析未完成，请检查接口设置或稍后重试。", "AI analysis could not complete. Check the endpoint settings or retry later."),
    };
    public bool CanAnalyzeInstalled => !_disposed && !IsBusy && !IsAiBusy && _all.Count > 0;
    public bool CanAskInstalled => CanAnalyzeInstalled && !string.IsNullOrWhiteSpace(AiQuestion);
    public bool CanExplainUninstall => CanAnalyzeInstalled && SelectedProgram is not null;

    [RelayCommand(CanExecute = nameof(CanAskInstalled), AllowConcurrentExecutions = true)]
    private Task AskInstalledAiAsync(CancellationToken ct) => RunInstalledAiAsync(
        new(AiAssistantTask.InstalledQuestion) { Question = AiQuestion.Trim() },
        AiLabel("已安装应用问答", "Installed-app question"), ct);

    [RelayCommand(CanExecute = nameof(CanAnalyzeInstalled), AllowConcurrentExecutions = true)]
    private Task CompareInstalledAiAsync(CancellationToken ct) => RunInstalledAiAsync(
        new(AiAssistantTask.InstalledQuestion)
        {
            Question = AiLabel(
                "比较我实际已安装应用的用途、适用场景和功能重叠；只依据提供的安装身份与目录资料，列出应用名称。资料不足时说明未知，不推断使用频率，也不要建议自动卸载。",
                "Compare the purposes, use cases, and overlapping features of my actual installed apps. Name the apps and use only the supplied installation identities and catalog information. State when information is missing; do not infer usage frequency or recommend automatic removal."),
        }, AiLabel("已安装应用对比", "Installed-app comparison"), ct);

    [RelayCommand(CanExecute = nameof(CanExplainUninstall), AllowConcurrentExecutions = true)]
    private Task ExplainUninstallAiAsync(CancellationToken ct)
    {
        if (SelectedProgram is not { } selected) return Task.CompletedTask;
        return RunInstalledAiAsync(new(AiAssistantTask.UninstallExplanation)
        {
            InstallId = selected.SourceKeyPath,
            Question = AiLabel("解释所选应用的卸载影响、已知事实和未知项。", "Explain the selected app's removal impact, known facts, and unknowns."),
            Context = JsonSerializer.Serialize(new
            {
                selected.DisplayName, selected.DisplayVersion,
                ReceiptOnly = true, VerifiedUninstallMetadata = false,
            }),
        }, selected.DisplayName, ct);
    }

    [RelayCommand]
    private void CancelInstalledAi() => CancelAiAnalysis(clearResult: false);

    private async Task RunInstalledAiAsync(AiAssistantRequest request, string subject, CancellationToken ct)
    {
        if (!CanAnalyzeInstalled) return;
        CancelAiAnalysis(clearResult: true);
        if (_aiAssistant?.IsConfigured != true)
        {
            _aiErrorCode = "ai_not_configured";
            OnPropertyChanged(nameof(AiErrorMessage));
            OnPropertyChanged(nameof(AiConfigurationNote));
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var version = ++_aiVersion;
        _aiCancellation = cancellation;
        var selectedId = SelectedProgram?.Id;
        var language = Text.LanguageCode;
        IsAiBusy = true;
        AiResultSubject = subject;
        try
        {
            var response = await _aiAssistant.AnalyzeAsync(request, language, cancellation.Token);
            if (!IsCurrentAi(version, cancellation, selectedId, language)) return;
            AiResponse = response;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (version == _aiVersion && !_disposed)
            {
                _aiCancelled = true;
                OnPropertyChanged(nameof(AiStatus));
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrentAi(version, cancellation, selectedId, language)) return;
            _aiErrorCode = ex is AiSummaryException ai ? ai.Code : "ai_failed";
            OnPropertyChanged(nameof(AiErrorMessage));
        }
        finally
        {
            if (version == _aiVersion && !_disposed)
            {
                _aiCancellation = null;
                IsAiBusy = false;
            }
        }
    }

    private bool IsCurrentAi(int version, CancellationTokenSource cancellation, string? selectedId, string language) =>
        !_disposed && version == _aiVersion && ReferenceEquals(_aiCancellation, cancellation)
        && !cancellation.IsCancellationRequested && SelectedProgram?.Id == selectedId && Text.LanguageCode == language;

    /// <summary>Called on refresh, selection/language change, unload, and disposal. Late results are ignored.</summary>
    public void CancelAiAnalysis(bool clearResult = false)
    {
        ++_aiVersion;
        var cancellation = _aiCancellation;
        _aiCancellation = null;
        _aiCancelled = cancellation is not null && !clearResult;
        if (cancellation is not null) _ = CancelAiSafelyAsync(cancellation);
        IsAiBusy = false;
        _aiErrorCode = null;
        if (clearResult)
        {
            AiResponse = string.Empty;
            AiResultSubject = string.Empty;
        }
        OnPropertyChanged(nameof(AiErrorMessage));
        OnPropertyChanged(nameof(AiStatus));
        RefreshAiCommands();
    }

    private static async Task CancelAiSafelyAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        catch (Exception) { /* The request owns disposal; stale completion is guarded by version. */ }
    }

    public void RefreshAiAvailability()
    {
        OnPropertyChanged(nameof(AiConfigurationNote));
        RefreshAiCommands();
    }

    private string AiLabel(string chinese, string english) => Text.IsEnglish ? english : chinese;
    private void RefreshAiCommands()
    {
        OnPropertyChanged(nameof(CanAnalyzeInstalled));
        OnPropertyChanged(nameof(CanAskInstalled));
        OnPropertyChanged(nameof(CanExplainUninstall));
        // 更新入口的可用性同样受 IsBusy/选中项影响，必须一起刷新，否则按钮会停在旧状态。
        OnPropertyChanged(nameof(CanCheckForUpdate));
        OnPropertyChanged(nameof(CanUpgrade));
        CheckForUpdateCommand.NotifyCanExecuteChanged();
        AskInstalledAiCommand.NotifyCanExecuteChanged();
        CompareInstalledAiCommand.NotifyCanExecuteChanged();
        ExplainUninstallAiCommand.NotifyCanExecuteChanged();
    }

    partial void OnAiQuestionChanged(string value) => CancelAiAnalysis(clearResult: true);
    partial void OnIsAiBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(AiStatus));
        RefreshAiCommands();
    }
    partial void OnIsBusyChanged(bool value) => RefreshAiCommands();
    partial void OnSelectedProgramChanged(InstalledProgram? value)
    {
        CancelAiAnalysis(clearResult: true);
        // 回退入口必须跟着选中项变化，不能残留上一个应用的可用状态。
        RefreshRollbackAvailability();
    }

    private void RefreshAiLanguage()
    {
        CancelAiAnalysis(clearResult: true);
        foreach (var property in new[] { nameof(AiHeading), nameof(AiQuestionHint), nameof(AiAskLabel),
            nameof(AiCompareLabel), nameof(AiUninstallLabel), nameof(AiCancelLabel), nameof(AiScopeNote),
            nameof(AiConfigurationNote), nameof(AiStatus), nameof(AiErrorMessage) })
            OnPropertyChanged(property);
    }
}
