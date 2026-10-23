using System.Runtime.InteropServices;

namespace GitHubStore.App.Services.Tools;

/// <summary>Resolve the current user's registered Windows App Installer; never scan arbitrary PATH entries.</summary>
public static class WindowsSystemToolLocator
{
    public static Task<string?> ResolveWingetAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            var candidates = new List<(Version Version, string Path)>();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(package.Id.FamilyName, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", StringComparison.Ordinal))
                    continue;
                var directory = package.InstalledLocation?.Path;
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
                var executable = Path.Combine(directory, "winget.exe");
                if (!File.Exists(executable)) continue;
                var version = package.Id.Version;
                candidates.Add((new Version(version.Major, version.Minor, version.Build, version.Revision), executable));
            }
            return candidates.OrderByDescending(candidate => candidate.Version)
                .Select(candidate => candidate.Path).FirstOrDefault();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException
            or InvalidOperationException or NotSupportedException)
        {
            // The tool manager can still use the current user's WindowsApps execution alias.
            System.Diagnostics.Trace.WriteLine($"System winget lookup: {ex.GetType().Name} (0x{ex.HResult:X8})");
            return null;
        }
    }, ct);
}
