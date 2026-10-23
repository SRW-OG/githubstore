using GitHubStore.App.Services.Preferences;
using GitHubStore.Platform.Win32.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace GitHubStore.App;

public sealed class MainWindow : Window
{
    private readonly PreferencesService _preferences;
    private readonly MainPage _shell;

    public MainWindow(MainPage shell, WindowController windows, PreferencesService preferences)
    {
        _shell = shell;
        _preferences = preferences;
        Title = "GitHubStore · 开源应用目录";
        Content = shell;
        if (MicaControllerSupported()) SystemBackdrop = new MicaBackdrop();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = windows.GetDpi(handle) / 96d;
        AppWindow.Resize(new SizeInt32((int)(1360 * scale), (int)(860 * scale)));
        ApplyTheme(preferences.Current.Theme);
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

    private void ApplyTheme(ThemeMode mode) => _shell.RequestedTheme = mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _preferences.Changed -= OnPreferencesChanged;
        _shell.Dispose();
    }

    private static bool MicaControllerSupported() =>
        Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
}
