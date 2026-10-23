using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitHubStore.Platform.Win32.Shell;

/// <summary>只负责窗口操作，不持有业务状态，不启动第三方进程。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowController
{
    public uint GetDpi(nint windowHandle)
    {
        if (windowHandle == 0)
            throw new ArgumentException("窗口句柄不可为空。", nameof(windowHandle));
        var dpi = GetDpiForWindow(windowHandle);
        return dpi == 0 ? 96u : dpi;
    }

    public void Restore(nint windowHandle)
    {
        if (windowHandle == 0)
            throw new ArgumentException("窗口句柄不可为空。", nameof(windowHandle));
        _ = ShowWindow(windowHandle, 9); // SW_RESTORE
        if (!SetForegroundWindow(windowHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法激活窗口。");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
