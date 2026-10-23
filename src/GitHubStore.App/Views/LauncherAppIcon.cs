using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace GitHubStore.App.Views;

/// <summary>Loads an executable's shell thumbnail; missing icons retain a generic local fallback.</summary>
public sealed class LauncherAppIcon : Grid
{
    private readonly FontIcon _fallback = new() { Glyph = "\uE8A5", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private int _version;

    public LauncherAppIcon()
    {
        Children.Add(_fallback);
        Children.Add(_image);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string? ExecutablePath
    {
        get => (string?)GetValue(ExecutablePathProperty);
        set => SetValue(ExecutablePathProperty, value);
    }
    public static readonly DependencyProperty ExecutablePathProperty = DependencyProperty.Register(
        nameof(ExecutablePath), typeof(string), typeof(LauncherAppIcon), new PropertyMetadata(null, OnPathChanged));

    private static void OnPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (LauncherAppIcon)sender;
        if (control.IsLoaded) control.LoadIcon();
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => LoadIcon();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ++_version;
        _image.Source = null;
        _fallback.Visibility = Visibility.Visible;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI control lifecycle entry; all asynchronous failures are contained and leave a fallback icon.")]
    private async void LoadIcon()
    {
        var version = ++_version;
        var path = ExecutablePath;
        _image.Source = null;
        _fallback.Visibility = Visibility.Visible;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96);
            if (thumbnail is null || thumbnail.Size == 0 || !IsLoaded || version != _version) return;
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            if (!IsLoaded || version != _version) return;
            _image.Source = image;
            _fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { /* Missing, locked or unsupported files use the generic icon. Never execute to extract an icon. */ }
    }
}
