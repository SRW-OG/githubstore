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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event handler catches failures.")]
    private async void OnRemoveRegistrationClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not { } viewModel || viewModel.SelectedProgram is not { CanRemoveRegistration: true } program) return;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = viewModel.Text["InstalledRemoveRegistration"],
                Content = viewModel.Text.Format("InstalledRemoveRegistrationConfirm", program.DisplayName)
                    + "\n\n" + viewModel.Text["InstalledRemoveRegistrationHint"],
                PrimaryButtonText = viewModel.Text["InstalledRemoveRegistration"],
                CloseButtonText = viewModel.Text["Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await viewModel.RemoveRegistrationConfirmedAsync(program);
        }
        catch (Exception) { viewModel.ErrorMessage = viewModel.Text["InstalledRegistrationRemovalFailed"]; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event handler catches failures.")]
    private async void OnRollbackClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not { } viewModel || viewModel.SelectedProgram is not { } program) return;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = viewModel.Text["InstalledRollbackTitle"],
                Content = viewModel.Text.Format("InstalledRollbackConfirm", program.DisplayName)
                    + "\n\n" + viewModel.Text["InstalledRollbackHint"],
                PrimaryButtonText = viewModel.Text["InstalledRollback"],
                CloseButtonText = viewModel.Text["Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await viewModel.RollbackConfirmedAsync(program);
        }
        catch (Exception) { viewModel.ErrorMessage = viewModel.Text["InstalledRollbackFailed"]; }
    }

    /// <summary>
    /// 定位主程序并修复入口路径。文件由系统选择器指定，且只改记录中的入口路径，
    /// 不移动、不删除任何文件；选择器不可用或用户取消时不做任何改动。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event handler catches failures.")]
    private async void OnRepairClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not { } viewModel || viewModel.SelectedProgram is not { CanRepairExecutable: true } program) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            // 未打包应用必须显式绑定窗口句柄，否则选择器不会显示。
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await viewModel.RepairConfirmedAsync(program, file.Path);
        }
        catch (Exception) { viewModel.ErrorMessage = viewModel.Text["InstalledRepairFailed"]; }
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
