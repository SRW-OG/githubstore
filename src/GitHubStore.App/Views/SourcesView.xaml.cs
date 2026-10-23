using GitHubStore.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubStore.App.Views;

public sealed partial class SourcesView : UserControl
{
    public SourcesView() => InitializeComponent();

    private SourcesViewModel? ViewModel => DataContext as SourcesViewModel;

    /// <summary>手动添加对话框由页面承载，避免视图各自持有服务。</summary>
    public event EventHandler? AddRepositoryRequested;

    private void OnAddRepositoryClick(object sender, RoutedEventArgs args) =>
        AddRepositoryRequested?.Invoke(this, EventArgs.Empty);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnWingetSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        try
        {
            if (ViewModel is { } viewModel) await viewModel.SearchAsync();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ErrorMessage = viewModel.Text.Format("ActionFailed", ex.Message);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnWingetSearchClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is { } viewModel) await viewModel.SearchAsync();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel) viewModel.ErrorMessage = viewModel.Text.Format("ActionFailed", ex.Message);
        }
    }
}
