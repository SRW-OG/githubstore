using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Ai;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

public sealed partial class InstallationViewModel
{
    private readonly IAiAssistantService? _assistant;
    private InstallDiagnostic? _lastDiagnostic;
    private CancellationTokenSource? _diagnosisCancellation;
    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDiagnose))]
    [NotifyCanExecuteChangedFor(nameof(DiagnoseCommand))]
    private bool _isDiagnosisBusy;
    [ObservableProperty] private string _diagnosisMarkdown = string.Empty;
    [ObservableProperty] private string _diagnosisError = string.Empty;

    public InstallDiagnostic? LastDiagnostic => _lastDiagnostic;
    public bool CanDiagnose => !_disposed && !IsInstalling && !IsDiagnosisBusy && _lastDiagnostic is not null && _assistant is not null;
    public string DiagnosisLabel => Text.IsEnglish ? "AI: explain installation failure" : "AI 分析安装失败原因";
    public string CancelDiagnosisLabel => Text.IsEnglish ? "Cancel analysis" : "取消分析";

    private void NotifyDiagnosisChanged()
    {
        OnPropertyChanged(nameof(LastDiagnostic));
        OnPropertyChanged(nameof(CanDiagnose));
        DiagnoseCommand.NotifyCanExecuteChanged();
    }

    private void ResetDiagnosis()
    {
        var previous = _diagnosisCancellation;
        _diagnosisCancellation = null;
        _ = CancelOnDisposeAsync(previous);
        _lastDiagnostic = null;
        DiagnosisMarkdown = string.Empty;
        DiagnosisError = string.Empty;
        IsDiagnosisBusy = false;
        NotifyDiagnosisChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDiagnose), AllowConcurrentExecutions = true)]
    private async Task DiagnoseAsync(CancellationToken ct)
    {
        if (!CanDiagnose || _assistant is null || _lastDiagnostic is not { } diagnostic) return;
        if (!_assistant.IsConfigured)
        {
            DiagnosisError = Text["AiNotConfigured"];
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _diagnosisCancellation = cancellation;
        var language = Text.IsEnglish ? "en-US" : "zh-CN";
        IsDiagnosisBusy = true;
        DiagnosisError = string.Empty;
        DiagnosisMarkdown = string.Empty;
        try
        {
            var result = await _assistant.AnalyzeAsync(new(AiAssistantTask.InstallDiagnosis)
            {
                Context = JsonSerializer.Serialize(diagnostic, DiagnosticJson),
            }, language, cancellation.Token);
            if (IsCurrent()) DiagnosisMarkdown = result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent()) DiagnosisError = Text.IsEnglish
                ? "Analysis failed. Check the AI settings or retry."
                : "分析失败，请检查 AI 接口设置或重试。";
        }
        finally
        {
            if (ReferenceEquals(_diagnosisCancellation, cancellation))
            {
                _diagnosisCancellation = null;
                IsDiagnosisBusy = false;
            }
        }
        bool IsCurrent() => !_disposed && !cancellation.IsCancellationRequested
            && ReferenceEquals(_diagnosisCancellation, cancellation)
            && ReferenceEquals(_lastDiagnostic, diagnostic)
            && language == (Text.IsEnglish ? "en-US" : "zh-CN");
    }

    [RelayCommand]
    private Task CancelDiagnosisAsync() => CancelOnDisposeAsync(_diagnosisCancellation);
}
