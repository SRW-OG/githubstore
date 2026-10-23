using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using GitHubStore.Core.Abstractions;
using Microsoft.Win32;

namespace GitHubStore.App.Services.Inventory;

/// <summary>只读库存；卸载只启动经校验的普通命令，不删除目录或注册表。</summary>
public sealed class InstalledProgramService : IInstalledProgramService
{
    private readonly Func<string?> _installRootProvider;
    private readonly Func<CancellationToken, IReadOnlyList<InstalledProgram>> _registryReader;
    private readonly IUninstallLauncher _uninstallLauncher;
    private readonly UninstallCommandValidator _validator = new();
    private readonly OperationLog? _log;

    /// <summary>兼容固定安装根调用方；新组合根应使用 provider 构造函数。</summary>
    public InstalledProgramService(string? greenInstallRoot = null, OperationLog? log = null)
        : this(() => greenInstallRoot, log) { }

    public InstalledProgramService(Func<string?> installRootProvider, OperationLog? log = null,
        Func<CancellationToken, IReadOnlyList<InstalledProgram>>? registryReader = null,
        IUninstallLauncher? uninstallLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(installRootProvider);
        _installRootProvider = installRootProvider;
        _registryReader = registryReader ?? ReadRegistry;
        _uninstallLauncher = uninstallLauncher ?? new ProcessUninstallLauncher();
        _log = log;
    }

    public Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<InstalledProgram>>(() => Scan(ct), ct);

    private IReadOnlyList<InstalledProgram> Scan(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 每次操作读取一次配置，不在构造时缓存安装根。
        var root = NormalizeLocation(_installRootProvider());
        var byIdentity = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        var locationOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in _registryReader(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (byIdentity.TryGetValue(program.Id, out var existing))
                byIdentity[program.Id] = existing with { DuplicateCount = existing.DuplicateCount + 1 };
            else
                byIdentity[program.Id] = program;
            // 不同注册表视图/键即不同安装，不凭显示名合并。
            // 路径证据只用来将目录发现映射到已有注册表安装。
            if (NormalizeLocation(program.InstallLocation) is { } location)
                locationOwners.TryAdd(location, program.Id);
        }

        if (root is not null && Directory.Exists(root))
        {
            foreach (var directory in EnumerateSafely(root))
            {
                ct.ThrowIfCancellationRequested();
                var location = NormalizeLocation(directory);
                if (location is null) continue;
                if (locationOwners.TryGetValue(location, out var ownerId))
                {
                    var existing = byIdentity[ownerId];
                    byIdentity[ownerId] = existing with { DuplicateCount = existing.DuplicateCount + 1 };
                    continue;
                }
                var name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var program = new InstalledProgram
                {
                    DisplayName = name,
                    DisplayVersion = ReadLooseVersion(directory),
                    InstallLocation = location,
                    Origin = RegistryOrigin.GreenInstall,
                    SourceKeyPath = location,
                };
                byIdentity[program.Id] = program;
            }
        }
        return byIdentity.Values.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> EnumerateSafely(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            Trace.WriteLine(ex);
            return [];
        }
    }

    private static string? ReadLooseVersion(string directory)
    {
        const int maxBytes = 64 * 1024;
        try
        {
            var candidate = Path.Combine(directory, "version.json");
            if (!File.Exists(candidate)) return null;
            using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maxBytes) return null;
            // 限制实际读取量，也防止文件在 Length 检查后增长。
            var buffer = new byte[maxBytes + 1];
            var count = 0;
            int read;
            while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
                count += read;
            if (count > maxBytes) return null;
            var offset = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(buffer.AsMemory(offset, count - offset));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    private static string? NormalizeLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var trimmed = path.Trim();
            return Path.IsPathFullyQualified(trimmed)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    public async Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ct.ThrowIfCancellationRequested();
        UninstallLaunchResult result;
        if (string.IsNullOrWhiteSpace(program.EffectiveUninstallString))
            result = UninstallLaunchResult.Failure(UninstallErrorCodes.NoUninstallString);
        else if (!_validator.TryValidate(program.EffectiveUninstallString, out var command, out var errorCode))
            result = UninstallLaunchResult.Failure(errorCode!);
        else
        {
            ct.ThrowIfCancellationRequested();
            result = await _uninstallLauncher.LaunchAsync(command!, ct).ConfigureAwait(false);
        }
        // 启动后取消不能把已启动结果变为失败，日志也只记录 started。
        await WriteLogAsync(program, result).ConfigureAwait(false);
        return result;
    }

    private async Task WriteLogAsync(InstalledProgram program, UninstallLaunchResult result)
    {
        if (_log is null) return;
        try
        {
            await _log.AppendAsync(new OperationLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Action = result.Started ? OperationLog.ActionUninstall : OperationLog.ActionUninstallFailed,
                DisplayName = program.DisplayName,
                Publisher = program.Publisher,
                Version = program.DisplayVersion,
                Path = program.InstallLocation,
                Outcome = result.Started ? "started" : result.ErrorCode ?? "unknown",
                Detail = result.CommandLine,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    private static IReadOnlyList<InstalledProgram> ReadRegistry(CancellationToken ct) =>
        OperatingSystem.IsWindows() ? WindowsRegistryReader.ReadAll(ct) : [];

    [SupportedOSPlatform("windows")]
    private static class WindowsRegistryReader
    {
        private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static IReadOnlyList<InstalledProgram> ReadAll(CancellationToken ct)
        {
            var result = new List<InstalledProgram>();
            var views = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryOrigin.Hklm64),
                (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryOrigin.Hklm32),
                (RegistryHive.CurrentUser, RegistryView.Default, RegistryOrigin.Hkcu),
            };
            foreach (var (hive, view, origin) in views)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
                    if (uninstallKey is null) continue;
                    foreach (var name in uninstallKey.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            using var entry = uninstallKey.OpenSubKey(name);
                            if (entry is null) continue;
                            var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                            if (Read(entry, origin, $"{hiveName}\\{UninstallKeyPath}\\{name}") is { } program)
                                result.Add(program);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                        { Trace.WriteLine(ex); }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                { Trace.WriteLine(ex); }
            }
            return result;
        }

        private static InstalledProgram? Read(RegistryKey entry, RegistryOrigin origin, string sourceKeyPath)
        {
            var displayName = ReadString(entry, "DisplayName")?.Trim();
            if (string.IsNullOrWhiteSpace(displayName) || entry.GetValue("SystemComponent") is 1 ||
                !string.IsNullOrWhiteSpace(ReadString(entry, "ParentKeyName"))) return null;
            var releaseType = ReadString(entry, "ReleaseType");
            if (releaseType is not null && (releaseType.Equals("Update", StringComparison.OrdinalIgnoreCase) ||
                releaseType.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) ||
                releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase))) return null;
            return new InstalledProgram
            {
                DisplayName = displayName,
                Publisher = ReadString(entry, "Publisher"),
                DisplayVersion = ReadString(entry, "DisplayVersion"),
                InstallLocation = ReadString(entry, "InstallLocation"),
                UninstallString = ReadString(entry, "UninstallString"),
                QuietUninstallString = ReadString(entry, "QuietUninstallString"),
                ProductCode = ReadString(entry, "ProductCode"),
                Origin = origin,
                SourceKeyPath = sourceKeyPath,
            };
        }

        private static string? ReadString(RegistryKey key, string name) =>
            key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }
}
