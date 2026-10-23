using System.Runtime.InteropServices;

namespace GitHubStore.App.Services;

/// <summary>
/// 启动期诊断：写应用目录旁的启动日志，并弹出系统错误提示框，
/// 用于定位 WinUI 初始化这类"进程静默退出"的问题。
/// 所有写入都吞掉自身异常，绝不能反过来阻止启动。
/// </summary>
internal static class StartupDiagnostics
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static readonly object Sync = new();
    private static readonly string CandidatePath = Path.Combine(AppContext.BaseDirectory, "startup.log");

    public static void Log(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(CandidatePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 诊断写入失败不能影响启动流程。
        }
    }

    public static void ShowFatal(string title, Exception? exception)
    {
        Log($"FATAL {title}: {exception}");
        try
        {
            var text = $"{title}{Environment.NewLine}{Environment.NewLine}{exception?.Message}"
                + $"{Environment.NewLine}{Environment.NewLine}程序目录：{AppContext.BaseDirectory}";
            _ = MessageBoxW(0, text, "GitHubStore 启动失败", 0x00000010 | 0x00040000);
        }
        catch (Exception)
        {
            // 没有交互桌面时无法弹窗。
        }
    }
}
