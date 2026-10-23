using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;

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

    public SettingsViewModel(PreferencesService preferences, LocalizationService text,
        IManagedAppLauncher? managedApps = null, InstallationViewModel? installation = null,
        ICatalogLibrarySyncService? librarySync = null, PackageCacheService? packageCache = null)
    {
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
            OnPropertyChanged(nameof(IsInitialized));
            OnPropertyChanged(nameof(CanClearCache));
            ClearCacheCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanEditProxy));
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _catalogLibraryLifetime.Cancel();
        _catalogLibraryLifetime.Dispose();
        Text.LanguageChanged -= OnLanguageChanged;
    }
}
