using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>
/// UI-thread presentation for the home launcher. Launches by registered ID, never by UI-supplied path.
/// Installed applications are grouped by the category recorded in the local catalog; an application that
/// is no longer in the catalog is shown as uncategorized rather than guessed from its folder name.
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    private readonly IManagedAppLauncher _launcher;
    private readonly ICatalogService? _catalog;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<AppId, CategoryId?> _categories = [];
    private IReadOnlyList<Category> _categoryOrder = [];
    private IReadOnlyList<LauncherApp> _allApps = [];
    private int _refreshVersion;
    private bool _disposed;
    private CancellationTokenSource? _refreshCancellation;

    public LauncherViewModel(IManagedAppLauncher launcher, LocalizationService text, ICatalogService? catalog = null)
    {
        _launcher = launcher;
        _catalog = catalog;
        Text = text;
        Text.LanguageChanged += OnLanguageChanged;
    }

    public LocalizationService Text { get; }

    /// <summary>Flat, search-filtered list; kept for callers that need a plain sequence.</summary>
    public ObservableCollection<LauncherItemViewModel> Apps { get; } = [];

    /// <summary>Search-filtered applications grouped by catalog category, in category display order.</summary>
    public ObservableCollection<LauncherCategoryGroup> Groups { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLaunching;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    public bool IsEmpty => Apps.Count == 0;
    public string EmptyMessage => Text[_allApps.Count == 0 ? "LauncherEmpty" : "LauncherNoMatches"];
    public string CountText => Text.Format("LauncherCount", Apps.Count, _allApps.Count);

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
            _allApps = entries;
            await ResolveCategoriesAsync(entries, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _refreshVersion) return;
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
            ErrorMessage = Text["LauncherExecutableMissing"];
            return;
        }
        IsLaunching = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            var result = await _launcher.LaunchAsync(item.InstallId, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            if (result.Started) StatusMessage = Text.Format("LauncherStarted", item.DisplayName);
            else ErrorMessage = ErrorCodeText(result.ErrorCode);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) ErrorMessage = ErrorText(ex); }
        finally { if (!_disposed) IsLaunching = false; }
    }

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
        var matched = _allApps.Where(app => query.Length == 0
            || app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || app.AppId.PackageId.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Apps.Clear();
        Groups.Clear();
        foreach (var app in matched) Apps.Add(new LauncherItemViewModel(app, Text));
        var grouped = matched
            .GroupBy(app => _categories.TryGetValue(app.AppId, out var category) ? category : null)
            .Select(group => (Category: group.Key, Order: CategoryOrder(group.Key), Items: group.ToArray()))
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Category?.Value ?? string.Empty, StringComparer.Ordinal);
        foreach (var (category, _, items) in grouped)
        {
            var name = category is { } id ? Text[$"category.{id.Value}"] : Text["LauncherUncategorized"];
            Groups.Add(new LauncherCategoryGroup(name, Text.Format("LauncherGroupCount", items.Length),
                Array.AsReadOnly(items.Select(item => new LauncherItemViewModel(item, Text)).ToArray())));
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(CountText));
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
        "launcher_executable_missing" => "LauncherExecutableMissing",
        "launcher_invalid_path" or "launcher_unsafe_path" or "launcher_path_unavailable" or "launcher_invalid_installation" => "LauncherInvalidPath",
        "launcher_not_found" => "LauncherEntryMissing",
        "launcher_access_denied" => "LauncherAccessDenied",
        "launcher_invalid_manifest" or "launcher_manifest_too_large" => "LauncherRegistryInvalid",
        "launcher_start_failed" => "LauncherLaunchFailed",
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
        _lifetime.Cancel();
        _refreshCancellation?.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>One category section on the home page; ordering follows the catalog, uncategorized last.</summary>
public sealed class LauncherCategoryGroup(string name, string countText, IReadOnlyList<LauncherItemViewModel> apps)
{
    public string Name { get; } = name;
    public string CountText { get; } = countText;
    public IReadOnlyList<LauncherItemViewModel> Apps { get; } = apps;
}

public sealed class LauncherItemViewModel(LauncherApp app, LocalizationService text)
{
    public string InstallId => app.InstallId;
    public string DisplayName => app.DisplayName;
    public string ExecutablePath => app.ExecutablePath;
    public bool IsAvailable => app.IsAvailable;
    public string Version => app.DisplayVersion ?? string.Empty;
    public string AvailabilityText => app.IsAvailable ? text["LauncherLaunch"] : text["LauncherUnavailable"];
    public string Tooltip => app.DisplayName + "\n" + app.ExecutablePath;
}
