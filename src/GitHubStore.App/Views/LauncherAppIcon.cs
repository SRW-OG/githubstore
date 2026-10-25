using GitHubStore.Platform.Win32.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace GitHubStore.App.Views;

/// <summary>Reads embedded executable icons first; shell thumbnails and a local glyph provide fallbacks.</summary>
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

    private static async Task<BitmapImage?> ReadEmbeddedImageAsync(string path)
    {
        try
        {
            var bytes = await Task.Run(() => ExecutableIconReader.ReadIcon(path));
            if (bytes is null) return null;
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 128 };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception) { return null; }
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
            var embedded = await ReadEmbeddedImageAsync(path);
            if (!IsLoaded || version != _version) return;
            if (embedded is not null)
            {
                _image.Source = embedded;
                _fallback.Visibility = Visibility.Collapsed;
                return;
            }
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
