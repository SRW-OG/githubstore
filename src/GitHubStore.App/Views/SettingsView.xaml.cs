using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace GitHubStore.App.Views;

public sealed partial class SettingsView : UserControl, IDisposable
{
    // 主题枚举与下拉索引一一对应，避免 XAML 直接绑定枚举。
    private static readonly ThemeMode[] ThemeOrder = [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];
    private static readonly string[] LanguageOrder = ["system", "zh-CN", "en-US"];

    private bool _initializeRequested;
    private bool _cacheConfirmationOpen;
    private bool _suppressSelection;
    private SettingsViewModel? _subscribedViewModel;
    private bool _disposed;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        _initializeRequested = false;
        AttachViewModel();
        PopulateSelectors();
    }

    private void AttachViewModel()
    {
        if (_disposed || ReferenceEquals(_subscribedViewModel, ViewModel)) return;
        DetachViewModel();
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is { } vm) vm.Text.LanguageChanged += OnLanguageChanged;
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is { } vm) vm.Text.LanguageChanged -= OnLanguageChanged;
        _subscribedViewModel = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => DetachViewModel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachViewModel();
        Aria2Probe = null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event handler catches asynchronous errors.")]
    private async void OnRefreshCacheUsageClick(object sender, RoutedEventArgs args)
    {
        try { if (ViewModel is { } vm) await vm.RefreshCacheUsageAsync(); }
        catch (Exception) { ViewModel?.ReportFailure(); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Manual cache deletion requires confirmation; all errors are caught.")]
    private async void OnClearDownloadCacheClick(object sender, RoutedEventArgs args)
    {
        if (_disposed || _cacheConfirmationOpen || ViewModel is not { CanClearCache: true } vm || XamlRoot is null) return;
        _cacheConfirmationOpen = true;
        try
        {
            var confirmedRoot = vm.CacheRoot;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = vm.Text["SettingsCacheClearTitle"],
                Content = new TextBlock
                {
                    Text = vm.Text.Format("SettingsCacheClearConfirm", confirmedRoot),
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 520,
                },
                PrimaryButtonText = vm.Text["SettingsCacheClear"], CloseButtonText = vm.Text["Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_disposed && ReferenceEquals(ViewModel, vm))
                await vm.ClearCacheConfirmedAsync(confirmedRoot);
        }
        catch (Exception) { if (!_disposed) ViewModel?.ReportFailure(); }
        finally { _cacheConfirmationOpen = false; }
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        PopulateSelectors();
        if (ViewModel is { } viewModel) SyncSelectorsFromViewModel(viewModel);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>
    /// 下拉项由代码填充：ComboBoxItem 的 Content 绑定在加载期不可靠，
    /// 会出现已选中却显示空白。这里按当前语言设置纯文本项。
    /// </summary>
    private void PopulateSelectors()
    {
        var text = ViewModel?.Text;
        _suppressSelection = true;
        try
        {
            ThemeSelector.Items.Clear();
            ThemeSelector.Items.Add(text?["ThemeSystem"] ?? "System");
            ThemeSelector.Items.Add(text?["ThemeLight"] ?? "Light");
            ThemeSelector.Items.Add(text?["ThemeDark"] ?? "Dark");
            LanguageSelector.Items.Clear();
            LanguageSelector.Items.Add(text?["LanguageSystem"] ?? "System");
            LanguageSelector.Items.Add(text?["LanguageChinese"] ?? "简体中文");
            LanguageSelector.Items.Add(text?["LanguageEnglish"] ?? "English");
            EngineSelector.Items.Clear();
            EngineSelector.Items.Add(text?["EngineHttp"] ?? "Built-in HTTP");
            EngineSelector.Items.Add(text?["EngineAria2"] ?? "aria2");
            ProxyKindSelector.Items.Clear();
            ProxyKindSelector.Items.Add(text?["ProxyNone"] ?? "No proxy");
            ProxyKindSelector.Items.Add(text?["ProxyHttp"] ?? "HTTP proxy");
            ProxyKindSelector.Items.Add(text?["ProxySocks5"] ?? "SOCKS5 proxy");
        }
        finally
        {
            _suppressSelection = false;
        }
        if (ViewModel is { } viewModel) SyncSelectorsFromViewModel(viewModel);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        AttachViewModel();
        PopulateSelectors();
        if (_initializeRequested) return;
        _initializeRequested = true;
        try
        {
            if (ViewModel is not { } viewModel) return;
            await viewModel.InitializeAsync();
            SyncSelectorsFromViewModel(viewModel);
            await viewModel.RefreshCacheUsageAsync();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_suppressSelection) return;
        try
        {
            if (ViewModel is not { } viewModel || !viewModel.CanApply) return;
            // 与当前设置相同则无需保存，避免初始化同步引起多余写入。
            if (ThemeSelector.SelectedIndex < 0 || ThemeOrder[ThemeSelector.SelectedIndex] == viewModel.SelectedTheme) return;
            viewModel.SelectedTheme = ThemeOrder[ThemeSelector.SelectedIndex];
            await viewModel.ApplyAppearanceAsync(viewModel.SelectedTheme, viewModel.SelectedLanguage);
            SyncSelectorsFromViewModel(viewModel);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_suppressSelection) return;
        try
        {
            if (ViewModel is not { } viewModel || !viewModel.CanApply) return;
            if (LanguageSelector.SelectedIndex < 0) return;
            if (string.Equals(LanguageOrder[LanguageSelector.SelectedIndex], viewModel.SelectedLanguage, StringComparison.Ordinal)) return;
            viewModel.SelectedLanguage = LanguageOrder[LanguageSelector.SelectedIndex];
            await viewModel.ApplyAppearanceAsync(viewModel.SelectedTheme, viewModel.SelectedLanguage);
            SyncSelectorsFromViewModel(viewModel);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnApplyAppearanceClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel || !viewModel.CanApply) return;
            await ApplyAppearanceSelectionAsync(viewModel);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnApplyPathsClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel) return;
            await viewModel.ApplyPathsAsync();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnApplyNetworkClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel) return;
            await viewModel.ApplyNetworkAsync();
            SyncSelectorsFromViewModel(viewModel);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnUseDefaultPathsClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel) return;
            // 清空文本框即“使用默认目录”，随后按当前选择保存。
            viewModel.UseDefaultPaths();
            await viewModel.ApplyPathsAsync();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    /// <summary>仅保存外观下拉选择；目录和网络由各自按钮提交。</summary>
    private async Task ApplyAppearanceSelectionAsync(SettingsViewModel viewModel)
    {
        var themeIndex = ThemeSelector.SelectedIndex;
        var languageIndex = LanguageSelector.SelectedIndex;
        var theme = themeIndex >= 0 && themeIndex < ThemeOrder.Length ? ThemeOrder[themeIndex] : viewModel.SelectedTheme;
        var language = languageIndex >= 0 && languageIndex < LanguageOrder.Length
            ? LanguageOrder[languageIndex] : viewModel.SelectedLanguage;
        viewModel.SelectedTheme = theme;
        viewModel.SelectedLanguage = language;
        try { await viewModel.ApplyAppearanceAsync(theme, language); }
        finally { SyncSelectorsFromViewModel(viewModel); }
    }

    private void OnEngineSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_suppressSelection || ViewModel is not { } viewModel || !viewModel.CanApply
            || EngineSelector.SelectedIndex < 0) return;
        // 引擎是网络草稿的一部分，点击“保存网络设置”时统一提交。
        viewModel.DownloadEngine = EngineSelector.SelectedIndex == 1 ? DownloadEngine.Aria2 : DownloadEngine.Http;
    }

    private void OnProxyKindSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_suppressSelection) return;
        if (ViewModel is not { } viewModel) return;
        viewModel.ProxyKind = ProxyKindSelector.SelectedIndex switch
        {
            1 => ProxyKind.Http,
            2 => ProxyKind.Socks5,
            _ => ProxyKind.None,
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnProbeEngineClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel || !viewModel.CanApply) return;
            await viewModel.ProbeDownloadEngineAsync(Aria2Probe);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ReportFailure();
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    /// <summary>由组合根注入的内置 aria2 检查委托；未注入表示组件初始化中或不可用。</summary>
    public Func<CancellationToken, Task<bool>>? Aria2Probe { get; set; }

    /// <summary>
    /// PasswordBox.Password 不是依赖属性，不能双向绑定；
    /// 这里把用户输入同步进 ViewModel，由「应用并保存」统一提交。
    /// </summary>
    private void OnAiApiKeyChanged(object sender, RoutedEventArgs args)
    {
        if (_suppressSelection || ViewModel is not { } viewModel) return;
        viewModel.AiApiKey = AiApiKeyBox.Password;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML event handler catches failures.")]
    private async void OnApplyAiClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel) return;
            await viewModel.ApplyAiAsync();
            SyncSelectorsFromViewModel(viewModel);
        }
        catch
        {
            // Do not log an unexpected exception that might include a credential.
            ViewModel?.ReportFailure();
        }
    }

    private void OnProxyPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_suppressSelection || ViewModel is not { } viewModel) return;
        viewModel.ProxyPassword = ProxyPasswordBox.Password;
    }

    /// <summary>把 ViewModel 的当前值写回下拉框；期间抑制 SelectionChanged 回环。</summary>
    private void SyncSelectorsFromViewModel(SettingsViewModel viewModel)
    {
        _suppressSelection = true;
        try
        {
            var themeIndex = Array.IndexOf(ThemeOrder, viewModel.SelectedTheme);
            if (themeIndex >= 0 && ThemeSelector.SelectedIndex != themeIndex) ThemeSelector.SelectedIndex = themeIndex;
            var languageIndex = Array.IndexOf(LanguageOrder, viewModel.SelectedLanguage);
            if (languageIndex >= 0 && LanguageSelector.SelectedIndex != languageIndex) LanguageSelector.SelectedIndex = languageIndex;
            // 引擎与代理类型同样需要回写，否则重建下拉项后会显示为空白。
            var engineIndex = viewModel.DownloadEngine == DownloadEngine.Aria2 ? 1 : 0;
            if (EngineSelector.SelectedIndex != engineIndex) EngineSelector.SelectedIndex = engineIndex;
            var proxyIndex = viewModel.ProxyKind switch
            {
                ProxyKind.Http => 1,
                ProxyKind.Socks5 => 2,
                _ => 0,
            };
            if (ProxyKindSelector.SelectedIndex != proxyIndex) ProxyKindSelector.SelectedIndex = proxyIndex;
            if (AiApiKeyBox.Password != viewModel.AiApiKey) AiApiKeyBox.Password = viewModel.AiApiKey;
            if (ProxyPasswordBox.Password != viewModel.ProxyPassword) ProxyPasswordBox.Password = viewModel.ProxyPassword;
        }
        finally
        {
            _suppressSelection = false;
        }
    }
}

/// <summary>非空字符串显示，空字符串折叠；用于错误文案。</summary>
public sealed class SettingsMessageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>非空字符串打开 InfoBar；空字符串关闭。</summary>
public sealed class SettingsMessageOpenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
