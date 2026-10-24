using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>
/// A local index of completed Store installation receipts, not a signature or proof of file trust.
/// No registry/directory discovery, shell, command-line arguments, or network access is used.
/// </summary>
public sealed class ManagedAppLauncher : IManagedAppLauncher
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumEntries = 512;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };
    private readonly string _fixedRoot;
    private readonly Func<string>? _installRootProvider;
    private string _root => _installRootProvider is null ? _fixedRoot : NormalizeLocalPath(_installRootProvider());
    private readonly string _manifestPath;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _gate;
    private readonly Func<string, string, bool> _processStarter;

    public ManagedAppLauncher(string dataDirectory, string? installRoot = null,
        Func<string, string, bool>? processStarter = null, Func<string>? installRootProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _fixedRoot = NormalizeLocalPath(installRoot ?? @"C:\githubapps");
        _installRootProvider = installRootProvider;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _manifestPath = Path.Combine(_dataDirectory, "managed-apps.json");
        _gate = Gates.GetOrAdd(_manifestPath, _ => new SemaphoreSlim(1, 1));
        _processStarter = processStarter ?? StartProcess;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// 每次访问都重新解析生效的安装根目录：用户可在设置中更改它，
    /// 缓存下来会让删除操作校验到过期的根目录。
    /// </summary>
    public string InstallRoot => _root;

    public async Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var result = new List<LauncherApp>(entries.Length);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var installation = entry.Installation;
                var pending = HasPendingRecovery(installation.InstallId);
                var available = !pending && IsAvailable(installation);
                result.Add(new LauncherApp
                {
                    InstallId = installation.InstallId,
                    AppId = installation.AppId,
                    DisplayName = entry.DisplayName,
                    InstallDirectory = installation.TargetDir!,
                    ExecutablePath = installation.ExePath!,
                    DisplayVersion = installation.DisplayVersion ?? installation.Version.Raw,
                    InstalledAt = installation.InstalledAt,
                    IsAvailable = available,
                    Status = pending ? InstalledStatus.RecoveryRequired
                        : available ? InstalledStatus.Installed : InstalledStatus.NeedsRepair,
                    // 只有本程序自己解包出来的便携/绿色安装才允许由本程序删除文件。
                    StoreManagedRemoval = StoreOwnedInstallation.OwnsExtractedFiles(installation),
                    // 本程序用合法产品码执行的 MSI：UI 据此显示“用产品码卸载”，而不是“启动第三方卸载器”。
                    StorePerformedMsiRemoval = StoreOwnedInstallation.CanRemoveMsi(installation),
                });
            }
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 按安装 ID 返回已校验并规范化路径的登记原文，供需要确认安装所有权的调用方使用。
    /// 与 LaunchAsync 一样按 ID 重新读取持久化数据：UI 快照永远不能作为写入依据。
    /// 只有“确实没有这条登记”才返回 null；索引本身不可读时抛出 <see cref="LauncherException"/>，
    /// 让调用方能区分“记录不存在”和“无法确认”，而不是把读取失败伪装成记录缺失。
    /// </summary>
    public async Task<InstallRecord?> GetAsync(string installId, CancellationToken ct = default)
    {
        if (!ValidText(installId, 256)) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var entry = entries.SingleOrDefault(e => string.Equals(e.Installation.InstallId, installId, StringComparison.Ordinal));
            if (entry is null) return null;
            ct.ThrowIfCancellationRequested();
            // 单条登记损坏时同样无法证明所有权，按“找不到”处理；索引级问题已在 ReadAsync 抛出。
            try { return ValidateInstallation(entry.Installation); }
            catch (LauncherException) { return null; }
        }
        finally { _gate.Release(); }
    }

    public async Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (installation is null || !ValidText(displayName, 200)) throw new LauncherException("launcher_invalid_installation");
        var normalized = ValidateInstallation(installation);
        EnsureExecutableAvailable(normalized);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read first: a corrupt or unreadable manifest must never be replaced with an empty index.
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var existing = Array.FindIndex(entries, e => string.Equals(e.Installation.InstallId, normalized.InstallId, StringComparison.Ordinal));
            if (existing >= 0 && entries[existing].Installation.AppId != normalized.AppId)
                throw new LauncherException("launcher_duplicate_install_id");
            for (var index = 0; index < entries.Length; index++)
            {
                if (index == existing) continue;
                if (entries[index].Installation.AppId == normalized.AppId) throw new LauncherException("launcher_duplicate_app");
                if (SamePath(entries[index].Installation.TargetDir!, normalized.TargetDir!)) throw new LauncherException("launcher_duplicate_target");
            }
            var updated = new Entry(displayName.Trim(), normalized);
            var next = existing >= 0 ? entries.ToArray() : entries.Append(updated).ToArray();
            if (existing >= 0) next[existing] = updated;
            if (next.Length > MaximumEntries) throw new LauncherException("launcher_manifest_too_large");
            // Revalidate after waiting for the gate; the filesystem may have changed in the meantime.
            EnsureExecutableAvailable(normalized);
            await WriteAsync(next, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        NotifyChanged();
    }

    public async Task UnregisterAsync(string installId, CancellationToken ct = default)
    {
        if (!ValidText(installId, 256)) throw new LauncherException("launcher_invalid_install_id");
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            if (HasPendingRecovery(installId)) throw new LauncherException("launcher_recovery_required");
            var next = entries.Where(e => !string.Equals(e.Installation.InstallId, installId, StringComparison.Ordinal)).ToArray();
            if (next.Length == entries.Length) return;
            await WriteAsync(next, ct).ConfigureAwait(false);
            changed = true;
        }
        finally { _gate.Release(); }
        if (changed) NotifyChanged();
    }

    /// <summary>
    /// 只改一条登记的入口路径。安装目录、安装身份、来源、版本与移除标记都不变，
    /// 因此安装器类应用同样适用：这里既不删除文件，也不改变任何卸载判定依据。
    /// </summary>
    public async Task<LauncherLaunchResult> RepairExecutableAsync(string installId, AppId expectedAppId,
        string executablePath, CancellationToken ct = default)
    {
        if (!ValidText(installId, 256)) return new(false, "launcher_invalid_install_id");
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            if (HasPendingRecovery(installId)) return new(false, "launcher_recovery_required");
            var index = Array.FindIndex(entries, e => string.Equals(e.Installation.InstallId, installId, StringComparison.Ordinal));
            if (index < 0) return new(false, "launcher_not_found");
            var existing = entries[index].Installation;
            // 安装身份由调用方重新确认：界面传入的 AppId 与登记不一致时不做任何改动。
            if (existing.AppId != expectedAppId) return new(false, "launcher_identity_mismatch");
            var target = NormalizeLocalPath(existing.TargetDir);
            var candidate = NormalizeLocalPath(executablePath);
            // 新入口必须仍在原安装目录之内：修复不能把启动指向别处的程序。
            if (!candidate.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return new(false, "launcher_invalid_path");
            CheckExistingAncestors(candidate);
            if (!Directory.Exists(target) || !File.Exists(candidate)) return new(false, "launcher_executable_missing");
            if (!IsPortableExecutable(candidate)) return new(false, "launcher_executable_invalid");
            ct.ThrowIfCancellationRequested();
            var next = entries.ToArray();
            // ExePath 已是规范化路径；其余字段原样保留，登记身份与元数据不受影响。
            next[index] = new Entry(entries[index].DisplayName, existing with { ExePath = candidate });
            await WriteAsync(next, ct).ConfigureAwait(false);
            changed = true;
        }
        catch (LauncherException ex) { return new(false, ex.Code); }
        finally { _gate.Release(); }
        if (changed) NotifyChanged();
        return new(true);
    }

    public async Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default)
    {
        if (!ValidText(installId, 256)) return new(false, "launcher_invalid_install_id");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Always reread persisted data by ID; a UI snapshot is never an executable launch request.
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            if (HasPendingRecovery(installId)) return new(false, "launcher_recovery_required");
            var entry = entries.SingleOrDefault(e => string.Equals(e.Installation.InstallId, installId, StringComparison.Ordinal));
            if (entry is null) return new(false, "launcher_not_found");
            var installation = ValidateInstallation(entry.Installation);
            EnsureExecutableAvailable(installation);
            ct.ThrowIfCancellationRequested();
            try
            {
                return _processStarter(installation.ExePath!, Path.GetDirectoryName(installation.ExePath!)!)
                    ? new(true) : new(false, "launcher_start_failed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Delegate/process errors can include arbitrary command text. Do not surface it.
                return new(false, "launcher_start_failed");
            }
        }
        catch (LauncherException ex) { return new(false, ex.Code); }
        finally { _gate.Release(); }
    }

    private bool HasPendingRecovery(string installId) =>
        GitHubStore.App.Services.Installation.PortableRecoveryState.RequiresRecovery(_dataDirectory, installId);

    private async Task<Entry[]> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var file = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaximumManifestBytes) throw new LauncherException("launcher_manifest_too_large");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await file.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaximumManifestBytes) throw new LauncherException("launcher_manifest_too_large");
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            var envelope = JsonSerializer.Deserialize<Envelope>(buffer.ToArray(), Json);
            if (envelope is null || envelope.SchemaVersion != 1 || envelope.Entries is null)
                throw new LauncherException("launcher_invalid_manifest");
            if (envelope.Entries.Length > MaximumEntries) throw new LauncherException("launcher_manifest_too_large");
            var installIds = new HashSet<string>(StringComparer.Ordinal);
            var appIds = new HashSet<AppId>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in envelope.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry is null || !ValidText(entry.DisplayName, 200)) throw new LauncherException("launcher_invalid_manifest");
                InstallRecord normalized;
                try { normalized = ValidateInstallation(entry.Installation); }
                catch (LauncherException) { throw new LauncherException("launcher_invalid_manifest"); }
                if (!installIds.Add(normalized.InstallId) || !appIds.Add(normalized.AppId) || !targets.Add(normalized.TargetDir!))
                    throw new LauncherException("launcher_invalid_manifest");
            }
            return envelope.Entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException) { throw new LauncherException("launcher_invalid_manifest"); }
        catch (NotSupportedException) { throw new LauncherException("launcher_invalid_manifest"); }
        catch (UnauthorizedAccessException) { throw new LauncherException("launcher_access_denied"); }
        catch (IOException) { throw new LauncherException("launcher_read_failed"); }
    }

    private async Task WriteAsync(Entry[] entries, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, entries), Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { throw new LauncherException("launcher_invalid_installation"); }
        if (bytes.Length > MaximumManifestBytes) throw new LauncherException("launcher_manifest_too_large");
        ct.ThrowIfCancellationRequested();
        var temporary = _manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _manifestPath, overwrite: true);
        }
        catch (UnauthorizedAccessException) { throw new LauncherException("launcher_access_denied"); }
        catch (IOException) { throw new LauncherException("launcher_write_failed"); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private InstallRecord ValidateInstallation(InstallRecord installation)
    {
        if (installation is null || installation.Origin != Origin.Store || installation.State != InstallState.Done
            || !ValidText(installation.InstallId, 256) || installation.AppId.SourceId.Value == Guid.Empty
            || !ValidText(installation.AppId.PackageId.Value, 256) || installation.Source is null
            || installation.Source.SourceId != installation.AppId.SourceId
            || installation.Source.PackageId != installation.AppId.PackageId
            || installation.Source.SourceRevision <= 0 || !ValidText(installation.Source.InstallerId, 256)
            || !SafeHttps(installation.Source.SourceEntryUrl) || installation.Source.ArtifactUrl is { } artifact && !SafeHttps(artifact)
            || !ValidText(installation.ReleaseKey, 256) || installation.Version is null || !ValidText(installation.Version.Raw, 256)
            || !Enum.IsDefined(installation.Channel) || !Enum.IsDefined(installation.Strategy) || !Enum.IsDefined(installation.Installer)
            || installation.Fingerprint?.AppId is { } fingerprint && fingerprint != installation.AppId)
            throw new LauncherException("launcher_invalid_installation");
        var target = NormalizeLocalPath(installation.TargetDir);
        var executable = NormalizeLocalPath(installation.ExePath);
        if (!SamePath(Path.GetDirectoryName(target) ?? string.Empty, _root)
            || !executable.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("launcher_invalid_path");
        return installation with { TargetDir = target, ExePath = executable };
    }

    private static string NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)) throw new LauncherException("launcher_invalid_path");
        try
        {
            var pathRoot = Path.GetPathRoot(path);
            if (pathRoot is null || pathRoot.Length != 3 || !char.IsAsciiLetter(pathRoot[0]) || pathRoot[1] != ':')
                throw new LauncherException("launcher_invalid_path");
            foreach (var part in path[pathRoot.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or ".." || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.')
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(part))
                    throw new LauncherException("launcher_invalid_path");
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new LauncherException("launcher_invalid_path"); }
    }

    private static bool IsReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    private static bool SafeHttps(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https" }
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    private static bool ValidText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximum && text == text.Trim() && !text.Any(char.IsControl);
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void CheckExistingAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LauncherException("launcher_unsafe_path");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { throw new LauncherException("launcher_access_denied"); }
            catch (IOException) { throw new LauncherException("launcher_path_unavailable"); }
        }
    }

    private static void EnsureExecutableAvailable(InstallRecord installation)
    {
        CheckExistingAncestors(installation.ExePath!);
        if (!Directory.Exists(installation.TargetDir!) || !File.Exists(installation.ExePath!))
            throw new LauncherException("launcher_executable_missing");
    }

    private static bool IsAvailable(InstallRecord installation)
    {
        try { EnsureExecutableAvailable(installation); return true; }
        catch (LauncherException) { return false; }
    }

    /// <summary>
    /// 修复入口时要求目标确为 Windows 可执行文件。与安装流程的校验口径一致：
    /// 只读取文件头，不执行任何内容；不满足时拒绝写入登记。
    /// </summary>
    private static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length < 64 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false;
            stream.Position = 60;
            Span<byte> offsetBytes = stackalloc byte[4];
            if (stream.Read(offsetBytes) != 4) return false;
            var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(offsetBytes);
            if (offset < 64 || offset > stream.Length - 24) return false;
            stream.Position = offset;
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != 24 || header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0) return false;
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
            return (flags & 2) != 0 && (flags & 0x2000) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool StartProcess(string executablePath, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        });
        return process is not null;
    }

    private void NotifyChanged()
    {
        if (Changed is not { } changed) return;
        foreach (EventHandler handler in changed.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { /* A subscriber cannot undo a committed registration or block another subscriber. */ }
        }
    }

    private sealed record Entry(string DisplayName, InstallRecord Installation);
    private sealed record Envelope(int SchemaVersion, Entry[] Entries);
}
