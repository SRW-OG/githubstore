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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Log("OnLaunched start.");
        try
        {
            _services = ServiceRegistration.CreateProvider();
            StartupDiagnostics.Log("DI provider created.");
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
