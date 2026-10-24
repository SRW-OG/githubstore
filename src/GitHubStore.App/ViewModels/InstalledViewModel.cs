using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>展示安装库存；当前页面的数据源只提供本程序成功安装登记。</summary>
public sealed partial class InstalledViewModel : ObservableObject, IDisposable
{
    private readonly IInstalledProgramService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scanCancellation;
    private int _scanVersion;
    private bool _uninstallInProgress;
    private bool _disposed;
    private bool _loadedOnce;

    public InstalledViewModel(IInstalledProgramService service, LocalizationService text,
        GitHubStore.App.Services.Ai.IAiAssistantService? aiAssistant = null)
    {
        _service = service;
        _aiAssistant = aiAssistant;
        Text = text;
        Text.LanguageChanged += OnLanguageChanged;
    }

    public LocalizationService Text { get; }
    public ObservableCollection<InstalledProgram> Programs { get; } = [];
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUninstall), nameof(CanRemoveRegistration), nameof(CanRepair))]
    private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedName), nameof(SelectedPublisher),
        nameof(SelectedVersion), nameof(SelectedLocation), nameof(CanUninstall), nameof(CanRemoveRegistration),
        nameof(CanRepair), nameof(SelectedStatusText), nameof(HasStatusWarning), nameof(SelectedRegistrationText))]
    private InstalledProgram? _selectedProgram;

    public bool HasSelection => SelectedProgram is not null;
    public string SelectedName => SelectedProgram?.DisplayName ?? string.Empty;
    public string SelectedPublisher => SelectedProgram?.Publisher ?? "—";
    public string SelectedVersion => SelectedProgram?.DisplayVersion ?? "—";
    public string SelectedLocation => SelectedProgram?.InstallLocation ?? "—";
    public bool CanRemoveRegistration => !_disposed && !IsBusy && SelectedProgram?.CanRemoveRegistration == true;
    public bool CanUninstall => !_disposed && !IsBusy && SelectedProgram?.CanUninstall == true;

    /// <summary>入口缺失的登记可以定位修复；其余状态不提供修复。</summary>
    public bool CanRepair => !_disposed && !IsBusy && SelectedProgram?.CanRepairExecutable == true;

    /// <summary>
    /// 如实说明这条登记当前的状态。“入口缺失”与“无法核对”都不等于已卸载，
    /// 界面不能把它们显示成未安装，也不能据此自动清理。
    /// </summary>
    public string SelectedStatusText => SelectedProgram?.Status switch
    {
        InstalledStatus.NeedsRepair => Text["InstalledStatusNeedsRepair"],
        InstalledStatus.Unknown => Text["InstalledStatusUnknown"],
        InstalledStatus.NotInstalled => Text["InstalledStatusNotInstalled"],
        _ => Text["InstalledStatusInstalled"],
    };

    public bool HasStatusWarning => SelectedProgram?.Status is InstalledStatus.NeedsRepair or InstalledStatus.Unknown;

    /// <summary>
    /// Windows 卸载登记的核对结果文案。“没有匹配到”与“无法核对”必须区分：
    /// 两者都不能说成已卸载，前者只是没找到可用登记。
    /// </summary>
    public string SelectedRegistrationText => SelectedProgram?.RegistrationStatus switch
    {
        UninstallRegistrationStatus.Available => Text["InstalledRegistrationAvailable"],
        UninstallRegistrationStatus.NotFound => Text["InstalledRegistrationNotFound"],
        _ => Text["InstalledRegistrationUnknown"],
    };
    private IReadOnlyList<InstalledProgram> _all = [];

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_loadedOnce || _disposed) return;
        _loadedOnce = true;
        await RescanAsync(ct);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RescanAsync(CancellationToken ct)
    {
        if (_disposed || _uninstallInProgress) return;
        CancelAiAnalysis(clearResult: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var previous = _scanCancellation;
        var version = ++_scanVersion;
        _scanCancellation = cancellation;
        IsBusy = true;
        try
        {
            if (previous is not null) await previous.CancelAsync();
            if (!IsCurrentScan(version, cancellation)) return;
            ErrorMessage = string.Empty;
            StatusMessage = Text["InstalledScanning"];
            var programs = await _service.ScanAsync(cancellation.Token);
            if (!IsCurrentScan(version, cancellation)) return;
            _all = programs;
            ApplyFilter();
            if (IsCurrentScan(version, cancellation))
                StatusMessage = Text.Format("InstalledCount", _all.Count);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentScan(version, cancellation))
            {
                ErrorMessage = Text.Format("InstalledScanFailed", ex.Message);
                StatusMessage = string.Empty;
            }
        }
        finally
        {
            if (!_disposed && version == _scanVersion)
            {
                _scanCancellation = null;
                IsBusy = false;
            }
        }
    }

    private bool IsCurrentScan(int version, CancellationTokenSource cancellation) =>
        !_disposed && version == _scanVersion && !cancellation.IsCancellationRequested;

    [RelayCommand]
    private Task UninstallAsync(CancellationToken ct) => SelectedProgram is { } program
        ? UninstallConfirmedAsync(program, ct) : Task.CompletedTask;

    /// <summary>UI 必须将确认对话框展示的同一条不可变记录传回，不能重新读取当前选择。</summary>
    public async Task UninstallConfirmedAsync(InstalledProgram confirmedProgram, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedProgram);
        if (_disposed || IsBusy || _uninstallInProgress) return;
        if (!confirmedProgram.CanUninstall)
        {
            ErrorMessage = Text["InstalledUninstallUnavailable"];
            StatusMessage = string.Empty;
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var program = confirmedProgram;
        _uninstallInProgress = true;
        IsBusy = true;
        try
        {
            if (_disposed || cancellation.IsCancellationRequested) return;
            ErrorMessage = string.Empty;
            var result = await _service.LaunchUninstallAsync(program, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            if (result.Started)
            {
                ErrorMessage = string.Empty;
                // 成功路径有三种真实语义，文案必须逐一对应，不能混为一谈：
                // 1) 本程序删除自己解包的目录——没有启动任何程序；
                // 2) 本程序用登记的产品码驱动 Windows Installer 静默卸载并已核验——是命令行，但不是交互式卸载器；
                // 3) 启动了第三方自带的交互式卸载器——结果由它决定。
                StatusMessage = (result.Kind switch
                    {
                        UninstallRemovalKind.ManagedDirectoryDeleted
                            => Text.Format("InstalledRemoved", program.DisplayName),
                        UninstallRemovalKind.RemovedByProductCode
                            => Text.Format("InstalledRemovedByProductCode", program.DisplayName),
                        _ => Text.Format("InstalledUninstallStarted", program.DisplayName),
                    })
                    + " " + Text["InstalledRescanHint"];
            }
            else
            {
                StatusMessage = string.Empty;
                ErrorMessage = result.ErrorCode switch
                {
                    UninstallErrorCodes.NoUninstallString => Text["InstalledUninstallUnavailable"],
                    UninstallErrorCodes.InvalidUninstallCommand => Text["InstalledInvalidUninstallCommand"],
                    UninstallErrorCodes.ExecutableNotFound => Text["InstalledUninstallExecutableNotFound"],
                    "uninstall_cancelled" => Text["InstalledUninstallCancelled"],
                    UninstallErrorCodes.AccessDenied => Text["UninstallAccessDenied"],
                    UninstallErrorCodes.InstallationNotFound => Text["InstalledRemovalNotFound"],
                    UninstallErrorCodes.NotStoreOwned => Text["InstalledRemovalNotStoreOwned"],
                    UninstallErrorCodes.UnsafeRemovalPath => Text["InstalledRemovalUnsafePath"],
                    // 登记已移除但文件可能残留：如实说明，不能让用户以为清理干净了。
                    UninstallErrorCodes.RemovalIncomplete => Text.Format("InstalledRemovalIncomplete", program.DisplayName),
                    // 读取登记失败与删除失败都不是“无法启动卸载程序”，不能套用启动器的文案。
                    UninstallErrorCodes.RemovalFailed or UninstallErrorCodes.ScanFailed
                        => Text.Format("InstalledRemovalFailed", program.DisplayName),
                    _ => Text.Format("InstalledUninstallFailed", program.DisplayName),
                };
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !cancellation.IsCancellationRequested)
                ErrorMessage = Text.Format("InstalledUninstallFailed", ex.Message);
        }
        finally
        {
            _uninstallInProgress = false;
            if (!_disposed) IsBusy = false;
        }
    }

    public async Task RemoveRegistrationConfirmedAsync(InstalledProgram program, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (_disposed || IsBusy || _uninstallInProgress || !program.CanRemoveRegistration) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _uninstallInProgress = true;
        IsBusy = true;
        try
        {
            ErrorMessage = string.Empty;
            StatusMessage = string.Empty;
            var result = await _service.RemoveRegistrationAsync(program, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            if (result.Started && result.Kind == UninstallRemovalKind.RegistrationRemoved)
            {
                _all = _all.Where(item => item.Id != program.Id).ToArray();
                ApplyFilter();
                StatusMessage = Text.Format("InstalledRegistrationRemoved", program.DisplayName);
            }
            else ErrorMessage = Text["InstalledRegistrationRemovalFailed"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed) ErrorMessage = Text["InstalledRegistrationRemovalFailed"];
        }
        finally
        {
            _uninstallInProgress = false;
            if (!_disposed) IsBusy = false;
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_disposed) return;
        var query = Filter.Trim();
        var matches = string.IsNullOrEmpty(query) ? _all : _all.Where(p =>
            p.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (p.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            (p.InstallLocation?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToArray();
        var previousId = SelectedProgram?.Id;
        Programs.Clear();
        foreach (var program in matches.Take(500))
        {
            if (_disposed) return;
            Programs.Add(program);
        }
        if (!_disposed) SelectedProgram = Programs.FirstOrDefault(p => p.Id == previousId);
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (!_disposed) RefreshAiLanguage();
        if (!_disposed && !string.IsNullOrEmpty(StatusMessage) && !IsBusy)
            StatusMessage = Text.Format("InstalledCount", _all.Count);
    }

    /// <summary>
    /// 重新指定入口文件。路径由用户在系统文件选择器中亲自选定，
    /// 服务层会按安装 ID 重读登记并复验路径归属，界面传入的值不授予任何权限。
    /// </summary>
    public async Task RepairConfirmedAsync(InstalledProgram program, string executablePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (_disposed || IsBusy || _uninstallInProgress || !program.CanRepairExecutable) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _uninstallInProgress = true;
        IsBusy = true;
        try
        {
            ErrorMessage = string.Empty;
            StatusMessage = string.Empty;
            var result = await _service.RepairExecutableAsync(program, executablePath, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            if (result.Started && result.Kind == UninstallRemovalKind.RepairApplied)
            {
                // 修复只改入口路径，因此重扫一次即可拿到新的核对结果，而不是就地猜测。
                _uninstallInProgress = false;
                IsBusy = false;
                await RescanAsync(ct);
                if (!_disposed) StatusMessage = Text.Format("InstalledRepairApplied", program.DisplayName);
                return;
            }
            ErrorMessage = result.ErrorCode == UninstallErrorCodes.ExecutableNotFound
                ? Text["InstalledRepairExecutableMissing"]
                : result.ErrorCode == UninstallErrorCodes.InvalidExecutable
                    ? Text["InstalledRepairNotExecutable"]
                    : Text["InstalledRepairFailed"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed) ErrorMessage = Text["InstalledRepairFailed"];
        }
        finally
        {
            _uninstallInProgress = false;
            if (!_disposed) IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAiAnalysis(clearResult: true);
        Text.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
