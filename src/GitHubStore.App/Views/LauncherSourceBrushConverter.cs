using GitHubStore.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GitHubStore.App.Views;

/// <summary>Keeps managed cards on the theme default, highlights manual entries in cyan, and honours a custom colour.</summary>
public sealed class LauncherSourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isFill = parameter as string == "Fill";
        var isManual = value is LauncherItemViewModel { IsManual: true };
        Color? color = value is LauncherItemViewModel { Color: { Length: > 0 } custom } ? Parse(custom) : null;
        if (color is { } chosen)
            return new SolidColorBrush(isFill ? WithAlpha(chosen, 80) : chosen);
        if (!isManual)
            return Application.Current.Resources[isFill
                ? "CardBackgroundFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
        return new SolidColorBrush(ColorHelper.FromArgb(isFill ? (byte)80 : (byte)255, 0, 151, 167));
    }

    private static Color Parse(string text)
    {
        var digits = text.TrimStart('#');
        if (digits.Length == 6 && uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (digits.Length == 8 && uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            return ColorHelper.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return Colors.Transparent;
    }

    private static Color WithAlpha(Color color, byte alpha) => ColorHelper.FromArgb(alpha, color.R, color.G, color.B);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
