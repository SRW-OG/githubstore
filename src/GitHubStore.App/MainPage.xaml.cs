using GitHubStore.App.ViewModels;
using GitHubStore.App.Views;
using GitHubStore.Core.Abstractions;
using GitHubStore.Platform.Win32.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubStore.App;

public sealed partial class MainPage : Page, IDisposable
{
    private readonly ShellViewModel _viewModel;
    private readonly AddRepositoryViewModel _addRepository;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private bool _disposed;
    // 重建分类项时 NavigationView 会自行选中新项，必须忽略这次非用户选择。
    private bool _suppressNavigationSelection;

    public MainPage(ShellViewModel viewModel, AddRepositoryViewModel addRepository, WindowsRuntimeInfo runtime,
        IDownloadService downloads, IManagedAppLauncher launcherRegistry)
    {
        _viewModel = viewModel;
        _addRepository = addRepository;
        _viewModel.RuntimeDescription =
            $"{runtime.OperatingSystemDescription} · {runtime.Architecture} · {runtime.FrameworkDescription}";
        InitializeComponent();
        DataContext = viewModel;
        PreferencesView.Aria2Probe = downloads.ProbeAria2Async;
        viewModel.Settings.RuntimeDescription = viewModel.RuntimeDescription;
        Launcher.Attach(launcherRegistry);
        viewModel.Settings.CatalogLibrarySynchronized += OnCatalogLibrarySynchronized;
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            await _viewModel.Settings.InitializeAsync(_lifetime.Token);
            if (_disposed) return;
            _ = InitializeDiscoveryAsync();
            await _viewModel.Launcher.RefreshEntriesAsync(_lifetime.Token);
            if (_disposed) return;
            await _viewModel.Catalog.InitializeAsync(_lifetime.Token);
            if (_disposed) return;
            RebuildCategoryItems();
            _viewModel.Text.LanguageChanged += OnLanguageChanged;
            _ = _viewModel.Settings.SyncCatalogLibraryAtStartupAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _viewModel.Catalog.ErrorMessage = _viewModel.Catalog.FormatError("LoadFailed", ex);
        }
        finally
        {
            if (!_disposed) Navigation.IsEnabled = true;
        }
    }

    private async Task InitializeDiscoveryAsync()
    {
        try { await _viewModel.Home.InitializeAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "目录同步完成的 UI 事件，异常在处理器内捕获。")]
    private async void OnCatalogLibrarySynchronized(object? sender, EventArgs args)
    {
        if (_disposed) return;
        try
        {
            await _viewModel.Catalog.LoadAsync(_lifetime.Token);
            if (!_disposed) await _viewModel.Launcher.RefreshEntriesAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

    private void OnExploreRequested(object? sender, EventArgs args)
    {
        if (!_disposed && _initialized)
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => item.Tag as string == "ranking");
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        RebuildCategoryItems();
    }

    private void RebuildCategoryItems()
    {
        var previous = Navigation.SelectedItem;
        _suppressNavigationSelection = true;
        try
        {
            RebuildCategoryItemsCore();
            // 新增分类项会让 NavigationView 自行选中新项；显式恢复原选择，界面不会跳到目录页。
            if (previous is not null && Navigation.MenuItems.Contains(previous)) Navigation.SelectedItem = previous;
        }
        finally { _suppressNavigationSelection = false; }
    }

    private void RebuildCategoryItemsCore()
    {
        for (var index = Navigation.MenuItems.Count - 1; index >= 0; index--)
        {
            if (Navigation.MenuItems[index] is NavigationViewItem { Tag: Category })
                Navigation.MenuItems.RemoveAt(index);
        }
        foreach (var category in _viewModel.Catalog.Categories)
        {
            var label = _viewModel.Text[$"category.{category.Id.Value}"];
            var item = new NavigationViewItem
            {
                Content = label,
                Tag = category,
                Icon = new FontIcon { Glyph = CategoryGlyph(category.Id.Value) },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "CategoryNavigation-" + category.Id.Value);
            ToolTipService.SetToolTip(item, label);
            Navigation.MenuItems.Add(item);
        }
    }

    private static string CategoryGlyph(string categoryId) => categoryId switch
    {
        "dev-tools" => "\uE943", // Code
        "system" => "\uE90F", // Repair
        "network" => "\uE968", // Network
        "media" => "\uE714", // Video
        "graphics" => "\uE790", // Color
        "office" => "\uE8A5", // Document
        "security" => "\uEA18", // Shield
        "games" => "\uE7FC", // Game controller
        "ai" => "\uEA80", // Lightbulb
        "terminal" => "\uE756", // Command prompt
        "files" => "\uE8B7", // Folder
        "science" => "\uE7BE", // Education
        _ => "\uE8A9", // All apps
    };

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!_initialized || _disposed || args.InvokedItemContainer is not NavigationViewItem item) return;
        if (item.Tag as string == "launcher") _ = _viewModel.Launcher.RefreshEntriesAsync(_lifetime.Token);
        else if (item.Tag as string == "home") _ = InitializeDiscoveryAsync();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_initialized || _disposed || _suppressNavigationSelection) return;
        try
        {
            if (args.IsSettingsSelected)
            {
                _viewModel.Section = ShellSection.Settings;
                return;
            }
            if (args.SelectedItem is not NavigationViewItem item) return;
            if (item.Tag is Category category)
            {
                _viewModel.Section = ShellSection.Catalog;
                await _viewModel.Catalog.ShowCategoryAsync(category, _lifetime.Token);
                return;
            }
            switch (item.Tag as string)
            {
                case "home":
                    _viewModel.Section = ShellSection.Home;
                    await _viewModel.Home.InitializeAsync(_lifetime.Token);
                    break;
                case "launcher":
                    _viewModel.Section = ShellSection.Launcher;
                    await _viewModel.Launcher.RefreshEntriesAsync(_lifetime.Token);
                    break;
                case "installed":
                    _viewModel.Section = ShellSection.Installed;
                    await _viewModel.Installed.RescanCommand.ExecuteAsync(null);
                    break;
                case "sources":
                    _viewModel.Section = ShellSection.Sources;
                    await _viewModel.Sources.InitializeAsync(_lifetime.Token);
                    break;
                case "settings":
                    _viewModel.Section = ShellSection.Settings;
                    await _viewModel.Settings.RefreshCacheUsageAsync(_lifetime.Token);
                    break;
                case "ranking":
                    _viewModel.Section = ShellSection.Catalog;
                    await _viewModel.Catalog.ShowRankingAsync(_lifetime.Token);
                    break;
                default:
                    _viewModel.Section = ShellSection.Catalog;
                    await _viewModel.Catalog.ShowHomeAsync(_lifetime.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _viewModel.Catalog.ErrorMessage = _viewModel.Catalog.FormatError("ActionFailed", ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 事件必须返回 void；对话框在主按钮 deferral 内完成异步写入。")]
    private async void OnAddRepositoryRequested(object? sender, EventArgs args)
    {
        if (_disposed) return;
        try
        {
            // 分类需要反映目录当前状态和所选语言。
            _addRepository.RefreshCategories();
            var dialog = new AddRepositoryDialog(_addRepository) { XamlRoot = XamlRoot };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && dialog.AddedDetail is { } detail)
            {
                await _viewModel.Catalog.ShowAddedAsync(detail, _lifetime.Token);
                _viewModel.Section = ShellSection.Catalog;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _viewModel.Catalog.ErrorMessage = _viewModel.Catalog.FormatError("AddFailed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.Text.LanguageChanged -= OnLanguageChanged;
        _viewModel.Settings.CatalogLibrarySynchronized -= OnCatalogLibrarySynchronized;
        _lifetime.Cancel();
        DiscoveryHome.Dispose();
        Launcher.Dispose();
        Catalog.Dispose();
        PreferencesView.Dispose();
        _viewModel.Dispose();
        _addRepository.Dispose();
        _lifetime.Dispose();
    }
}
