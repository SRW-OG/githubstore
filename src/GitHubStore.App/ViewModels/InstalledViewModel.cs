using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>
/// 已安装列表的一行。公开属性只描述可展示、可操作的行状态；候选对象与失败码保留在行内，
/// 防止筛选或切换选择时把一条安装的更新结论错配给另一条安装。
/// </summary>
public sealed class InstalledProgramListItem : ObservableObject
{
    private GitHubStore.App.Services.Installation.PortableUpgradeCandidate? _candidate;
    private string? _candidateErrorCode;
    private bool _canRollback;

    public InstalledProgramListItem(InstalledProgram program, LocalizationService text)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public InstalledProgram Program { get; }
    public LocalizationService Text { get; }
    public string DisplayName => Program.DisplayName;
    public string Publisher => Program.Publisher ?? "—";
    public string CurrentVersion => Program.DisplayVersion ?? "—";

    /// <summary>
    /// 只有查到真实候选，或目录明确返回“没有候选”时才展示版本。
    /// 目录不可用表示没有核对成功，不能把当前版本冒充成最新版。
    /// </summary>
    public string LatestVersion => _candidate?.CandidateVersion
        ?? (_candidateErrorCode == GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CandidateNotFound
            ? CurrentVersion : "—");

    public bool CanUninstall => Program.CanUninstall;
    public bool CanUpdate => _candidate is not null;
    public bool CanRollback => _canRollback;

    internal GitHubStore.App.Services.Installation.PortableUpgradeCandidate? Candidate => _candidate;
    internal string? CandidateErrorCode => _candidateErrorCode;

    internal void SetCandidateResult(GitHubStore.App.Services.Installation.PortableUpgradeCandidateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _candidate = result.Candidate;
        _candidateErrorCode = result.ErrorCode;
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(CanUpdate));
    }

    internal void SetRollbackAvailability(bool value)
    {
        if (_canRollback == value) return;
        _canRollback = value;
        OnPropertyChanged(nameof(CanRollback));
    }
}

/// <summary>展示安装库存；当前页面的数据源只提供本程序成功安装登记。</summary>
public sealed partial class InstalledViewModel : ObservableObject, IDisposable
{
    private readonly IInstalledProgramService _service;
    private readonly GitHubStore.App.Services.Installation.PortableUpgradeService? _upgrades;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scanCancellation;
    private int _scanVersion;
    private bool _uninstallInProgress;
    private bool _disposed;
    private bool _loadedOnce;
    private string? _rollbackInstallId;
    /// <summary>当前选中项已找到的候选更新；null 表示还没查或确实没有。</summary>
    private GitHubStore.App.Services.Installation.PortableUpgradeCandidate? _updateCandidate;
    /// <summary>候选对应的安装 ID。选中项一变就必须作废，避免把 A 的候选装到 B 上。</summary>
    private string? _updateCandidateInstallId;
    /// <summary>查候选时的结论码；null 表示未查或已找到候选。</summary>
    private string? _updateCandidateErrorCode;

    public InstalledViewModel(IInstalledProgramService service, LocalizationService text,
        GitHubStore.App.Services.Ai.IAiAssistantService? aiAssistant = null,
        GitHubStore.App.Services.Installation.PortableUpgradeService? upgrades = null)
    {
        _service = service;
        _aiAssistant = aiAssistant;
        _upgrades = upgrades;
        Text = text;
        Text.LanguageChanged += OnLanguageChanged;
    }

    public LocalizationService Text { get; }
    public ObservableCollection<InstalledProgram> Programs { get; } = [];
    public ObservableCollection<InstalledProgramListItem> InstalledItems { get; } = [];
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUninstall), nameof(CanRemoveRegistration), nameof(CanRepair),
        nameof(CanCheckForUpdate), nameof(CanUpgrade))]
    private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedName), nameof(SelectedPublisher),
        nameof(SelectedVersion), nameof(SelectedLocation), nameof(CanUninstall), nameof(CanRemoveRegistration),
        nameof(CanRepair), nameof(SelectedStatusText), nameof(HasStatusWarning), nameof(SelectedRegistrationText),
        nameof(CanRollback), nameof(CanCheckForUpdate), nameof(CanUpgrade), nameof(UpdateStatusText))]
    private InstalledProgram? _selectedProgram;

    public bool HasSelection => SelectedProgram is not null;
    public string SelectedName => SelectedProgram?.DisplayName ?? string.Empty;
    public string SelectedPublisher => SelectedProgram?.Publisher ?? "—";
    public string SelectedVersion => SelectedProgram?.DisplayVersion ?? "—";
    public string SelectedLocation => SelectedProgram?.InstallLocation ?? "—";
    // 恢复未完成时登记本身正要被改写，此时移除登记会把恢复所需的权威信息删掉。
    public bool CanRemoveRegistration => !_disposed && !IsBusy && SelectedProgram?.CanRemoveRegistration == true
        && SelectedProgram.Status != InstalledStatus.RecoveryRequired;
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
        // 更新恢复未完成：文件与登记可能不一致。既不是“可运行”，也不是“入口缺失”，必须单独如实说明。
        InstalledStatus.RecoveryRequired => Text["InstalledStatusRecoveryRequired"],
        InstalledStatus.Unknown => Text["InstalledStatusUnknown"],
        InstalledStatus.NotInstalled => Text["InstalledStatusNotInstalled"],
        _ => Text["InstalledStatusInstalled"],
    };

    public bool HasStatusWarning => SelectedProgram?.Status is InstalledStatus.NeedsRepair
        or InstalledStatus.Unknown or InstalledStatus.RecoveryRequired;

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
    private IReadOnlyList<InstalledProgramListItem> _allInstalledItems = [];

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
            var installedItems = programs.Select(program => new InstalledProgramListItem(program, Text)).ToArray();
            await InspectInstalledItemsAsync(installedItems, cancellation.Token);
            if (!IsCurrentScan(version, cancellation)) return;
            _all = programs;
            _allInstalledItems = installedItems;
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

    private async Task InspectInstalledItemsAsync(IReadOnlyList<InstalledProgramListItem> items, CancellationToken ct)
    {
        if (_upgrades is null) return;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var program = item.Program;
            var canInspect = program.Origin == RegistryOrigin.StoreInstall
                && program.Status == InstalledStatus.Installed
                && !string.IsNullOrWhiteSpace(program.SourceKeyPath);
            if (!canInspect) continue;

            try
            {
                var rollback = await _upgrades.FindRollbackCandidateAsync(program.SourceKeyPath, ct);
                item.SetRollbackAvailability(rollback.Candidate is not null);
                item.SetCandidateResult(await _upgrades.FindCandidateAsync(program.SourceKeyPath, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // 核对失败不能让安装库存本身消失，也不能因此宣称当前版本已经是最新、或可回退。
                item.SetRollbackAvailability(false);
                item.SetCandidateResult(new(null,
                    GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CatalogUnavailable));
            }
        }
    }

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
                _allInstalledItems = _allInstalledItems.Where(item => item.Program.Id != program.Id).ToArray();
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
        InstalledItems.Clear();
        foreach (var program in matches.Take(500))
        {
            if (_disposed) return;
            Programs.Add(program);
            var item = _allInstalledItems.FirstOrDefault(candidate => ReferenceEquals(candidate.Program, program))
                ?? _allInstalledItems.FirstOrDefault(candidate => candidate.Program.Id == program.Id);
            if (item is not null) InstalledItems.Add(item);
        }
        if (!_disposed)
        {
            SelectedProgram = Programs.FirstOrDefault(p => p.Id == previousId);
            RefreshRollbackAvailability();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (!_disposed) RefreshAiLanguage();
        if (!_disposed && !string.IsNullOrEmpty(StatusMessage) && !IsBusy)
            StatusMessage = Text.Format("InstalledCount", _all.Count);
    }

    /// <summary>
    /// 是否有可回退的上一版本。回退由用户显式触发，且只在保留记录仍存在时为真。
    /// </summary>
    public bool CanRollback => !_disposed && !IsBusy && _upgrades is not null
        && SelectedProgram?.Status == InstalledStatus.Installed
        && _rollbackInstallId == SelectedProgram.SourceKeyPath;

    /// <summary>
    /// 回退到上一版本。只适用于本程序解包的绿色安装；失败时保留当前版本并如实报错。
    /// </summary>
    public async Task<bool> RollbackConfirmedAsync(InstalledProgram program, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (_disposed || IsBusy || _uninstallInProgress || _upgrades is null) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _uninstallInProgress = true;
        IsBusy = true;
        // 需要重扫的两种情形：成功换了文件，或文件可能已换回但登记没写回（中间状态）。
        var refresh = false;
        try
        {
            ErrorMessage = string.Empty;
            StatusMessage = string.Empty;
            var result = await _upgrades.RollbackAsync(program.SourceKeyPath, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            if (result.Succeeded)
            {
                StatusMessage = Text.Format("InstalledRollbackApplied", program.DisplayName);
                refresh = true;
                return true;
            }
            ErrorMessage = MapRollbackError(result.ErrorCode);
            // 只要回退可能动过文件（文件已换回/处于中间状态），就重扫；否则界面会停在过期状态。
            refresh = result.ErrorCode is GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.ReceiptRestoreFailed
                or GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.RollbackFailed
                or GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.RollbackIncomplete;
            return false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            if (!_disposed) ErrorMessage = Text["InstalledRollbackFailed"];
            return false;
        }
        finally
        {
            _uninstallInProgress = false;
            if (!_disposed) IsBusy = false;
            // 重扫必须在这里做：RescanAsync 见到 _uninstallInProgress 会直接早退，
            // 在 try 内部调用等于什么都没刷新，界面会继续显示已被替换掉的旧状态。
            if (!_disposed && !cancellation.IsCancellationRequested && refresh) await RescanAsync(CancellationToken.None);
        }
    }

    /// <summary>回退失败码到用户文案。没有任何一个码应当退化成“无法启动卸载程序”的说法。</summary>
    private string MapRollbackError(string? errorCode) => errorCode switch
    {
        GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.RollbackNotAvailable
            => Text["InstalledRollbackUnavailable"],
        // 软件源读不出、或回退所需的旧版本安装包拿不到：如实说不可回退，绝不猜测。
        GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CatalogUnavailable
            or GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.ScanFailed
            or GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.RollbackNotPortable
            or GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.DownloadFailed
            => Text["InstalledRollbackUnavailable"],
        // 上次更新/回退未收尾：需要先恢复，不能继续动文件。
        GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.PendingJournal
            => Text["InstalledUpdatePendingRecovery"],
        // 本次回退已经改动过文件但没能收尾：必须如实说明“可能处于中间状态”，并告知重启会继续恢复。
        GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.ReceiptRestoreFailed
            or GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.RollbackFailed
            => Text["InstalledRollbackIncomplete"],
        _ => Text["InstalledRollbackFailed"],
    };

    /// <summary>
    /// 记录当前选中项是否可回退，供界面决定是否显示回退入口。
    /// 回退可用性来自逐行扫描时对软件源的查询结果，不读取本地备份。
    /// </summary>
    private void RefreshRollbackAvailability()
    {
        var item = SelectedProgram is { } selected
            ? _allInstalledItems.FirstOrDefault(candidate => candidate.Program.Id == selected.Id)
            : null;
        _rollbackInstallId = item?.CanRollback == true ? item.Program.SourceKeyPath : null;
        _updateCandidate = item?.Candidate;
        _updateCandidateInstallId = item is not null ? item.Program.SourceKeyPath : null;
        _updateCandidateErrorCode = item?.CandidateErrorCode;
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanCheckForUpdate));
        OnPropertyChanged(nameof(CanUpgrade));
        OnPropertyChanged(nameof(UpdateStatusText));
    }

    /// <summary>
    /// 是否存在可检查的候选更新：只对本程序已登记且当前可运行的安装开放。
    /// 候选来自绑定软件源的目录，界面不猜版本。
    /// </summary>
    public bool CanCheckForUpdate => !_disposed && !IsBusy && !IsAiBusy && _upgrades is not null
        && SelectedProgram is { Origin: RegistryOrigin.StoreInstall, Status: InstalledStatus.Installed }
        && !string.IsNullOrWhiteSpace(SelectedProgram.SourceKeyPath);

    /// <summary>
    /// 是否可以执行更新：必须已经为<em>当前这条</em>安装查到候选，且候选带有可用资产。
    /// 这是“用户明确确认”之前的最后一道门槛——界面不会替用户决定装什么。
    /// </summary>
    public bool CanUpgrade => CanCheckForUpdate && _updateCandidate is not null
        && _updateCandidateInstallId == SelectedProgram?.SourceKeyPath;

    /// <summary>
    /// 候选与版本比较结论的如实说明。无法比较（Different）时明确写成“无法判定”，
    /// 绝不把它说成“确认有新版”。
    /// </summary>
    public string UpdateStatusText
    {
        get
        {
            if (_upgrades is null) return string.Empty;
            if (_updateCandidateInstallId is { } id && id == (SelectedProgram?.SourceKeyPath ?? string.Empty)
                && _updateCandidate is { } candidate)
            {
                return candidate.Relation == GitHubStore.Core.Catalog.VersionRelation.Different
                    ? Text.Format("InstalledUpdateDifferent", SelectedVersion, candidate.CandidateVersion)
                    : Text.Format("InstalledUpdateAvailable", SelectedVersion, candidate.CandidateVersion);
            }
            return _updateCandidateErrorCode switch
            {
                GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CandidateNotFound
                    => Text["InstalledUpdateCurrent"],
                GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CatalogUnavailable
                    => Text["InstalledUpdateUnavailable"],
                GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.NotPortable
                    => Text["InstalledUpdateNotPortable"],
                // 上一次更新还没收尾：先恢复，别开新的。
                GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.PendingJournal
                    => Text["InstalledUpdatePendingRecovery"],
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// 查询候选更新。结果只用于告知与启用“更新”按钮；这里不下载、不替换、不写任何文件。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdate), AllowConcurrentExecutions = true)]
    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (_disposed || IsBusy || _uninstallInProgress || !CanCheckForUpdate) return;
        var installationId = SelectedProgram!.SourceKeyPath;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _uninstallInProgress = true;
        IsBusy = true;
        try
        {
            ErrorMessage = string.Empty;
            StatusMessage = Text["InstalledUpdateChecking"];
            var result = await _upgrades!.FindCandidateAsync(installationId, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            var item = _allInstalledItems.FirstOrDefault(candidate =>
                candidate.Program.SourceKeyPath == installationId);
            item?.SetCandidateResult(result);
            StatusMessage = string.Empty;
            if (SelectedProgram?.SourceKeyPath == installationId)
            {
                RefreshRollbackAvailability();
                ErrorMessage = result.ErrorCode == GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CatalogUnavailable
                    ? Text["InstalledUpdateUnavailable"] : string.Empty;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
            {
                var unavailable = new GitHubStore.App.Services.Installation.PortableUpgradeCandidateResult(null,
                    GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.CatalogUnavailable);
                _allInstalledItems.FirstOrDefault(candidate => candidate.Program.SourceKeyPath == installationId)
                    ?.SetCandidateResult(unavailable);
                if (SelectedProgram?.SourceKeyPath == installationId)
                {
                    RefreshRollbackAvailability();
                    ErrorMessage = Text["InstalledUpdateUnavailable"];
                }
            }
        }
        finally
        {
            _uninstallInProgress = false;
            if (!_disposed)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(CanUpgrade));
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }
    }

    /// <summary>
    /// 已确认的更新：用当前候选替换这条安装。候选与安装 ID 在进入时再次核对，
    /// 只有两者仍然匹配当前选中项才执行——确认对话框展示的内容不能被悄悄换掉。
    /// </summary>
    public async Task<bool> UpgradeConfirmedAsync(InstalledProgram program, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (_disposed || IsBusy || _uninstallInProgress || _upgrades is null) return false;
        if (SelectedProgram?.Id != program.Id) return false;
        var item = _allInstalledItems.FirstOrDefault(candidate => candidate.Program.Id == program.Id);
        if (item?.Candidate is not { } candidate || item.Program.SourceKeyPath != program.SourceKeyPath) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _uninstallInProgress = true;
        IsBusy = true;
        try
        {
            ErrorMessage = string.Empty;
            StatusMessage = Text["InstalledUpdateApplying"];
            var result = await _upgrades.UpdateAsync(new GitHubStore.App.Services.Installation.PortableUpgradeRequest
            {
                InstallId = program.SourceKeyPath, App = candidate.App, Release = candidate.Release,
                Asset = candidate.Asset, ExecutableRelativePath = candidate.ExecutableRelativePath,
                PreservePaths = candidate.PreservePaths,
            }, progress: null, chooseExecutable: null, ct: cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            if (result.Succeeded)
            {
                StatusMessage = Text.Format("InstalledUpdateApplied", program.DisplayName, result.DisplayVersion ?? candidate.CandidateVersion);
                return true;
            }
            ErrorMessage = MapUpdateError(result);
            return false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            if (!_disposed) ErrorMessage = Text["InstalledUpdateFailed"];
            return false;
        }
        finally
        {
            _uninstallInProgress = false;
            if (!_disposed)
            {
                IsBusy = false;
                // 无论成败都重新查一遍：成功要反映新版本，失败可能已还原成旧版本。
                await RescanAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 更新失败码到用户文案。两条最严重的路径必须分开说：
    /// 已还原（旧版本完好）与未能还原（可能处于不一致状态，重启会继续恢复）。
    /// </summary>
    private string MapUpdateError(GitHubStore.App.Services.Installation.PortableUpgradeResult result)
    {
        // 先看既成事实，再看原因码：文件是否已还原比“卡在哪一步”重要得多。
        // FilesRestored=true 表示旧版本完好无损，用户重试即可。
        if (result.FilesRestored) return Text["InstalledUpdateRestored"];
        // 未能还原：可能处于“新文件 + 旧登记”之类的不一致状态，重启会继续恢复。绝不能轻描淡写。
        if (result.FilesMayRemain)
            return Text.Format("InstalledUpdateIncomplete", SelectedProgram?.DisplayName ?? string.Empty);
        return result.ErrorCode switch
        {
            GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.AlreadyCurrent => Text["InstalledUpdateCurrent"],
            GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.NotPortable => Text["InstalledUpdateNotPortable"],
            GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.PreservePathsUnsupported
                => Text["InstalledUpdatePreservePaths"],
            GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.NotAnUpgrade
                => Text["InstalledUpdateNotAnUpgrade"],
            GitHubStore.App.Services.Installation.PortableUpgradeErrorCodes.PendingJournal
                => Text["InstalledUpdatePendingRecovery"],
            _ => Text["InstalledUpdateFailed"],
        };
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
