using GitHubStore.App.Services.Preferences;
using GitHubStore.Platform.Win32.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace GitHubStore.App;

public sealed class MainWindow : Window
{
    private readonly PreferencesService _preferences;
    private readonly MainPage _shell;
    private readonly AccessibilitySettings _accessibilitySettings = new();

    public MainWindow(MainPage shell, WindowController windows, PreferencesService preferences)
    {
        _shell = shell;
        _preferences = preferences;
        Title = "GitHubStore · 开源应用目录";
        Content = shell;
        if (MicaControllerSupported()) SystemBackdrop = new MicaBackdrop();
        // 自包含发布不注册包标识，标题栏与任务栏图标需显式指定，否则回退到默认图标。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "githubstore.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = windows.GetDpi(handle) / 96d;
        AppWindow.Resize(new SizeInt32((int)(1360 * scale), (int)(860 * scale)));
        _shell.Loaded += OnShellLoaded;
        _shell.ActualThemeChanged += OnActualThemeChanged;
        ApplyTheme(preferences.Current.Theme);
        ApplyTitleBarTheme();
        preferences.Changed += OnPreferencesChanged;
        Closed += OnClosed;
    }

    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        // Changed 可能来自工作线程；RequestedTheme 只能在 UI 线程设置。
        var dispatcher = DispatcherQueue;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess) ApplyTheme(_preferences.Current.Theme);
        else dispatcher.TryEnqueue(() => ApplyTheme(_preferences.Current.Theme));
    }

    private void OnShellLoaded(object sender, RoutedEventArgs args)
    {
        _shell.Loaded -= OnShellLoaded;
        ApplyTitleBarTheme();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyTitleBarTheme();

    private void ApplyTheme(ThemeMode mode) => _shell.RequestedTheme = mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void ApplyTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var titleBar = AppWindow.TitleBar;
        if (_accessibilitySettings.HighContrast)
        {
            titleBar.ResetToDefault();
            return;
        }

        var isDark = _shell.ActualTheme == ElementTheme.Dark;
        var background = (_shell.Background as SolidColorBrush)?.Color
            ?? (isDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
        var foreground = isDark ? Colors.White : Colors.Black;
        var inactiveForeground = Blend(background, foreground, 0.58);
        var hoverBackground = Blend(background, foreground, 0.08);
        var pressedBackground = Blend(background, foreground, 0.14);

        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static Color Blend(Color background, Color foreground, double foregroundOpacity)
    {
        static byte BlendChannel(byte backgroundChannel, byte foregroundChannel, double opacity) =>
            (byte)Math.Round(backgroundChannel + ((foregroundChannel - backgroundChannel) * opacity));

        return Color.FromArgb(
            255,
            BlendChannel(background.R, foreground.R, foregroundOpacity),
            BlendChannel(background.G, foreground.G, foregroundOpacity),
            BlendChannel(background.B, foreground.B, foregroundOpacity));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _preferences.Changed -= OnPreferencesChanged;
        _shell.Loaded -= OnShellLoaded;
        _shell.ActualThemeChanged -= OnActualThemeChanged;
        _shell.Dispose();
    }

    private static bool MicaControllerSupported() =>
        Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
}
