using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace GitHubStore.App.Converters;

/// <summary>
/// true 折叠、false 显示。用于“没有数据时显示提示”这类反向绑定，
/// 避免为了取反而在 ViewModel 里额外暴露一个语义重复的属性。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
