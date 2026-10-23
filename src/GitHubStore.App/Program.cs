using GitHubStore.App.Services;
using Microsoft.UI.Xaml;

namespace GitHubStore.App;

/// <summary>
/// 自定义入口点：逐步记录启动阶段，避免 WinUI 在窗口创建前静默退出时无从定位。
/// 非打包 WinUI 3 必须在 Application.Start 前完成 Windows App SDK 引导。
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        StartupDiagnostics.Log(
            $"Process start. BaseDirectory={AppContext.BaseDirectory}; OS={Environment.OSVersion.Version}; ProcessId={Environment.ProcessId}");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartupDiagnostics.Log("ComWrappers initialized.");
            Application.Start(_ =>
            {
                StartupDiagnostics.Log("Application.Start callback entered.");
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue);
                SynchronizationContext.SetSynchronizationContext(context);
                StartupDiagnostics.Log("DispatcherQueue and SynchronizationContext ready.");
                new App();
                StartupDiagnostics.Log("App object constructed.");
            });
            StartupDiagnostics.Log("Application.Start returned normally.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ShowFatal("WinUI 初始化失败", ex);
            Environment.Exit(1);
        }
    }
}
