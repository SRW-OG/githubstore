using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Microsoft.UI.Xaml.Controls;

namespace GitHubStore.App.Views;

public sealed partial class AddRepositoryDialog : ContentDialog
{
    private readonly AddRepositoryViewModel _viewModel;

    /// <summary>内部可见：公开会触发 XAML 类型生成器为 AppDetail 生成无参激活代码。</summary>
    internal AppDetail? AddedDetail { get; private set; }

    public AddRepositoryDialog(AddRepositoryViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 对话框事件必须返回 void；异步 deferral 在 finally 中完成且异常全部处理。")]
    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            AddedDetail = await _viewModel.AddAsync();
            args.Cancel = AddedDetail is null;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            _viewModel.ErrorMessage = $"{_viewModel.Text["AddFailed"]}：{ex.Message}";
        }
        finally { deferral.Complete(); }
    }
}
