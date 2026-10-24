using GitHubStore.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace GitHubStore.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private MainWindow? _window;

    /// <summary>
    /// 当前主窗口，供需要窗口句柄的界面能力使用（例如未打包应用的文件选择器）。
    /// 尚未创建或已关闭时为 null，调用方必须自行处理这种情况。
    /// </summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        StartupDiagnostics.Log("App ctor: InitializeComponent start.");
        InitializeComponent();
        StartupDiagnostics.Log("App ctor: InitializeComponent done.");
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // 恢复必须 await：它是文件写入操作，同步等待会在 UI 线程上死锁（VSTHRD002）。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100",
        Justification = "WinUI 的 OnLaunched 只能是 void；内部捕获全部异常，不会抛出未处理异常。")]
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Log("OnLaunched start.");
        try
        {
            _services = ServiceRegistration.CreateProvider();
            StartupDiagnostics.Log("DI provider created.");
            // 上次升级若在切换途中中断，必须在读取任何安装记录之前先恢复旧版本，
            // 否则界面会先展示一个可能已被移动过的安装。
            await RecoverInterruptedUpgradeAsync(_services);
            _window = _services.GetRequiredService<MainWindow>();
            MainWindow = _window;
            StartupDiagnostics.Log("MainWindow constructed.");
            _window.Closed += OnWindowClosed;
            _window.Activate();
            StartupDiagnostics.Log("MainWindow activated.");
            // 启动时若有应用未能恢复完整，必须让用户看见，而不是只在诊断日志里留一行。
            await ShowStartupRecoveryWarningAsync(_services);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ShowFatal("创建主窗口失败", ex);
            CrashLog.Write(ex);
            Exit();
        }
    }

    /// <summary>
    /// 尽力完成上次中断的升级恢复。恢复失败不能阻止程序启动，但也绝不谎称已恢复：
    /// 调用方（此处为启动流程）只记录结果，界面的安装状态会如实显示为“无法核对”。
    /// </summary>
    private static async Task RecoverInterruptedUpgradeAsync(IServiceProvider services)
    {
        try
        {
            var upgrades = services.GetRequiredService<GitHubStore.App.Services.Installation.PortableUpgradeService>();
            var result = await upgrades.RecoverAsync();
            StartupRecoveryFailures = result.Failed;
            StartupDiagnostics.Log($"Upgrade recovery: examined={result.Examined} restored={result.Restored} cleaned={result.CleanedUp} failed={result.Failed}.");
        }
        catch (Exception ex)
        {
            // 恢复整体失败同样意味着磁盘上可能留着不一致的安装，必须让界面能如实告知用户，
            // 而不是只在诊断日志里留一行。
            StartupRecoveryFailures = 1;
            StartupDiagnostics.Log("Upgrade recovery failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 启动恢复未能收尾的条目数。大于 0 表示磁盘上可能仍有处于中间状态的安装：
    /// 界面必须如实告知用户，并说明重启会再次尝试，绝不能当成“一切正常”。
    /// </summary>
    public static int StartupRecoveryFailures { get; private set; }

    /// <summary>
    /// 启动恢复未能收尾时，用一次明确的对话框告知用户。仅写诊断日志不够：
    /// 用户需要知道“有应用的升级没有完成、相关应用已被阻止启动和改登记、备份与日志要保留”，
    /// 而且对话框是模态的，不会被别的界面盖过去。
    /// <para>
    /// 失败不阻止启动，也绝不改变恢复结果；对话框本身出错只记 Trace。
    /// </para>
    /// </summary>
    private static async Task ShowStartupRecoveryWarningAsync(IServiceProvider services)
    {
        if (StartupRecoveryFailures <= 0) return;
        var root = MainWindow?.Content as FrameworkElement;
        try
        {
            // Activate 刚返回时 XamlRoot 可能还没建立，此时 ShowAsync 会抛异常。
            // 等一次 Loaded 再显示，而不是静默失败——这条提示必须在用户面前出现过。
            if (root is not null && root.XamlRoot is null)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
                root.Loaded += OnLoaded;
                try { await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(5))); }
                finally { root.Loaded -= OnLoaded; }
            }
            // 窗口在等待期间被关掉：此时不再显示，也不留悬挂的订阅。
            if (MainWindow?.Content is not FrameworkElement current || current.XamlRoot is null) return;
            var text = services.GetRequiredService<Services.Localization.LocalizationService>();
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = current.XamlRoot,
                Title = text["StartupRecoveryTitle"],
                Content = text["StartupRecoveryMessage"],
                CloseButtonText = text["Close"],
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            };
            // 稳定的自动化标识：隔离数据目录下用无效 journal 做 UI 验收时据此定位这个对话框。
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "StartupRecoveryWarning");
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 提示失败不能影响启动；恢复状态本身已如实记录在诊断日志与安装列表里。
            StartupDiagnostics.Log("Startup recovery warning could not be shown: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 关闭窗口时释放容器。必须是 async void：容器里有只实现 <see cref="IAsyncDisposable"/> 的单例
    /// （下载队列服务），对它调用同步 <c>Dispose()</c> 会抛 InvalidOperationException，
    /// 而同步等待又会在 UI 线程上死锁。因此这里异步等待，并先清空字段避免重入。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100",
        Justification = "WinUI 的 Closed 事件只能是 void；方法内捕获全部异常。")]
    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        // 先取走引用再置空：Closed 期间的重入不会拿到同一个容器去释放两次。
        var services = _services;
        _services = null;
        _window = null;
        MainWindow = null;
        if (services is null) return;
        try { await services.DisposeAsync(); }
        catch (Exception ex)
        {
            // 释放失败绝不能把关闭流程变成崩溃；队列的中断恢复由下次启动负责。
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        StartupDiagnostics.ShowFatal("UI 线程未处理异常", args.Exception);
        CrashLog.Write(args.Exception);
        args.Handled = true;
        Exit();
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args) =>
        CrashLog.Write(args.ExceptionObject as Exception ?? new Exception("未知进程异常"));

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        CrashLog.Write(args.Exception);
        args.SetObserved();
    }
}
