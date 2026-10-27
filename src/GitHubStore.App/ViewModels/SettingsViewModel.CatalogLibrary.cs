using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly ICatalogLibrarySyncService? _librarySync;
    private readonly CancellationTokenSource _catalogLibraryLifetime = new();
    private CancellationTokenSource? _catalogSyncCancellation;
    private string? _catalogLibraryErrorKey;
    private string? _catalogLibraryStatusKey;
    private int _catalogLibraryCount;
    private int _catalogLibraryMergedCount;
    private DateTimeOffset? _catalogLibrarySyncedAt;

    [ObservableProperty] private string _catalogLibraryUrl = string.Empty;
    [ObservableProperty] private bool _syncCatalogLibraryOnStartup = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanEditProxy), nameof(CatalogLibraryStatus))]
    private bool _isSynchronizingCatalogLibrary;

    public string CatalogLibraryError => _catalogLibraryErrorKey is { } key ? Text[key] : string.Empty;
    public string CatalogLibraryStatus => IsSynchronizingCatalogLibrary ? Text["SettingsCatalogLibrarySyncing"]
        : _catalogLibraryStatusKey switch
        {
            null => Text["SettingsCatalogLibraryIdle"],
            // 合并过重复的手动记录时单独说明，避免用户以为手动添加的软件被同步删掉了。
            "SettingsCatalogLibrarySynced" when _catalogLibraryMergedCount > 0 => Text.Format("SettingsCatalogLibrarySyncedMerged",
                _catalogLibraryCount, _catalogLibrarySyncedAt?.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(Text.LanguageCode)),
                _catalogLibraryMergedCount),
            { } key => Text.Format(key, _catalogLibraryCount, _catalogLibrarySyncedAt?.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(Text.LanguageCode))),
        };

    public event EventHandler? CatalogLibrarySynchronized;

    private void RestoreCatalogLibrary()
    {
        CatalogLibraryUrl = Preferences.Current.CatalogLibraryUrl ?? string.Empty;
        SyncCatalogLibraryOnStartup = Preferences.Current.SyncCatalogLibraryOnStartup;
    }

    public Task ApplyCatalogLibraryAsync(CancellationToken ct = default) => ApplyCoreAsync(
        () => Preferences.Current with
        {
            CatalogLibraryUrl = PreferencesService.NormalizeCatalogLibraryUrl(CatalogLibraryUrl),
            // 清空地址是明确的停用动作：记录该选择，避免被默认地址悄悄覆盖。
            CatalogLibraryOptedOut = string.IsNullOrWhiteSpace(CatalogLibraryUrl),
            SyncCatalogLibraryOnStartup = SyncCatalogLibraryOnStartup,
        }, RestoreCatalogLibrary, validateNetwork: false, ct);

    [RelayCommand]
    private Task SaveCatalogLibraryAsync(CancellationToken ct) => ApplyCatalogLibraryAsync(ct);

    [RelayCommand]
    private Task SynchronizeCatalogLibraryAsync(CancellationToken ct) => SyncCatalogLibraryAsync(ct);

    /// <summary>Manual sync saves this section first, without submitting other settings drafts.</summary>
    public async Task SyncCatalogLibraryAsync(CancellationToken ct = default)
    {
        if (_disposed || !CanApply) return;
        await ApplyCatalogLibraryAsync(ct);
        if (_disposed || _errorKey is not null) return;
        await SynchronizeSavedCatalogLibraryAsync(ct);
    }

    /// <summary>Startup uses the persisted address, and never saves a draft or blocks initial catalog rendering.</summary>
    public Task SyncCatalogLibraryAtStartupAsync(CancellationToken ct = default) =>
        Preferences.Current.SyncCatalogLibraryOnStartup && !string.IsNullOrWhiteSpace(Preferences.Current.CatalogLibraryUrl)
            ? SynchronizeSavedCatalogLibraryAsync(ct) : Task.CompletedTask;

    private async Task SynchronizeSavedCatalogLibraryAsync(CancellationToken ct)
    {
        if (_disposed || !CanApply) return;
        _catalogLibraryErrorKey = null;
        _catalogLibraryStatusKey = null;
        OnPropertyChanged(nameof(CatalogLibraryError));
        var address = Preferences.Current.CatalogLibraryUrl;
        if (string.IsNullOrWhiteSpace(address))
        {
            SetCatalogLibraryError("SettingsCatalogLibraryUrlRequired");
            return;
        }
        if (_librarySync is null)
        {
            SetCatalogLibraryError("SettingsCatalogLibraryUnavailable");
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalogLibraryLifetime.Token);
        _catalogSyncCancellation = cancellation;
        IsSynchronizingCatalogLibrary = true;
        try
        {
            var result = await _librarySync.SyncLibraryAsync(address, cancellation.Token);
            if (_disposed) return;
            _catalogLibraryCount = result.AppCount;
            _catalogLibraryMergedCount = result.MergedManualCount;
            _catalogLibrarySyncedAt = result.SyncedAt;
            _catalogLibraryStatusKey = "SettingsCatalogLibrarySynced";
            // A view refresh failure cannot turn a committed catalog into a failed synchronization.
            if (CatalogLibrarySynchronized is { } handlers)
                foreach (EventHandler handler in handlers.GetInvocationList())
                    try { handler(this, EventArgs.Empty); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) _catalogLibraryStatusKey = "SettingsCatalogLibraryCancelled";
        }
        catch (RepositoryCatalogException ex)
        {
            if (!_disposed) SetCatalogLibraryError(ex.Code switch
            {
                "invalid_library_url" => "SettingsCatalogLibraryUrlInvalid",
                "invalid_response" => "SettingsCatalogLibraryInvalidFile",
                "request_timeout" => "SettingsCatalogLibraryTimeout",
                "cache_write_failed" => "SettingsCatalogLibraryWriteFailed",
                _ => "SettingsCatalogLibraryNetworkFailed",
            });
        }
        catch (Exception)
        {
            if (!_disposed) SetCatalogLibraryError("SettingsCatalogLibraryFailed");
        }
        finally
        {
            if (ReferenceEquals(_catalogSyncCancellation, cancellation)) _catalogSyncCancellation = null;
            if (!_disposed)
            {
                IsSynchronizingCatalogLibrary = false;
                OnPropertyChanged(nameof(CatalogLibraryStatus));
            }
        }
    }

    [RelayCommand]
    private void CancelCatalogLibrarySync() => _catalogSyncCancellation?.Cancel();

    private void SetCatalogLibraryError(string key)
    {
        _catalogLibraryErrorKey = key;
        OnPropertyChanged(nameof(CatalogLibraryError));
        OnPropertyChanged(nameof(CatalogLibraryStatus));
    }
}
