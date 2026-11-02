using GitHubStore.Platform.Win32.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace GitHubStore.App.Views;

/// <summary>Reads embedded executable icons first; native shell icons, thumbnails and a local glyph provide fallbacks.</summary>
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

    /// <summary>Optional user-selected icon file; falls back to the executable's own icon.</summary>
    public string? IconPath
    {
        get => (string?)GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }
    public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register(
        nameof(IconPath), typeof(string), typeof(LauncherAppIcon), new PropertyMetadata(null, OnPathChanged));

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
        try { return await DecodeIconAsync(await Task.Run(() => ExecutableIconReader.ReadIcon(path))); }
        catch (Exception) { return null; }
    }

    private static async Task<BitmapImage?> ReadShellImageAsync(string path)
    {
        try { return await DecodeIconAsync(await ShellIconReader.ReadIconAsync(path)); }
        catch (Exception) { return null; }
    }

    private static async Task<BitmapImage?> DecodeFileAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var image = new BitmapImage { DecodePixelWidth = 128 };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception) { return null; }
    }

    private static async Task<BitmapImage?> DecodeIconAsync(byte[]? bytes)
    {
        try
        {
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
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
            var custom = IconPath;
            if (!string.IsNullOrWhiteSpace(custom) && Path.IsPathFullyQualified(custom) && File.Exists(custom))
            {
                var extension = Path.GetExtension(custom).ToLowerInvariant();
                var customImage = extension is ".ico" or ".png" or ".jpg" or ".jpeg" or ".bmp"
                    ? await DecodeFileAsync(custom)
                    : await ReadShellImageAsync(custom);
                if (!IsLoaded || version != _version) return;
                if (customImage is not null)
                {
                    _image.Source = customImage;
                    _fallback.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            var isExecutable = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var isShortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            if (!isExecutable && !isShortcut) return;
            // Shortcuts use the shell's resolved icon; only PE files have embedded executable icons.
            var embedded = isExecutable ? await ReadEmbeddedImageAsync(path) : null;
            if (!IsLoaded || version != _version) return;
            if (embedded is not null)
            {
                _image.Source = embedded;
                _fallback.Visibility = Visibility.Collapsed;
                return;
            }
            // The WinRT thumbnail provider can return nothing for .lnk files. Ask the shell
            // directly on its own STA worker before trying that final image fallback.
            var shellImage = await ReadShellImageAsync(path);
            if (!IsLoaded || version != _version) return;
            if (shellImage is not null)
            {
                _image.Source = shellImage;
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
