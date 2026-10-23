using GitHubStore.Core.Abstractions;
using GitHubStore.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubStore.App.Views;

/// <summary>Launcher tab inside the main window. Theme cascades from the shell; only the registry subscription is owned here.</summary>
public sealed partial class LauncherView : UserControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private IManagedAppLauncher? _launcher;
    private bool _loadedOnce;
    private bool _disposed;

    public LauncherView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? ExploreRequested;

    private void OnExploreRequested(object sender, RoutedEventArgs args) => ExploreRequested?.Invoke(this, EventArgs.Empty);

    private LauncherViewModel? ViewModel => DataContext as LauncherViewModel;

    /// <summary>Subscribes to install-registry changes so new registrations appear without a manual refresh.</summary>
    public void Attach(IManagedAppLauncher launcher)
    {
        if (_disposed || _launcher is not null) return;
        _launcher = launcher;
        launcher.Changed += OnRegistryChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => StartLoad();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 事件必须返回 void；失败由视图模型的状态文本呈现。")]
    private async void OnLoaded(object sender, RoutedEventArgs args) => await StartLoadAsync();

    private void OnUnloaded(object sender, RoutedEventArgs args) { /* The page keeps its state while the shell swaps views. */ }

    private void StartLoad() { if (IsLoaded) _ = StartLoadAsync(); }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 事件必须返回 void；失败由视图模型的状态文本呈现。")]
    private async Task StartLoadAsync()
    {
        if (_loadedOnce || _disposed || ViewModel is not { } viewModel) return;
        _loadedOnce = true;
        await viewModel.RefreshEntriesAsync(_lifetime.Token);
    }

    private void OnRegistryChanged(object? sender, EventArgs args)
    {
        var dispatcher = DispatcherQueue;
        if (_disposed || dispatcher is null) return;
        dispatcher.TryEnqueue(RefreshAfterRegistryChanged);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Dispatcher 边界；刷新失败由视图模型呈现。")]
    private async void RefreshAfterRegistryChanged()
    {
        if (!_disposed && ViewModel is { } viewModel) await viewModel.RefreshEntriesAsync(_lifetime.Token);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 项点击边界；启动结果由视图模型呈现。")]
    private async void OnApplicationClick(object sender, ItemClickEventArgs args)
    {
        if (!_disposed && args.ClickedItem is LauncherItemViewModel app && ViewModel is { } viewModel)
            await viewModel.LaunchEntryAsync(app, _lifetime.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_launcher is not null) _launcher.Changed -= OnRegistryChanged;
        _lifetime.Cancel();
        ApplicationScroller.Content = null;
        _lifetime.Dispose();
    }
}
