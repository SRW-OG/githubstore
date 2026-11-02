using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>Persists launcher metadata only; never changes installation receipts or application files.</summary>
public sealed class LauncherCustomizationStore : ILauncherCustomizationStore
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumEntries = 4096;
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

    public LauncherCustomizationStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _manifestPath = Path.Combine(Path.GetFullPath(dataDirectory), "launcher-customizations.json");
        _gate = Gates.GetOrAdd(_manifestPath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<LauncherCustomization>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return Array.AsReadOnly(await ReadAsync(ct).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    public Task<LauncherCustomization> RecordLaunchAsync(bool isManual, string entryId, CancellationToken ct = default) =>
        MutateAsync(isManual, entryId, entry => entry with
        {
            LaunchCount = entry.LaunchCount == long.MaxValue ? long.MaxValue : entry.LaunchCount + 1,
        }, ct);

    public Task<LauncherCustomization> UpdatePropertiesAsync(bool isManual, string entryId, string displayName,
        CategoryId? category, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidText(displayName, 200)) throw Error("invalid_display_name");
        if (category is { } value && !ValidText(value.Value, 256)) throw Error("invalid_category");
        return MutateAsync(isManual, entryId, entry => entry with
        {
            DisplayName = displayName, HasCategoryOverride = true, Category = category,
        }, ct);
    }

    public Task<LauncherCustomization> SavePropertiesAsync(bool isManual, string entryId, string displayName,
        CategoryId? category, LauncherEntryOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidText(displayName, 200)) throw Error("invalid_display_name");
        if (category is { } value && !ValidText(value.Value, 256)) throw Error("invalid_category");
        if (!LauncherEntryOptionsValidation.IsValid(options))
        {
            if (options?.Color is { Length: > 0 } color && (color.Length is not (7 or 9) || color[0] != '#'))
                throw Error("invalid_color");
            if (options?.Hotkey is { Length: > 80 }) throw Error("invalid_hotkey");
            if (options?.Arguments is { Length: > 8192 } || options?.Notes is { Length: > 4000 })
                throw Error("invalid_options");
            throw Error("invalid_options");
        }
        return MutateAsync(isManual, entryId, entry => entry with
        {
            DisplayName = displayName, HasCategoryOverride = true, Category = category, Options = options,
        }, ct);
    }

    private async Task<LauncherCustomization> MutateAsync(bool isManual, string entryId,
        Func<LauncherCustomization, LauncherCustomization> update, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidText(entryId, 256)) throw Error("invalid_id");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Always read before mutation, so corruption cannot be overwritten by a newly created index.
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            var index = Array.FindIndex(entries, entry => entry.IsManual == isManual
                && string.Equals(entry.EntryId, entryId, StringComparison.Ordinal));
            var previous = index >= 0 ? entries[index] : new LauncherCustomization { EntryId = entryId, IsManual = isManual };
            var updated = update(previous);
            if (index >= 0)
            {
                if (updated == previous) return previous;
                entries[index] = updated;
            }
            else
            {
                if (entries.Length >= MaximumEntries) throw Error("manifest_too_large");
                entries = entries.Append(updated).ToArray();
            }
            await WriteAsync(entries, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task<LauncherCustomization[]> ReadAsync(CancellationToken ct)
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
            var keys = new HashSet<(bool IsManual, string EntryId)>();
            foreach (var entry in envelope.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry is null || !ValidText(entry.EntryId, 256) || entry.LaunchCount < 0
                    || entry.DisplayName is not null && !ValidText(entry.DisplayName, 200)
                    || entry.Category is { } category && !ValidText(category.Value, 256)
                    || !entry.HasCategoryOverride && entry.Category is not null
                    || !LauncherEntryOptionsValidation.IsValid(entry.Options)
                    || !keys.Add((entry.IsManual, entry.EntryId))) throw Error("invalid_manifest");
            }
            return envelope.Entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException) { throw Error("invalid_manifest"); }
        catch (NotSupportedException) { throw Error("invalid_manifest"); }
        catch (UnauthorizedAccessException) { throw Error("access_denied"); }
        catch (IOException) { throw Error("read_failed"); }
    }

    private async Task WriteAsync(LauncherCustomization[] entries, CancellationToken ct)
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

    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    private static LauncherException Error(string suffix) => new("launcher_customization_" + suffix);
    private sealed record Envelope(int SchemaVersion, LauncherCustomization[] Entries);
}
