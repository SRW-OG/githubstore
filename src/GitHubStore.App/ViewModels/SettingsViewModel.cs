using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.Services.Updates;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;

namespace GitHubStore.App.ViewModels;

/// <summary>Pure presentation state; consumers call initialization and apply on the UI thread.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private Task? _initialization;
    private string? _errorKey;
    private string? _statusKey;
    private string? _aria2StatusKey;
    private bool _disposed;

    private readonly IManagedAppLauncher? _managedApps;
    private readonly InstallationViewModel? _installation;
    private readonly PackageCacheService _packageCache;

    /// <summary>自更新服务；未接入时界面如实报告不可用，不会假装有更新。</summary>
    private readonly SelfUpdateService? _selfUpdate;

    /// <summary>软件库维护需要真实发行包与校验信息；未接入时维护功能如实报告不可用。</summary>
    private readonly ICatalogService? _catalog;

    public SettingsViewModel(PreferencesService preferences, LocalizationService text,
        IManagedAppLauncher? managedApps = null, InstallationViewModel? installation = null,
        ICatalogLibrarySyncService? librarySync = null, PackageCacheService? packageCache = null,
        SelfUpdateService? selfUpdate = null, ICatalogService? catalog = null)
    {
        _selfUpdate = selfUpdate;
        _catalog = catalog;
        _librarySync = librarySync;
        _managedApps = managedApps;
        _installation = installation;
        _packageCache = packageCache ?? new PackageCacheService(
            () => StorageLocations.ResolveCacheDirectory(preferences.Current, preferences.DataDirectory));
        Preferences = preferences;
        Text = text;
        _selectedTheme = preferences.Current.Theme;
        _selectedLanguage = preferences.Current.Language;
        Text.LanguageChanged += OnLanguageChanged;
    }

    public PreferencesService Preferences { get; }
    public LocalizationService Text { get; }
    [ObservableProperty] private ThemeMode _selectedTheme;
    [ObservableProperty] private string _selectedLanguage;
    [ObservableProperty] private string _runtimeDescription = string.Empty;

    /// <summary>下载缓存目录；留空使用应用默认目录。</summary>
    [ObservableProperty] private string _downloadCacheDirectory = string.Empty;

    /// <summary>默认安装目录；留空使用 C:\githubapps。</summary>
    [ObservableProperty] private string _installRootDirectory = string.Empty;

    /// <summary>提示当前生效的默认目录，让“留空”也有明确含义。</summary>
    public string DefaultCacheHint => Text.Format("SettingsUseDefault") + ": " + StorageLocations.DefaultCacheDirectory;
    public string DefaultInstallHint => Text.Format("SettingsUseDefault") + ": " + StorageLocations.DefaultInstallRoot;

    private PackageCacheSummary? _cacheSummary;
    public string CacheRoot => _packageCache.Root;
    public string CacheUsageText => _cacheSummary is null ? Text["SettingsCacheUsageUnknown"]
        : Text.Format("SettingsCacheUsage", _cacheSummary.FileCount, FormatBytes(_cacheSummary.TotalBytes));
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearCache), nameof(CanApply), nameof(CanEditProxy))]
    [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
    private bool _isCacheBusy;
    [ObservableProperty] private string _cacheStatus = string.Empty;
    [ObservableProperty] private string _cacheError = string.Empty;

    public async Task RefreshCacheUsageAsync(CancellationToken ct = default)
    {
        if (_disposed || IsCacheBusy || !IsInitialized) return;
        IsCacheBusy = true;
        CacheError = string.Empty;
        try { await UpdateCacheUsageAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed) { _cacheSummary = null; OnPropertyChanged(nameof(CacheUsageText)); CacheError = Text["SettingsCacheReadFailed"]; }
        }
        finally { if (!_disposed) IsCacheBusy = false; }
    }

    private async Task UpdateCacheUsageAsync(CancellationToken ct)
    {
        var summary = await Task.Run(() => _packageCache.SummarizeAsync(ct), ct);
        if (_disposed) return;
        _cacheSummary = summary;
        OnPropertyChanged(nameof(CacheUsageText));
        OnPropertyChanged(nameof(CacheRoot));
    }

    public bool CanClearCache => !_disposed && IsInitialized && !IsCacheBusy && !IsBusy && !IsProbing;

    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private Task ClearCacheAsync(CancellationToken ct) => ClearCacheConfirmedAsync(CacheRoot, ct);

    /// <summary>The view supplies the exact saved root displayed in its confirmation. No per-package deletion.</summary>
    public async Task ClearCacheConfirmedAsync(string confirmedRoot, CancellationToken ct = default)
    {
        if (!CanClearCache) return;
        CacheStatus = string.Empty;
        CacheError = string.Empty;
        if (!string.Equals(confirmedRoot, CacheRoot, StringComparison.OrdinalIgnoreCase))
        { CacheError = Text["SettingsCacheRootChanged"]; return; }
        IsCacheBusy = true;
        try
        {
            var result = await Task.Run(() => _packageCache.ClearAsync(ct), ct);
            if (_disposed) return;
            if (result.Busy) { CacheError = Text["SettingsCacheInUse"]; return; }
            await UpdateCacheUsageAsync(ct);
            if (_disposed) return;
            CacheStatus = Text.Format("SettingsCacheCleared", result.DeletedFiles, FormatBytes(result.FreedBytes));
            if (result.SkippedFiles > 0 || result.RemainingFiles > 0)
                CacheError = Text.Format("SettingsCacheClearPartial", result.RemainingFiles, result.SkippedFiles);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { if (!_disposed) CacheError = Text["SettingsCacheClearCancelled"]; }
        catch (Exception)
        {
            if (!_disposed)
            {
                _cacheSummary = null;
                OnPropertyChanged(nameof(CacheUsageText));
                CacheError = Text["SettingsCacheClearFailed"];
            }
        }
        finally { if (!_disposed) IsCacheBusy = false; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KiB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576d).ToString("N1", CultureInfo.CurrentCulture) + " MiB";
        return (bytes / 1073741824d).ToString("N2", CultureInfo.CurrentCulture) + " GiB";
    }

    [ObservableProperty] private string _aiEndpoint = "https://api.openai.com/v1";
    [ObservableProperty] private string _aiModel = "gpt-4o-mini";
    [ObservableProperty] private string _aiApiKey = string.Empty;

    // ── 下载与网络 ──────────────────────────────────────────────
    /// <summary>首选下载引擎；内置组件不可用时报告错误，不自动切换引擎。</summary>
    [ObservableProperty] private DownloadEngine _downloadEngine = DownloadEngine.Http;
    [ObservableProperty] private bool _useAccelerator;
    [ObservableProperty] private bool _enableP2P;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyEnabled))]
    [NotifyPropertyChangedFor(nameof(CanEditProxy))]
    private ProxyKind _proxyKind = ProxyKind.None;
    [ObservableProperty] private string _proxyHost = string.Empty;
    [ObservableProperty] private string _proxyPort = string.Empty;
    [ObservableProperty] private string _proxyUserName = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    /// <summary>仅为旧设置与调用方兼容保留；设置页不编辑或提交此值。</summary>
    [ObservableProperty] private string _aria2ExecutablePath = string.Empty;
    /// <summary>仅为旧设置与调用方兼容保留；加速服务由应用管理。</summary>
    [ObservableProperty] private string _xgetInstance = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanEditProxy), nameof(CanClearCache))]
    [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
    private bool _isProbing;
    /// <summary>探测结果文案（本地化）；空表示尚未探测。</summary>
    [ObservableProperty] private string _aria2Status = string.Empty;

    /// <summary>代理相关控件仅在启用代理时可用，避免用户填了却不生效。</summary>
    public bool IsProxyEnabled => ProxyKind != ProxyKind.None;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanEditProxy), nameof(CanClearCache))]
    [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
    private bool _isBusy;

    public bool IsInitialized { get; private set; }
    public bool CanApply => IsInitialized && !IsBusy && !IsProbing && !IsCacheBusy && !IsSynchronizingCatalogLibrary;
    public bool CanEditProxy => CanApply && IsProxyEnabled;
    public bool HasFeedback => _errorKey is not null || _statusKey is not null;
    public string ErrorMessage => _errorKey is null ? string.Empty : Text.Format(_errorKey);
    public string StatusMessage => _statusKey is null ? string.Empty : Text.Format(_statusKey);

    public Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInitialized) return Task.CompletedTask;
        return _initialization is { IsCompleted: false } ? _initialization : _initialization = InitializeCoreAsync(ct);
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await Preferences.LoadAsync(ct);
            if (_disposed) return;
            RestoreSelections();
            SetError(Preferences.Error);
            IsInitialized = true;
            // 版本号在初始化后即可展示；此时不发起任何网络请求，也不提示更新。
            RefreshSelfUpdateText();
            OnPropertyChanged(nameof(IsInitialized));
            OnPropertyChanged(nameof(CanClearCache));
            ClearCacheCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanEditProxy));
            OnPropertyChanged(nameof(IsSelfUpdateAvailable));
            OnPropertyChanged(nameof(CanCheckForUpdate));
            OnPropertyChanged(nameof(CanInstallSelfUpdate));
            CheckForUpdateCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            if (!_disposed) IsBusy = false;
        }
    }

    /// <summary>Legacy save for appearance, paths and network only; AI has its own save boundary.</summary>
    public Task ApplyAsync(ThemeMode theme, string language, CancellationToken ct = default) =>
        ApplyCoreAsync(() => WithNetworkDraft(WithPathsDraft(Preferences.Current)) with
        {
            Theme = theme,
            Language = language,
        }, () =>
        {
            RestoreAppearance(); RestorePaths(); RestoreNetwork();
            Aria2ExecutablePath = Preferences.Current.Aria2ExecutablePath ?? string.Empty;
            XgetInstance = Preferences.Current.XgetInstance ?? string.Empty;
        }, validateNetwork: true, ct);

    public Task ApplyPathsAsync(CancellationToken ct = default) =>
        ApplyCoreAsync(() => WithPathsDraft(Preferences.Current), RestorePaths, validateNetwork: false, ct);

    public Task ApplyNetworkAsync(CancellationToken ct = default) =>
        ApplyCoreAsync(() => WithNetworkDraft(Preferences.Current), RestoreNetwork, validateNetwork: true, ct);

    public Task ApplyAppearanceAsync(ThemeMode theme, string language, CancellationToken ct = default) =>
        ApplyCoreAsync(() => Preferences.Current with { Theme = theme, Language = language },
            RestoreAppearance, validateNetwork: false, ct);

    /// <summary>AI settings are saved independently; other sections never submit this draft.</summary>
    public Task ApplyAiAsync(CancellationToken ct = default) =>
        ApplyCoreAsync(() => Preferences.Current with
        {
            AiEndpoint = AiEndpoint,
            AiModel = AiModel,
            AiApiKey = string.IsNullOrEmpty(AiApiKey) ? null : AiApiKey,
        }, RestoreAi, validateNetwork: false, ct);

    private async Task ApplyCoreAsync(Func<AppPreferences> createPreferences, Action syncSavedFields,
        bool validateNetwork, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanApply) return;
        IsBusy = true;
        SetError(null);
        SetStatus(null);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (validateNetwork && ProxyKind != ProxyKind.None
                && (string.IsNullOrWhiteSpace(ProxyHost) || ParseProxyPort() == 0))
            {
                SetError("SettingsProxyInvalid");
                return;
            }

            var next = createPreferences();
            var oldRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StorageLocations.ResolveInstallRoot(Preferences.Current)));
            var newRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StorageLocations.ResolveInstallRoot(next)));
            if (!string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            {
                var locked = _installation?.IsInstalling == true;
                if (!locked && _managedApps is not null)
                {
                    try { locked = (await _managedApps.ListAsync(ct)).Count > 0; }
                    catch (LauncherException) { locked = true; }
                }
                if (locked)
                {
                    SetError("SettingsInstallRootLocked");
                    return;
                }
            }
            await Preferences.SaveAsync(next, ct);
            if (_disposed) return;
            syncSavedFields();
            SetStatus("SettingsSaved");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            if (!_disposed) SetError("SettingsSavePermissionDenied");
        }
        catch (IOException)
        {
            if (!_disposed) SetError("SettingsSaveFailed");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            if (!_disposed) SetError(Preferences.Error ?? "SettingsSecretProtectionFailed");
        }
        catch (ArgumentException ex)
        {
            if (!_disposed) SetError(ex.ParamName switch
            {
                nameof(AppPreferences.CatalogLibraryUrl) => "SettingsCatalogLibraryUrlInvalid",
                nameof(AppPreferences.AiEndpoint) => "SettingsAiEndpointInvalid",
                nameof(AppPreferences.AiModel) => "SettingsAiModelInvalid",
                _ => "SettingsInvalidSelection",
            });
        }
        finally
        {
            if (!_disposed) IsBusy = false;
        }
    }

    private AppPreferences WithPathsDraft(AppPreferences current) => current with
    {
        DownloadCacheDirectory = NullIfBlank(DownloadCacheDirectory),
        InstallRootDirectory = NullIfBlank(InstallRootDirectory),
    };

    private AppPreferences WithNetworkDraft(AppPreferences current) => current with
    {
        DownloadEngine = DownloadEngine,
        UseAccelerator = UseAccelerator,
        EnableP2P = EnableP2P,
        ProxyKind = ProxyKind,
        ProxyHost = NullIfBlank(ProxyHost),
        ProxyPort = ParseProxyPort(),
        ProxyUserName = NullIfBlank(ProxyUserName),
        ProxyPassword = string.IsNullOrEmpty(ProxyPassword) ? null : ProxyPassword,
    };

    public void ReportFailure()
    {
        SetStatus(null);
        SetError("SettingsSaveFailed");
    }

    private void RestoreSelections()
    {
        RestoreAppearance();
        RestorePaths();
        RestoreNetwork();
        RestoreAi();
        RestoreCatalogLibrary();
        Aria2ExecutablePath = Preferences.Current.Aria2ExecutablePath ?? string.Empty;
        XgetInstance = Preferences.Current.XgetInstance ?? string.Empty;
    }

    private void RestoreAppearance()
    {
        SelectedTheme = Preferences.Current.Theme;
        SelectedLanguage = Preferences.Current.Language;
        Text.ApplyLanguage(SelectedLanguage);
    }

    private void RestorePaths()
    {
        DownloadCacheDirectory = Preferences.Current.DownloadCacheDirectory ?? string.Empty;
        InstallRootDirectory = Preferences.Current.InstallRootDirectory ?? string.Empty;
        _cacheSummary = null;
        CacheStatus = string.Empty;
        CacheError = string.Empty;
        OnPropertyChanged(nameof(CacheRoot));
        OnPropertyChanged(nameof(CacheUsageText));
    }

    private void RestoreAi()
    {
        AiEndpoint = Preferences.Current.AiEndpoint;
        AiModel = Preferences.Current.AiModel;
        AiApiKey = Preferences.Current.AiApiKey ?? string.Empty;
    }

    private void RestoreNetwork()
    {
        var current = Preferences.Current;
        DownloadEngine = current.DownloadEngine;
        UseAccelerator = current.UseAccelerator;
        EnableP2P = current.EnableP2P;
        ProxyKind = current.ProxyKind;
        ProxyHost = current.ProxyHost ?? string.Empty;
        ProxyPort = current.ProxyPort > 0 ? current.ProxyPort.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ProxyUserName = current.ProxyUserName ?? string.Empty;
        ProxyPassword = current.ProxyPassword ?? string.Empty;
    }

    /// <summary>
    /// 端口输入是自由的文本，非法值在这里被拒绝；
    /// 启用代理却不填端口属于用户错误，必须明确报错而不是静默忽略。
    /// </summary>
    private int ParseProxyPort()
    {
        if (ProxyKind == ProxyKind.None) return 0;
        if (string.IsNullOrWhiteSpace(ProxyPort)) return 0;
        return int.TryParse(ProxyPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535 ? port : 0;
    }

    /// <summary>通过组合根提供的真实 API 检查内置 aria2；检查不修改引擎选择。</summary>
    public async Task ProbeDownloadEngineAsync(System.Func<CancellationToken, Task<bool>>? probe, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy || IsProbing) return;
        IsProbing = true;
        SetAria2Status("DownloadTesting");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (probe is null)
            {
                SetAria2Status("DownloadComponentsInitializing");
                return;
            }

            var available = await probe(ct);
            ct.ThrowIfCancellationRequested();
            if (!_disposed) SetAria2Status(available ? "DownloadAria2Found" : "DownloadAria2Missing");
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) SetAria2Status("DownloadComponentCheckCancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (!_disposed) SetAria2Status("DownloadComponentCheckFailed");
        }
        finally { if (!_disposed) IsProbing = false; }
    }

    private void SetAria2Status(string key)
    {
        _aria2StatusKey = key;
        Aria2Status = Text[key];
    }

    /// <summary>把“使用默认值”按钮的语义固定为清空文本框。</summary>
    public void UseDefaultPaths()
    {
        DownloadCacheDirectory = string.Empty;
        InstallRootDirectory = string.Empty;
    }

    // ── 应用自更新（GitHubStore 更新自身，不是更新目录里的应用） ──────────
    // 状态文案只保存 key 与参数，语言切换时重新求值，避免留下旧语言的句子。
    private readonly CancellationTokenSource _selfUpdateLifetime = new();
    private CancellationTokenSource? _selfUpdateCancellation;
    private SelfUpdateCandidate? _selfUpdateCandidate;
    private string? _selfUpdateStatusKey;
    private object?[] _selfUpdateStatusArgs = [];
    private string? _selfUpdateErrorKey;
    private string? _selfUpdateChecksumName;
    private string? _selfUpdateSha256;
    private string? _preparedInstallerPath;

    /// <summary>当前版本；构建时未注入版本号时显示“未知”，不显示编造值。</summary>
    [ObservableProperty] private string _selfUpdateCurrentVersion = string.Empty;
    [ObservableProperty] private string _selfUpdateStatus = string.Empty;
    [ObservableProperty] private string _selfUpdateError = string.Empty;
    /// <summary>候选版本标签；没有候选时为空。</summary>
    [ObservableProperty] private string _selfUpdateCandidateVersion = string.Empty;
    /// <summary>候选版本的发行说明地址。</summary>
    [ObservableProperty] private string _selfUpdateReleaseNotesUrl = string.Empty;
    /// <summary>实际使用的校验依据（官方发行版资产名与 SHA-256）；未下载时为空。</summary>
    [ObservableProperty] private string _selfUpdateVerificationBasis = string.Empty;
    [ObservableProperty] private double _selfUpdateProgressPercent;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdate), nameof(CanDownloadSelfUpdate),
        nameof(CanInstallSelfUpdate), nameof(HasSelfUpdateCandidate))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelfUpdateCommand))]
    private bool _isSelfUpdateBusy;
    /// <summary>存在可判定的更新候选（同版本/更旧/无法判定都不算）。</summary>
    public bool HasSelfUpdateCandidate => _selfUpdateCandidate?.IsUpdateOffered == true;
    public bool IsSelfUpdateAvailable => _selfUpdate is not null;
    public bool CanCheckForUpdate => _selfUpdate is not null && !_disposed && IsInitialized && !IsSelfUpdateBusy;
    public bool CanDownloadSelfUpdate => CanCheckForUpdate && HasSelfUpdateCandidate;

    /// <summary>已有通过 SHA-256 校验的安装包，才允许启动；未准备时按钮不可用。</summary>
    public bool CanInstallSelfUpdate => IsInitialized && !_disposed && !IsSelfUpdateBusy
        && _selfUpdate?.IsInstallerPrepared == true && _preparedInstallerPath is not null;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdate))]
    private Task CheckForUpdateAsync(CancellationToken ct) => CheckForSelfUpdateAsync(ct);

    [RelayCommand(CanExecute = nameof(CanDownloadSelfUpdate))]
    private Task DownloadSelfUpdateAsync(CancellationToken ct) => DownloadSelfUpdateCoreAsync(ct);

    /// <summary>
    /// 查询官方仓库的最新正式版并与当前版本比较。同版本、更旧与无法判定都如实报告，
    /// 且只有确实更新时才允许下载。
    /// </summary>
    public async Task CheckForSelfUpdateAsync(CancellationToken ct = default)
    {
        if (_disposed || !CanCheckForUpdate) return;
        _selfUpdateErrorKey = null;
        _selfUpdateStatusKey = null;
        _selfUpdateCandidate = null;
        _preparedInstallerPath = null;
        _selfUpdateChecksumName = null;
        _selfUpdateSha256 = null;
        SelfUpdateProgressPercent = 0;
        RefreshSelfUpdateText();
        IsSelfUpdateBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _selfUpdateLifetime.Token);
        _selfUpdateCancellation = cancellation;
        SetSelfUpdateStatus("SelfUpdateChecking");
        try
        {
            var result = await _selfUpdate!.CheckAsync(cancellation.Token);
            if (_disposed) return;
            if (!result.Succeeded || result.Candidate is not { } candidate)
            {
                SetSelfUpdateError(SelfUpdateErrorKey(result.ErrorCode));
                return;
            }
            _selfUpdateCandidate = candidate;
            SelfUpdateCandidateVersion = candidate.Tag;
            SelfUpdateReleaseNotesUrl = candidate.ReleaseNotesUrl;
            SetSelfUpdateStatus(candidate.Relation switch
            {
                VersionRelation.Newer => "SelfUpdateAvailable",
                VersionRelation.Older => "SelfUpdateOlderInstalled",
                VersionRelation.Same => "SelfUpdateUpToDate",
                // Different 表示无法判定：既不说“有更新”，也不谎称“已是最新”。
                _ => "SelfUpdateCannotCompare",
            }, candidate.Tag);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_disposed) SetSelfUpdateError("SelfUpdateCancelled"); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (!_disposed) SetSelfUpdateError("SelfUpdateNetworkError");
        }
        finally
        {
            if (ReferenceEquals(_selfUpdateCancellation, cancellation)) _selfUpdateCancellation = null;
            if (!_disposed)
            {
                IsSelfUpdateBusy = false;
                OnPropertyChanged(nameof(HasSelfUpdateCandidate));
                OnPropertyChanged(nameof(CanInstallSelfUpdate));
                DownloadSelfUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 下载并校验官方安装包到暂存目录。<em>不</em>执行任何安装程序；
    /// 校验依据是官方发行版里的 .sha256 资产，界面会如实展示该依据。
    /// </summary>
    public async Task DownloadSelfUpdateCoreAsync(CancellationToken ct = default)
    {
        if (_disposed || !CanDownloadSelfUpdate) return;
        if (_selfUpdateCandidate is not { IsUpdateOffered: true } candidate) return;
        _selfUpdateErrorKey = null;
        SelfUpdateProgressPercent = 0;
        IsSelfUpdateBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _selfUpdateLifetime.Token);
        _selfUpdateCancellation = cancellation;
        SetSelfUpdateStatus("SelfUpdateDownloading");
        try
        {
            var progress = new InlineProgress<DownloadProgress>(update =>
            {
                if (_disposed) return;
                SelfUpdateProgressPercent = update.Fraction * 100;
            });
            var result = await _selfUpdate!.DownloadAsync(candidate, progress, cancellation.Token);
            if (_disposed) return;
            if (!result.Succeeded || result.InstallerPath is not { } installerPath)
            {
                _selfUpdateChecksumName = null;
                _selfUpdateSha256 = null;
                SetSelfUpdateError(SelfUpdateErrorKey(result.ErrorCode));
                return;
            }
            // 只有通过校验的结果才登记为可启动，并把校验依据原样展示出来。
            _preparedInstallerPath = installerPath;
            _selfUpdateChecksumName = result.VerificationBasisChecksumName;
            _selfUpdateSha256 = result.Sha256;
            SetSelfUpdateStatus("SelfUpdateDownloaded");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_disposed) SetSelfUpdateError("SelfUpdateCancelled"); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (!_disposed) SetSelfUpdateError("SelfUpdateDownloadFailed");
        }
        finally
        {
            if (ReferenceEquals(_selfUpdateCancellation, cancellation)) _selfUpdateCancellation = null;
            if (!_disposed)
            {
                IsSelfUpdateBusy = false;
                RefreshSelfUpdateText();
                OnPropertyChanged(nameof(CanInstallSelfUpdate));
            }
        }
    }

    /// <summary>取消正在进行的自更新检查或下载；不会执行任何安装程序。</summary>
    [RelayCommand]
    private void CancelSelfUpdate() => _selfUpdateCancellation?.Cancel();

    /// <summary>已通过校验的安装包路径；未准备时为 null。视图据此展示确认对话框。</summary>
    public string? PreparedInstallerPath => _selfUpdate?.IsInstallerPrepared == true ? _preparedInstallerPath : null;

    /// <summary>
    /// 在用户<em>明确确认</em>之后启动已校验的安装程序。视图负责先弹出确认对话框；
    /// 本方法自身不弹窗，也不做任何自动执行。
    /// </summary>
    public async Task<bool> InstallPreparedUpdateAsync(CancellationToken ct = default)
    {
        if (_disposed || !CanInstallSelfUpdate || _preparedInstallerPath is not { } installerPath) return false;
        SetSelfUpdateError(null);
        IsSelfUpdateBusy = true;
        try
        {
            var result = await _selfUpdate!.LaunchVerifiedInstallerAsync(installerPath, ct);
            if (_disposed) return false;
            if (!result.Succeeded) { SetSelfUpdateError(SelfUpdateErrorKey(result.ErrorCode)); return false; }
            SetSelfUpdateStatus("SelfUpdateLaunched");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { if (!_disposed) SetSelfUpdateError("SelfUpdateCancelled"); return false; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (!_disposed) SetSelfUpdateError("SelfUpdateLaunchFailed");
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                IsSelfUpdateBusy = false;
                OnPropertyChanged(nameof(CanInstallSelfUpdate));
            }
        }
    }

    private void RefreshSelfUpdateText()
    {
        SelfUpdateCurrentVersion = _selfUpdate?.CurrentVersion is { Length: > 0 } version
            ? Text.Format("SelfUpdateCurrentVersion", version)
            : _selfUpdate is null ? string.Empty : Text["SelfUpdateVersionUnknown"];
        SelfUpdateStatus = ResolveSelfUpdate(_selfUpdateStatusKey, _selfUpdateStatusArgs);
        SelfUpdateError = ResolveSelfUpdate(_selfUpdateErrorKey, []);
        SelfUpdateVerificationBasis = _selfUpdateSha256 is { } sha && _selfUpdateChecksumName is { } name
            ? Text.Format("SelfUpdateVerificationBasis", name, sha) : string.Empty;
    }

    private string ResolveSelfUpdate(string? key, object?[] args) => key is null ? string.Empty
        : args.Length == 0 ? Text[key] : Text.Format(key, args);

    private void SetSelfUpdateStatus(string key, params object?[] args)
    {
        _selfUpdateStatusKey = key;
        _selfUpdateStatusArgs = args;
        SelfUpdateStatus = ResolveSelfUpdate(key, args);
    }

    private void SetSelfUpdateError(string? key)
    {
        _selfUpdateErrorKey = key;
        SelfUpdateError = ResolveSelfUpdate(key, []);
    }

    /// <summary>把自更新错误码映射到本地化文案 key；未知码不臆测原因。</summary>
    private static string SelfUpdateErrorKey(string? errorCode) => errorCode switch
    {
        SelfUpdateErrorCodes.Unavailable => "SelfUpdateUnavailable",
        SelfUpdateErrorCodes.NetworkError => "SelfUpdateNetworkError",
        SelfUpdateErrorCodes.RateLimited => "SelfUpdateRateLimited",
        SelfUpdateErrorCodes.InvalidResponse => "SelfUpdateInvalidResponse",
        SelfUpdateErrorCodes.UnsupportedSource => "SelfUpdateUnsupportedSource",
        SelfUpdateErrorCodes.AccessDenied => "SelfUpdateAccessDenied",
        SelfUpdateErrorCodes.NotStable => "SelfUpdateNotStable",
        SelfUpdateErrorCodes.UnsupportedVersion => "SelfUpdateUnsupportedVersion",
        SelfUpdateErrorCodes.InstallerMissing => "SelfUpdateInstallerMissing",
        SelfUpdateErrorCodes.ChecksumAssetMissing => "SelfUpdateNoChecksum",
        SelfUpdateErrorCodes.ChecksumAssetInvalid => "SelfUpdateChecksumInvalid",
        SelfUpdateErrorCodes.AssetRejected => "SelfUpdateAssetRejected",
        SelfUpdateErrorCodes.ChecksumMismatch => "SelfUpdateChecksumMismatch",
        SelfUpdateErrorCodes.StagingFailed => "SelfUpdateStagingFailed",
        SelfUpdateErrorCodes.DownloadFailed => "SelfUpdateDownloadFailed",
        SelfUpdateErrorCodes.NotOffered => "SelfUpdateNotOffered",
        SelfUpdateErrorCodes.NotPrepared => "SelfUpdateNotPrepared",
        SelfUpdateErrorCodes.LaunchFailed => "SelfUpdateLaunchFailed",
        SelfUpdateErrorCodes.Cancelled => "SelfUpdateCancelled",
        _ => "SelfUpdateDownloadFailed",
    };

    /// <summary>进度直接回调，不依赖捕获的同步上下文；测试因此可确定地断言。</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetError(string? key)
    {
        _errorKey = key;
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasFeedback));
    }

    private void SetStatus(string? key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasFeedback));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(StatusMessage));
        if (_aria2StatusKey is { } key) Aria2Status = Text[key];
        // 默认目录提示包含本地化前缀，语言切换后需要重新求值。
        OnPropertyChanged(nameof(DefaultCacheHint));
        OnPropertyChanged(nameof(DefaultInstallHint));
        OnPropertyChanged(nameof(CacheUsageText));
        CacheStatus = string.Empty;
        CacheError = string.Empty;
        OnPropertyChanged(nameof(CatalogLibraryStatus));
        OnPropertyChanged(nameof(CatalogLibraryError));
        // 自更新文案含本地化句子与格式化参数，语言切换后必须整段重新求值。
        RefreshSelfUpdateText();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _libraryEditor?.Dispose();
        _catalogLibraryLifetime.Cancel();
        _catalogLibraryLifetime.Dispose();
        _selfUpdateCancellation?.Cancel();
        _selfUpdateLifetime.Cancel();
        _selfUpdateLifetime.Dispose();
        Text.LanguageChanged -= OnLanguageChanged;
    }
}
