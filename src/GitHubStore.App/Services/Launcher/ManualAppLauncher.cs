using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>Stores explicit user-selected local exe/lnk entries separately from managed installations.</summary>
public sealed class ManualAppLauncher : IManualAppLauncher
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
    private readonly string _manifestPath;
    private readonly SemaphoreSlim _gate;
    private readonly Func<string, string, bool>? _processStarter;
    private readonly Func<ProcessStartInfo, bool>? _optionProcessStarter;

    public ManualAppLauncher(string dataDirectory, Func<string, string, bool>? processStarter = null,
        Func<ProcessStartInfo, bool>? optionProcessStarter = null)
    {
        _optionProcessStarter = optionProcessStarter;
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _manifestPath = Path.Combine(Path.GetFullPath(dataDirectory), "manual-launcher.json");
        _gate = Gates.GetOrAdd(_manifestPath, _ => new SemaphoreSlim(1, 1));
        _processStarter = processStarter;
    }

    public async Task<IReadOnlyList<ManualLauncherEntry>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            return Array.AsReadOnly(entries.Select(ToEntry).ToArray());
        }
        finally { _gate.Release(); }
    }

    public Task<ManualLauncherEntry> AddAsync(string path, string displayName, CategoryId? category = null,
        CancellationToken ct = default) => AddCoreAsync(path, displayName, category, null, ct);

    public Task<ManualLauncherEntry> AddProgramAsync(string path, string displayName, CategoryId? category,
        string arguments, CancellationToken ct = default) => AddCoreAsync(path, displayName, category, arguments, ct);

    private async Task<ManualLauncherEntry> AddCoreAsync(string path, string displayName, CategoryId? category,
        string? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (arguments is not null && !LauncherEntryOptionsValidation.IsValid(new LauncherEntryOptions { Arguments = arguments }))
            throw Error("invalid_options");
        var normalized = NormalizePath(path);
        if (!ValidText(displayName, 200)) throw Error("invalid_display_name");
        if (category is { } value && !ValidText(value.Value, 256)) throw Error("invalid_category");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Never replace an unreadable/corrupt index with an empty one.
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            EnsureAvailable(normalized);
            var existing = entries.SingleOrDefault(e => string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return ToEntry(existing);
            if (entries.Length >= MaximumEntries) throw Error("manifest_too_large");
            var entry = new Entry(Guid.NewGuid().ToString("N"), displayName, normalized, category, DateTimeOffset.UtcNow, arguments);
            await WriteAsync(entries.Append(entry).ToArray(), ct).ConfigureAwait(false);
            return ToEntry(entry);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualLauncherEntry> UpdateAsync(string id, string path, string displayName, CategoryId? category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidText(id, 256)) throw Error("invalid_id");
        var normalized = NormalizePath(path);
        if (!ValidText(displayName, 200)) throw Error("invalid_display_name");
        if (category is { } value && !ValidText(value.Value, 256)) throw Error("invalid_category");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var index = Array.FindIndex(entries, entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0) throw Error("not_found");
            var previous = entries[index];
            var samePath = string.Equals(previous.Path, normalized, StringComparison.OrdinalIgnoreCase);
            if (entries.Any(entry => entry.Id != id && string.Equals(entry.Path, normalized, StringComparison.OrdinalIgnoreCase)))
                throw Error("duplicate_path");
            // Metadata edits remain possible before a missing entry is repaired. A replacement must exist.
            if (!samePath) EnsureAvailable(normalized);
            var updated = previous with
            {
                Path = samePath ? previous.Path : normalized, DisplayName = displayName, Category = category,
            };
            if (updated != previous)
            {
                entries[index] = updated;
                await WriteAsync(entries, ct).ConfigureAwait(false);
            }
            return ToEntry(updated);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualLauncherEntry> UpdateProgramAsync(string id, string path, string arguments,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidText(id, 256)) throw Error("invalid_id");
        var normalized = NormalizePath(path);
        if (!LauncherEntryOptionsValidation.IsValid(new LauncherEntryOptions { Arguments = arguments }))
            throw Error("invalid_options");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var index = Array.FindIndex(entries, entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0) throw Error("not_found");
            var previous = entries[index];
            var samePath = string.Equals(previous.Path, normalized, StringComparison.OrdinalIgnoreCase);
            if (entries.Any(entry => entry.Id != id && string.Equals(entry.Path, normalized, StringComparison.OrdinalIgnoreCase)))
                throw Error("duplicate_path");
            if (!samePath) EnsureAvailable(normalized);
            var updated = previous with { Path = samePath ? previous.Path : normalized, Arguments = arguments };
            if (updated != previous)
            {
                entries[index] = updated;
                await WriteAsync(entries, ct).ConfigureAwait(false);
            }
            return ToEntry(updated);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        if (!ValidText(id, 256)) throw Error("invalid_id");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var next = entries.Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal)).ToArray();
            if (next.Length != entries.Length) await WriteAsync(next, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<LauncherLaunchResult> LaunchAsync(string id, CancellationToken ct = default)
    {
        if (!ValidText(id, 256)) return new(false, "manual_launcher_invalid_id");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Only the freshly read persisted ID selects the launch target; never trust a UI snapshot.
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var entry = entries.SingleOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (entry is null) return new(false, "manual_launcher_not_found");
            EnsureAvailable(entry.Path);
            ct.ThrowIfCancellationRequested();
            // Leave lnk working-directory resolution to ShellExecute and the shortcut itself.
            var directory = Path.GetExtension(entry.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? string.Empty : Path.GetDirectoryName(entry.Path)!;
            try
            {
                bool started;
                if (!string.IsNullOrEmpty(entry.Arguments))
                {
                    var info = new ProcessStartInfo
                    {
                        FileName = entry.Path, WorkingDirectory = directory,
                        Arguments = entry.Arguments, UseShellExecute = true,
                    };
                    started = _optionProcessStarter is not null ? _optionProcessStarter(info) : StartProcess(info);
                }
                else
                    started = _processStarter is not null
                        ? _processStarter(entry.Path, directory)
                        : StartProcess(entry.Path, directory);
                return started ? new(true) : new(false, "manual_launcher_start_failed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(false, "manual_launcher_start_failed"); }
        }
        catch (LauncherException ex) { return new(false, ex.Code); }
        finally { _gate.Release(); }
    }

    public async Task<LauncherLaunchResult> LaunchWithOptionsAsync(string id, LauncherEntryOptions options,
        CancellationToken ct = default)
    {
        if (!LauncherEntryOptionsValidation.IsValid(options)) return new(false, "manual_launcher_invalid_options");
        if (!LauncherEntryOptionsValidation.RequiresCustomLaunch(options))
            return await LaunchAsync(id, ct).ConfigureAwait(false);
        if (!ValidText(id, 256)) return new(false, "manual_launcher_invalid_id");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var entry = entries.SingleOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (entry is null) return new(false, "manual_launcher_not_found");
            EnsureAvailable(entry.Path);
            ct.ThrowIfCancellationRequested();
            var isShortcut = Path.GetExtension(entry.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            var directory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? isShortcut ? string.Empty : Path.GetDirectoryName(entry.Path)!
                : options.WorkingDirectory!;
            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && !Directory.Exists(options.WorkingDirectory))
                return new(false, "manual_launcher_invalid_working_directory");
            var startInfo = new ProcessStartInfo
            {
                FileName = entry.Path,
                Arguments = entry.Arguments ?? options.Arguments,
                WorkingDirectory = directory,
                UseShellExecute = true,
                Verb = options.RunAsAdministrator ? "runas" : string.Empty,
            };
            try
            {
                var started = _optionProcessStarter is not null ? _optionProcessStarter(startInfo) : StartProcess(startInfo);
                return started ? new(true) : new(false, "manual_launcher_start_failed");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new(false, "manual_launcher_launch_cancelled");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(false, "manual_launcher_start_failed"); }
        }
        catch (LauncherException ex) { return new(false, ex.Code); }
        finally { _gate.Release(); }
    }

    private static bool StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private async Task<Entry[]> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var file = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaximumManifestBytes) throw Error("manifest_too_large");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await file.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaximumManifestBytes) throw Error("manifest_too_large");
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            var envelope = JsonSerializer.Deserialize<Envelope>(buffer.ToArray(), Json);
            if (envelope is null || envelope.SchemaVersion != 1 || envelope.Entries is null) throw Error("invalid_manifest");
            if (envelope.Entries.Length > MaximumEntries) throw Error("manifest_too_large");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new Entry[envelope.Entries.Length];
            for (var i = 0; i < result.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = envelope.Entries[i];
                if (entry is null || !ValidText(entry.Id, 256) || !ValidText(entry.DisplayName, 200)
                    || entry.Arguments is not null && !LauncherEntryOptionsValidation.IsValid(new LauncherEntryOptions { Arguments = entry.Arguments })
                    || entry.AddedAt == default || entry.Category is { } category && !ValidText(category.Value, 256))
                    throw Error("invalid_manifest");
                string normalized;
                try { normalized = NormalizePath(entry.Path); }
                catch (LauncherException) { throw Error("invalid_manifest"); }
                if (!ids.Add(entry.Id) || !paths.Add(normalized)) throw Error("invalid_manifest");
                result[i] = entry with { Path = normalized };
            }
            return result;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException) { throw Error("invalid_manifest"); }
        catch (NotSupportedException) { throw Error("invalid_manifest"); }
        catch (UnauthorizedAccessException) { throw Error("access_denied"); }
        catch (IOException) { throw Error("read_failed"); }
    }

    private async Task WriteAsync(Entry[] entries, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, entries), Json);
        if (bytes.Length > MaximumManifestBytes) throw Error("manifest_too_large");
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
        catch (UnauthorizedAccessException) { throw Error("access_denied"); }
        catch (IOException) { throw Error("write_failed"); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)) throw Error("invalid_path");
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':')
                throw Error("invalid_path");
            foreach (var part in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or ".." || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.')
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(part))
                    throw Error("invalid_path");
            }
            var normalized = Path.GetFullPath(path);
            var extension = Path.GetExtension(normalized);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) throw Error("invalid_path");
            return normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Error("invalid_path"); }
    }

    private static bool IsReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    internal static void EnsureAvailable(string path)
    {
        // Reject filesystem redirections that could turn a local path into UNC.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Error("unsafe_path");
            }
            catch (FileNotFoundException) { throw Error("entry_missing"); }
            catch (DirectoryNotFoundException) { throw Error("entry_missing"); }
            catch (UnauthorizedAccessException) { throw Error("access_denied"); }
            catch (IOException) { throw Error("path_unavailable"); }
        }
        if (!File.Exists(path)) throw Error("entry_missing");
    }

    private static ManualLauncherEntry ToEntry(Entry entry)
    {
        var available = true;
        try { EnsureAvailable(entry.Path); }
        catch (LauncherException) { available = false; }
        return new ManualLauncherEntry
        {
            Id = entry.Id, DisplayName = entry.DisplayName, Path = entry.Path,
            Category = entry.Category, AddedAt = entry.AddedAt, IsAvailable = available, Arguments = entry.Arguments,
        };
    }

    private static bool StartProcess(string path, string directory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = directory,
            UseShellExecute = true,
        });
        // ShellExecute may successfully activate an existing application without
        // returning a process handle, especially for shortcuts. Exceptions signal failure.
        return true;
    }

    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    private static LauncherException Error(string suffix) => new("manual_launcher_" + suffix);
    private sealed record Entry(string Id, string DisplayName, string Path, CategoryId? Category, DateTimeOffset AddedAt,
        string? Arguments = null);
    private sealed record Envelope(int SchemaVersion, Entry[] Entries);
}
