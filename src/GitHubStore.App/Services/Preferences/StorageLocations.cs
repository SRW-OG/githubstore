using System.IO;

namespace GitHubStore.App.Services.Preferences;

/// <summary>
/// 解析生效的下载缓存目录与默认安装目录。
/// 默认安装目录按用户要求为 C:\githubapps。
/// </summary>
public static class StorageLocations
{
    public const string DefaultInstallRoot = @"C:\githubapps";

    public static string DefaultCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubStore", "cache");

    public static string ResolveCacheDirectory(AppPreferences preferences, string? dataDirectory = null) =>
        string.IsNullOrWhiteSpace(preferences.DownloadCacheDirectory)
            ? dataDirectory is null ? DefaultCacheDirectory : Path.Combine(Path.GetFullPath(dataDirectory), "cache")
            : preferences.DownloadCacheDirectory!;

    public static string ResolveInstallRoot(AppPreferences preferences) =>
        string.IsNullOrWhiteSpace(preferences.InstallRootDirectory)
            ? DefaultInstallRoot
            : preferences.InstallRootDirectory!;

    /// <summary>
    /// 尝试创建目录以尽早暴露权限问题；只创建目录本身，不写入任何文件，
    /// 失败时返回 false 并给出原因，不抛异常。
    /// </summary>
    public static bool TryEnsureDirectory(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
