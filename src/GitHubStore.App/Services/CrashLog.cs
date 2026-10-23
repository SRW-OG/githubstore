namespace GitHubStore.App.Services;

internal static class CrashLog
{
    // 仅用于无法恢复的异常兜底，不在正常 UI 加载路径执行 IO。
    public static void Write(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitHubStore", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception logError)
        {
            System.Diagnostics.Trace.WriteLine(logError);
            System.Diagnostics.Trace.WriteLine(exception);
        }
    }
}
