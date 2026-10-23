using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubStore.App.Views;

public sealed partial class InstalledView : UserControl
{
    private bool _initialized;

    public InstalledView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private InstalledViewModel? ViewModel => DataContext as InstalledViewModel;

    private void OnUnloaded(object sender, RoutedEventArgs args) => ViewModel?.CancelAiAnalysis(clearResult: true);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel?.RefreshAiAvailability();
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (ViewModel is { } viewModel) await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel)
                viewModel.ErrorMessage = viewModel.Text.Format("InstalledScanFailed", ex.Message);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI XAML 事件必须返回 void；处理器内捕获所有异常。")]
    private async void OnUninstallClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (ViewModel is not { } viewModel || viewModel.SelectedProgram is not { } program) return;
            // 先确认再启动：卸载是不可撤销的系统写操作。
            if (!await ConfirmUninstallAsync(program)) return;
            await viewModel.UninstallConfirmedAsync(program);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } viewModel)
                viewModel.ErrorMessage = viewModel.Text.Format("InstalledUninstallFailed", ex.Message);
        }
    }

    /// <summary>
    /// 卸载前二次确认：卸载是不可撤销的系统写操作，必须先让用户看到会发生什么。
    /// 对话框文案来自本地化服务，卸载命令由 ViewModel 调用服务层执行。
    /// </summary>
    internal async Task<bool> ConfirmUninstallAsync(InstalledProgram program)
    {
        if (ViewModel is not { } viewModel) return false;
        // 本商店执行的 MSI 由 Windows Installer 静默卸载并核验结果，不是“启动第三方交互式卸载器”；
        // 对话框必须按这一条记录的实际移除方式选文案，不能对所有情况都承诺同一种机制。
        var msi = program.StorePerformedMsiRemoval;
        var confirmKey = msi ? "InstalledUninstallConfirmMsi" : "InstalledUninstallConfirm";
        var hintKey = msi ? "InstalledUninstallHintMsi" : "InstalledUninstallHint";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = viewModel.Text["InstalledUninstallTitle"],
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = viewModel.Text.Format(confirmKey, program.DisplayName),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = viewModel.Text[hintKey],
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                },
            },
            PrimaryButtonText = viewModel.Text["InstalledUninstall"],
            CloseButtonText = viewModel.Text["Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
