using System.Runtime.InteropServices;

namespace GitHubStore.Platform.Win32.Runtime;

/// <summary>平台信息不依赖业务模型；由 App 的适配器提供给设置界面。</summary>
public sealed class WindowsRuntimeInfo
{
    public string OperatingSystemDescription => RuntimeInformation.OSDescription;
    public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();
    public bool IsWindows => OperatingSystem.IsWindows();
    public string FrameworkDescription => RuntimeInformation.FrameworkDescription;
}
