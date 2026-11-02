using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>
/// UI-thread presentation for the launcher. Launches by saved ID, never by UI-supplied path.
/// Installed applications use catalog categories; manual entries use the category selected when added.
/// Manual entries are kept separately from installation receipts and do not grant uninstall ownership.
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    private readonly IManagedAppLauncher _launcher;
    private readonly ICatalogService? _catalog;
    private readonly IManualAppLauncher? _manualLauncher;
    private readonly ILauncherCustomizationStore? _customizationStore;
    private readonly ILauncherHotkeyRegistry? _hotkeys;
    private readonly ILauncherProgramResolver _programResolver;
    private Dictionary<(bool IsManual, string Id), LauncherCustomization> _customizations = [];
    private Dictionary<(bool IsManual, string Id), LauncherItemViewModel> _items = [];
    private int _customizationVersion;
    private IReadOnlyList<ManualLauncherEntry> _manualApps = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<AppId, CategoryId?> _categories = [];
    private IReadOnlyList<Category> _categoryOrder = [];
    private IReadOnlyList<LauncherApp> _allApps = [];
    private int _refreshVersion;
    private bool _disposed;
    private CancellationTokenSource? _refreshCancellation;

    public LauncherViewModel(IManagedAppLauncher launcher, LocalizationService text, ICatalogService? catalog = null,
        IManualAppLauncher? manualLauncher = null, ILauncherCustomizationStore? customizationStore = null,
        ILauncherHotkeyRegistry? hotkeys = null, ILauncherProgramResolver? programResolver = null)
    {
        _launcher = launcher;
        _catalog = catalog;
        _manualLauncher = manualLauncher;
        _customizationStore = customizationStore;
        _hotkeys = hotkeys;
        _programResolver = programResolver ?? new Services.Launcher.LauncherProgramResolver();
        Text = text;
        Text.LanguageChanged += OnLanguageChanged;
        if (_hotkeys is not null) _hotkeys.Triggered += OnHotkeyTriggered;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Hotkey callbacks arrive on the UI thread; launch failures are surfaced through view-model state.")]
    private async void OnHotkeyTriggered(object? sender, LauncherHotkeyEventArgs args)
    {
        if (_items.TryGetValue((args.IsManual, args.EntryId), out var item))
            await LaunchEntryAsync(item, _lifetime.Token);
    }

    public LocalizationService Text { get; }

    /// <summary>Flat, search-filtered list; kept for callers that need a plain sequence.</summary>
    public ObservableCollection<LauncherItemViewModel> Apps { get; } = [];

    /// <summary>Search-filtered applications grouped by catalog category, in category display order.</summary>
    public ObservableCollection<LauncherCategoryGroup> Groups { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLaunching;
    [ObservableProperty] private bool _isAddingManual;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    public bool IsEmpty => Apps.Count == 0;
    public string EmptyMessage => Text[_allApps.Count + _manualApps.Count == 0 ? "LauncherEmpty" : "LauncherNoMatches"];
    public string CountText => Text.Format("LauncherCount", Apps.Count, _allApps.Count + _manualApps.Count);

    partial void OnSearchTextChanged(string value) => RebuildItems();

    [RelayCommand]
    private Task RefreshAsync(CancellationToken ct) => RefreshEntriesAsync(ct);

    public async Task RefreshEntriesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previousRefresh = _refreshCancellation;
        _refreshCancellation = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        if (previousRefresh is not null) await previousRefresh.CancelAsync();
        _refreshCancellation = cancellation;
        var version = ++_refreshVersion;
        IsRefreshing = true;
        ErrorMessage = string.Empty;
        try
        {
            var entries = await _launcher.ListAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _refreshVersion) return;
            var manualEntries = _manualLauncher is null ? Array.Empty<ManualLauncherEntry>()
                : await _manualLauncher.ListAsync(cancellation.Token);
            var customizationVersion = _customizationVersion;
            IReadOnlyList<LauncherCustomization> customizations = _customizations.Values.ToArray();
            if (_customizationStore is not null)
            {
                try { customizations = await _customizationStore.ListAsync(cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception) { ErrorMessage = Text["LauncherCustomizationFailed"]; }
            }
            await ResolveCategoriesAsync(entries, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _refreshVersion) return;
            _allApps = entries;
            _manualApps = manualEntries;
            // A launch may finish while a refresh is reading its earlier snapshot.
            if (customizationVersion == _customizationVersion)
                _customizations = customizations.ToDictionary(entry => (entry.IsManual, entry.EntryId));
            RebuildItems();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && version == _refreshVersion) ErrorMessage = ErrorText(ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
            if (!_disposed && version == _refreshVersion) IsRefreshing = false;
        }
    }

    public async Task LaunchEntryAsync(LauncherItemViewModel item, CancellationToken ct = default)
    {
        if (_disposed || IsLaunching) return;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        if (!item.IsAvailable)
        {
            ErrorMessage = item.RequiresRecovery ? Text["LauncherRecoveryRequired"] : Text["LauncherExecutableMissing"];
            return;
        }
        IsLaunching = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            var custom = LauncherEntryOptionsValidation.RequiresCustomLaunch(item.Options);
            var result = item.IsManual && _manualLauncher is not null
                ? custom
                    ? await _manualLauncher.LaunchWithOptionsAsync(item.InstallId, item.Options, cancellation.Token)
                    : await _manualLauncher.LaunchAsync(item.InstallId, cancellation.Token)
                : item.IsManual ? new LauncherLaunchResult(false, "launcher_not_found")
                : custom
                    ? await _launcher.LaunchWithOptionsAsync(item.InstallId, item.Options, cancellation.Token)
                    : await _launcher.LaunchAsync(item.InstallId, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            if (result.Started)
            {
                StatusMessage = Text.Format("LauncherStarted", item.DisplayName);
                if (_customizationStore is not null)
                {
                    try
                    {
                        var saved = await _customizationStore.RecordLaunchAsync(item.IsManual, item.InstallId, cancellation.Token);
                        if (_disposed || cancellation.IsCancellationRequested) return;
                        _customizations[(item.IsManual, item.InstallId)] = saved;
                        _customizationVersion++;
                        RebuildItems();
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception) { if (!_disposed) ErrorMessage = Text["LauncherLaunchCountSaveFailed"]; }
                }
            }
            else ErrorMessage = ErrorCodeText(result.ErrorCode);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) ErrorMessage = ErrorText(ex); }
        finally { if (!_disposed) IsLaunching = false; }
    }

    public async Task<IReadOnlyList<Category>> GetManualCategoriesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_catalog is null) return [];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _categoryOrder = await _catalog.ListCategoriesAsync(cancellation.Token);
        return _categoryOrder;
    }

    public Task<LauncherProgramTarget> ResolveProgramAsync(string path, CancellationToken ct = default) =>
        _programResolver.ResolveAsync(path, ct);

    public Task<bool> AddManualEntryAsync(string path, string displayName, CategoryId? category,
        CancellationToken ct = default) => AddManualEntryAsync(path, displayName, category, string.Empty, ct);

    public async Task<bool> AddManualEntryAsync(string path, string displayName, CategoryId? category,
        string arguments, CancellationToken ct = default)
    {
        if (_disposed || IsAddingManual || _manualLauncher is null) return false;
        IsAddingManual = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            var target = await ResolveProgramAsync(path, cancellation.Token);
            var effectiveArguments = JoinArguments(target.Arguments, arguments);
            var options = new LauncherEntryOptions { Arguments = effectiveArguments };
            if (!LauncherEntryOptionsValidation.IsValid(options)) throw new LauncherException("manual_launcher_invalid_options");
            // Path and arguments share one atomic manifest write. Duplicate targets keep their saved settings.
            await _manualLauncher.AddProgramAsync(target.Path, displayName.Trim(), category, effectiveArguments, cancellation.Token);
            // Keep the new entry visible even if a previous search was active.
            SearchText = string.Empty;
            await RefreshEntriesAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            StatusMessage = Text.Format("LauncherManualAdded", displayName.Trim());
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (!_disposed) ReportManualError(ex); return false; }
        finally { if (!_disposed) IsAddingManual = false; }
    }

    public async Task RemoveManualEntryAsync(LauncherItemViewModel item, CancellationToken ct = default)
    {
        if (_disposed || !item.IsManual || _manualLauncher is null || IsAddingManual) return;
        IsAddingManual = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await _manualLauncher.RemoveAsync(item.InstallId, cancellation.Token);
            await RefreshEntriesAsync(cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested)
                StatusMessage = Text.Format("LauncherManualRemoved", item.DisplayName);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) ReportManualError(ex); }
        finally { if (!_disposed) IsAddingManual = false; }
    }

    private static string JoinArguments(string original, string extra) =>
        original.Length == 0 ? extra : extra.Length == 0 ? original : original + " " + extra;

    /// <summary>The simplified editor changes only the launch target and arguments, preserving metadata.</summary>
    public async Task<bool> SaveLaunchPropertiesAsync(LauncherItemViewModel item, string path, string arguments,
        CancellationToken ct = default)
    {
        if (_disposed || IsAddingManual) return false;
        if (!item.IsManual)
            return await SaveEntryPropertiesAsync(item, item.DisplayName, item.Category, path,
                item.Options with { Arguments = arguments }, ct);
        IsAddingManual = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            if (_manualLauncher is null) throw new LauncherException("manual_launcher_unsupported");
            var target = await ResolveProgramAsync(path, cancellation.Token);
            await _manualLauncher.UpdateProgramAsync(item.InstallId, target.Path,
                JoinArguments(target.Arguments, arguments), cancellation.Token);
            SearchText = string.Empty;
            await RefreshEntriesAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            StatusMessage = Text.Format("LauncherPropertiesSaved", item.DisplayName);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (!_disposed) ReportManualError(ex); return false; }
        finally { if (!_disposed) IsAddingManual = false; }
    }

    public Task<bool> SaveEntryPropertiesAsync(LauncherItemViewModel item, string displayName,
        CategoryId? category, string path, CancellationToken ct = default) =>
        SaveEntryPropertiesAsync(item, displayName, category, path, item.Options, ct);

    public async Task<bool> SaveEntryPropertiesAsync(LauncherItemViewModel item, string displayName,
        CategoryId? category, string path, LauncherEntryOptions options, CancellationToken ct = default)
    {
        if (_disposed || IsAddingManual) return false;
        IsAddingManual = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            if (!LauncherEntryOptionsValidation.IsValid(options)) throw new LauncherException("manual_launcher_invalid_options");
            if (item.IsManual)
            {
                if (_manualLauncher is null) throw new LauncherException("manual_launcher_unsupported");
                await _manualLauncher.UpdateAsync(item.InstallId, path, displayName.Trim(), category, cancellation.Token);
                // Manual identity lives in its own index; only the launcher options live here.
                if (_customizationStore is not null)
                {
                    var savedManual = await _customizationStore.SavePropertiesAsync(true, item.InstallId,
                        displayName.Trim(), category, options, cancellation.Token);
                    if (_disposed || cancellation.IsCancellationRequested) return false;
                    _customizations[(true, item.InstallId)] = savedManual;
                    _customizationVersion++;
                }
            }
            else
            {
                if (_customizationStore is null) throw new LauncherException("launcher_customization_unsupported");
                // Installed entries keep their trusted executable in the installation receipt.
                if (!string.Equals(path, item.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException("launcher_invalid_path");
                var saved = await _customizationStore.SavePropertiesAsync(false, item.InstallId,
                    displayName.Trim(), category, options, cancellation.Token);
                if (_disposed || cancellation.IsCancellationRequested) return false;
                _customizations[(false, item.InstallId)] = saved;
                _customizationVersion++;
            }
            SearchText = string.Empty;
            await RefreshEntriesAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            StatusMessage = Text.Format("LauncherPropertiesSaved", displayName.Trim());
            if (_hotkeys is not null && !string.IsNullOrWhiteSpace(options.Hotkey)
                && !_hotkeys.TrySet(item.IsManual, item.InstallId, options.Hotkey))
                ErrorMessage = Text["LauncherHotkeyUnavailable"];
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (!_disposed) ReportManualError(ex); return false; }
        finally { if (!_disposed) IsAddingManual = false; }
    }

    public void ReportManualError(Exception ex) => ErrorMessage = ex is LauncherException launcherError
        ? ErrorCodeText(launcherError.Code) : Text["LauncherManualFailed"];

    /// <summary>
    /// Resolves each installation's category from the local catalog. Failures are treated as
    /// uncategorized; grouping never invents a category or reads it from the install path.
    /// </summary>
    private async Task ResolveCategoriesAsync(IReadOnlyList<LauncherApp> entries, CancellationToken ct)
    {
        if (_catalog is null) return;
        if (_categoryOrder.Count == 0)
        {
            try { _categoryOrder = await _catalog.ListCategoriesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { _categoryOrder = []; }
        }
        foreach (var app in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var detail = await _catalog.GetAppAsync(app.AppId, ct);
                ct.ThrowIfCancellationRequested();
                _categories[app.AppId] = detail.App.PrimaryCategory;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { _categories[app.AppId] = null; }
        }
    }

    private void RebuildItems()
    {
        if (_disposed) return;
        var query = SearchText.Trim();
        LauncherCustomization? Customization(bool manual, string id) =>
            _customizations.TryGetValue((manual, id), out var value) ? value : null;
        var entries = _allApps.Select(app => new LauncherItemViewModel(app, Text,
                Customization(false, app.InstallId), _categories.TryGetValue(app.AppId, out var category) ? category : null))
            .Concat(_manualApps.Select(app => new LauncherItemViewModel(app, Text, Customization(true, app.Id))));
        var matched = entries.Where(item => query.Length == 0
            || item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.SearchKey.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.LaunchCount)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.IsManual)
            .ThenBy(item => item.InstallId, StringComparer.Ordinal)
            .ToArray();
        Apps.Clear();
        Groups.Clear();
        _items = entries.ToDictionary(item => (item.IsManual, item.InstallId));
        foreach (var item in matched) Apps.Add(item);
        var grouped = matched
            .GroupBy(item => item.Category)
            .Select(group => (Category: group.Key, Order: CategoryOrder(group.Key), Items: group.ToArray()))
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Category?.Value ?? string.Empty, StringComparer.Ordinal);
        foreach (var (category, _, items) in grouped)
        {
            var name = category is { } id ? Text[$"category.{id.Value}"] : Text["LauncherUncategorized"];
            Groups.Add(new LauncherCategoryGroup(name, Text.Format("LauncherGroupCount", items.Length),
                Array.AsReadOnly(items), Text["LauncherSortByLaunchCount"]));
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(CountText));
        SyncHotkeys(_items.Values.ToArray());
    }

    /// <summary>Keeps native registrations aligned with the saved per-entry shortcuts.</summary>
    private void SyncHotkeys(IReadOnlyList<LauncherItemViewModel> items)
    {
        if (_hotkeys is null) return;
        try
        {
            _hotkeys.Retain(items.Select(item => (item.IsManual, item.InstallId)).ToArray());
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Options.Hotkey))
                    _hotkeys.TrySet(item.IsManual, item.InstallId, item.Options.Hotkey);
            }
        }
        catch (Exception) { /* A hotkey problem must never break the launcher list. */ }
    }

    private int CategoryOrder(CategoryId? category)
    {
        if (category is not { } id) return int.MaxValue;
        for (var index = 0; index < _categoryOrder.Count; index++)
            if (_categoryOrder[index].Id == id) return index;
        // A category that is no longer offered still groups separately, after the known categories.
        return int.MaxValue;
    }

    private string ErrorText(Exception ex) => ex is LauncherException launcherError
        ? ErrorCodeText(launcherError.Code) : Text["LauncherLoadFailed"];

    private string ErrorCodeText(string? code) => Text[code switch
    {
        "manual_launcher_invalid_path" or "manual_launcher_unsafe_path" or "manual_launcher_path_unavailable" => "LauncherManualInvalidPath",
        "manual_launcher_shortcut_invalid" => "LauncherShortcutInvalid",
        "manual_launcher_invalid_display_name" or "launcher_customization_invalid_display_name" => "LauncherManualInvalidName",
        "manual_launcher_duplicate_path" => "LauncherManualDuplicatePath",
        "manual_launcher_update_unsupported" or "manual_launcher_unsupported" => "LauncherManualFailed",
        var customizationCode when customizationCode?.StartsWith("launcher_customization_", StringComparison.Ordinal) == true
            => "LauncherCustomizationFailed",
        "manual_launcher_entry_missing" => "LauncherExecutableMissing",
        "manual_launcher_not_found" or "manual_launcher_invalid_id" => "LauncherEntryMissing",
        "manual_launcher_access_denied" => "LauncherAccessDenied",
        "manual_launcher_invalid_manifest" or "manual_launcher_manifest_too_large" => "LauncherManualRegistryInvalid",
        "manual_launcher_read_failed" or "manual_launcher_write_failed" or "manual_launcher_invalid_category" => "LauncherManualFailed",
        "launcher_recovery_required" => "LauncherRecoveryRequired",
        "launcher_executable_missing" => "LauncherExecutableMissing",
        "launcher_invalid_path" or "launcher_unsafe_path" or "launcher_path_unavailable" or "launcher_invalid_installation" => "LauncherInvalidPath",
        "launcher_not_found" => "LauncherEntryMissing",
        "launcher_access_denied" => "LauncherAccessDenied",
        "launcher_invalid_manifest" or "launcher_manifest_too_large" => "LauncherRegistryInvalid",
        "launcher_start_failed" or "manual_launcher_start_failed" => "LauncherLaunchFailed",
        "launcher_invalid_options" or "manual_launcher_invalid_options" or "launcher_customization_invalid_options"
            or "launcher_customization_invalid_color" or "launcher_customization_invalid_hotkey" => "LauncherInvalidOptions",
        "launcher_invalid_working_directory" or "manual_launcher_invalid_working_directory" => "LauncherInvalidWorkingDirectory",
        "launcher_launch_cancelled" or "manual_launcher_launch_cancelled" => "LauncherLaunchCancelled",
        "launcher_hotkey_invalid_window" or "launcher_hotkey_subclass_failed" or "launcher_hotkey_not_attached" => "LauncherHotkeyUnavailable",
        _ => "LauncherLoadFailed",
    }];

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        StatusMessage = string.Empty;
        ErrorMessage = string.Empty;
        RebuildItems();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Text.LanguageChanged -= OnLanguageChanged;
        if (_hotkeys is not null) _hotkeys.Triggered -= OnHotkeyTriggered;
        _lifetime.Cancel();
        _refreshCancellation?.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>One category section on the home page; ordering follows the catalog, uncategorized last.</summary>
public sealed class LauncherCategoryGroup(string name, string countText, IReadOnlyList<LauncherItemViewModel> apps,
    string sortDescription = "")
{
    public string Name { get; } = name;
    public string CountText { get; } = countText;
    public IReadOnlyList<LauncherItemViewModel> Apps { get; } = apps;
    public string SortDescription { get; } = sortDescription;
}

public sealed class LauncherItemViewModel
{
    private readonly LocalizationService _text;

    public LauncherItemViewModel(LauncherApp app, LocalizationService text,
        LauncherCustomization? customization = null, CategoryId? category = null)
    {
        _text = text;
        InstallId = app.InstallId;
        DisplayName = customization?.DisplayName ?? app.DisplayName;
        Category = customization is { HasCategoryOverride: true } ? customization.Category : category;
        LaunchCount = customization?.LaunchCount ?? 0;
        ExecutablePath = app.ExecutablePath;
        IsAvailable = app.IsAvailable;
        Version = app.DisplayVersion ?? string.Empty;
        RequiresRecovery = app.Status == InstalledStatus.RecoveryRequired;
        SearchKey = app.AppId.PackageId.Value;
        Options = customization?.Options ?? new LauncherEntryOptions();
    }

    public LauncherItemViewModel(ManualLauncherEntry entry, LocalizationService text,
        LauncherCustomization? customization = null)
    {
        _text = text;
        InstallId = entry.Id;
        DisplayName = entry.DisplayName;
        Category = entry.Category;
        LaunchCount = customization?.LaunchCount ?? 0;
        ExecutablePath = entry.Path;
        IsAvailable = entry.IsAvailable;
        SearchKey = entry.Path;
        IsManual = true;
        var legacyOptions = customization?.Options ?? new LauncherEntryOptions();
        Options = entry.Arguments is null ? legacyOptions : legacyOptions with { Arguments = entry.Arguments };
    }

    public string InstallId { get; }
    public string DisplayName { get; }
    public string ExecutablePath { get; }
    public bool IsAvailable { get; }
    public string Version { get; } = string.Empty;
    public bool RequiresRecovery { get; }
    public bool IsManual { get; }
    public CategoryId? Category { get; }
    public long LaunchCount { get; }
    public LauncherEntryOptions Options { get; }
    public string? Color => Options.Color;
    public string LaunchCountText => _text.Format("LauncherLaunchCount", LaunchCount);
    internal string SearchKey { get; }
    public string SourceLabel => _text[IsManual ? "LauncherManualSource" : "LauncherStoreSource"];
    public string AvailabilityText => RequiresRecovery ? _text["LauncherRecoveryRequired"]
        : IsAvailable ? _text["LauncherLaunch"] : _text["LauncherUnavailable"];
    public string Tooltip => DisplayName + "\n" + ExecutablePath + "\n" + LaunchCountText + "\n" + AvailabilityText;
}
