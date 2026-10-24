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
            StartupDiagnostics.Log($"Upgrade recovery: examined={result.Examined} restored={result.Restored} cleaned={result.CleanedUp} failed={result.Failed}.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("Upgrade recovery failed: " + ex.GetType().Name);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _services?.Dispose();
        _services = null;
        _window = null;
        UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
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
