using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitHubStore.App.Services.Download;

public sealed record CachedPackage(string Path, long Size);
public sealed record PackageCacheSummary(int FileCount, long TotalBytes, string Root)
{
    public static PackageCacheSummary Empty(string root) => new(0, 0, root);
}
public sealed record PackageCacheClearResult(int DeletedFiles, int SkippedFiles, int RemainingFiles, long FreedBytes, bool Busy);

/// <summary>Hold through download and installer consumption; disposal releases only locks.</summary>
public sealed class PackageCacheLease : IAsyncDisposable
{
    private FileStream? _lock;
    private readonly SemaphoreSlim _gate;
    public PackageCacheService Cache { get; }
    internal PackageCacheLease(PackageCacheService cache, SemaphoreSlim gate, FileStream fileLock)
    { Cache = cache; _gate = gate; _lock = fileLock; }
    public ValueTask DisposeAsync()
    {
        var fileLock = Interlocked.Exchange(ref _lock, null);
        if (fileLock is not null)
        {
            try { fileLock.Dispose(); }
            finally { _gate.Release(); }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Persistent offline cache: only explicit ClearAsync deletes packages.
/// Mutating callers hold AcquireAsync throughout the download/install operation.
/// Receipts verify local bytes; they do not attest to publisher authenticity.
/// </summary>
public sealed class PackageCacheService
{
    public const int KeyLength = 64;
    private const string NamespaceName = "packages-v1";
    private const string MarkerName = ".cache-owner";
    private const string MarkerText = "GitHubStore.PackageCache/v1\n";
    private const string LockName = ".cache-lease";
    private const int MaximumEntries = 10_000;
    private const int MaximumReceiptBytes = 16_384;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string> _rootProvider;

    public PackageCacheService(Func<string> rootProvider)
    { ArgumentNullException.ThrowIfNull(rootProvider); _rootProvider = rootProvider; }
    public PackageCacheService(string root) : this(() => root) { }
    public string Root => _rootProvider();

    public static string KeyFor(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri)));
    }

    public static bool IsSafeFileName(string name) => !string.IsNullOrWhiteSpace(name)
        && name.Length <= 255 && name is not ("." or "..")
        && !name.EndsWith(' ') && !name.EndsWith('.')
        && !Regex.IsMatch(name, "[<>:\"/\\\\|?*\\x00-\\x1f]")
        && !Regex.IsMatch(name.Split('.')[0].TrimEnd(' '), "^(CON|PRN|AUX|NUL|CLOCK\\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase)
        && !name.StartsWith(".cache-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith(".retained-invalid-", StringComparison.OrdinalIgnoreCase);

    public string? TryResolvePath(Uri source, string fileName)
    {
        if (source is null || !source.IsAbsoluteUri || !IsSafeFileName(fileName)) return null;
        try
        {
            var root = NormalizeRoot(Root);
            var owned = OwnedDirectory(root, false);
            var path = Path.GetFullPath(Path.Combine(owned, KeyFor(source), fileName));
            EnsureSafePath(path);
            return path;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    public async Task<PackageCacheLease> AcquireAsync(CancellationToken ct = default)
    {
        var root = NormalizeRoot(Root);
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FileStream fileLock;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { fileLock = OpenLock(OwnedDirectory(root, true)); break; }
                catch (IOException ex) when (IsSharingViolation(ex) || (ex.HResult & 0xffff) is 80 or 183)
                { await Task.Delay(50, ct).ConfigureAwait(false); }
            }
            return new PackageCacheLease(new PackageCacheService(root), gate, fileLock);
        }
        catch { gate.Release(); throw; }
    }

    public async Task<CachedPackage?> FindVerifiedAsync(Uri source, string fileName,
        long? expectedSize, string? expectedDigest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var digest = NormalizeDigest(expectedDigest);
        if (HasDigest(expectedDigest) && digest is null) return null;
        var path = TryResolvePath(source, fileName);
        if (path is null) return null;
        try
        {
            await using var stream = OpenPackage(path);
            if (stream.Length <= 0 || (expectedSize is > 0 && stream.Length != expectedSize)) return null;
            var receipt = await ReadReceiptAsync(path, ct).ConfigureAwait(false);
            // A publisher hash independently verifies an unreceipted package.
            if (receipt is not null && (receipt.Source != source.AbsoluteUri || receipt.FileName != fileName
                || receipt.Size != stream.Length || NormalizeDigest(receipt.Sha256) is null)) return null;
            if (digest is null && receipt is null) return null;
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (digest is not null && actual != digest) return null;
            if (receipt is not null && actual != NormalizeDigest(receipt.Sha256)) return null;
            return new CachedPackage(path, stream.Length);
        }
        catch (Exception ex) when (IsPathFailure(ex) || ex is JsonException) { return null; }
    }

    public async Task RecordCompletedAsync(Uri source, string filename, long? expectedSize,
        string? expectedDigest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var digest = NormalizeDigest(expectedDigest);
        if (HasDigest(expectedDigest) && digest is null) throw new ArgumentException("Invalid SHA-256 digest.", nameof(expectedDigest));
        var path = RequirePath(source, filename, true);
        await using var stream = OpenPackage(path);
        if (stream.Length <= 0 || (expectedSize is > 0 && stream.Length != expectedSize))
            throw new IOException("The completed package has an invalid size.");
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (digest is not null && actual != digest) throw new IOException("The completed package SHA-256 does not match.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Receipt(source.AbsoluteUri, filename, stream.Length, actual));
        if (bytes.Length > MaximumReceiptBytes) throw new IOException("Package receipt exceeds the size limit.");
        var receiptPath = ReceiptPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".cache-receipt-pending-" + Guid.NewGuid().ToString("N"));
        EnsureSafePath(temporary);
        // WriteThrough makes the receipt durable when the stream closes, so no blocking Flush(true) is needed.
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        // Failure preserves the package and any bounded temporary metadata for manual clear.
        EnsureSafePath(temporary);
        EnsureSafePath(receiptPath);
        try { File.Move(temporary, receiptPath, true); }
        // 收据文件被占用等失败必须统一成 IOException：调用方据此区分“包已保留但记录未写成”，
        // 不能让 UnauthorizedAccessException 冒到安装流程之外。
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { throw new IOException("The package verification record could not be written.", ex); }
    }

    public Task PrepareDownloadAsync(Uri source, string filename, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = RequirePath(source, filename, true);
        if (File.Exists(path))
        {
            var backup = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".retained-invalid-" + Guid.NewGuid().ToString("N") + ".package"));
            // Check both absolute paths immediately before moving; never overwrite backups.
            EnsureSafePath(path);
            EnsureSafePath(backup);
            File.Move(path, backup, false);
            var receipt = ReceiptPath(path);
            if (File.Exists(receipt))
            {
                var retainedReceipt = backup + ".cache-receipt";
                EnsureSafePath(receipt);
                EnsureSafePath(retainedReceipt);
                File.Move(receipt, retainedReceipt, false);
            }
        }
        return Task.CompletedTask;
    }

    public Task<PackageCacheSummary> SummarizeAsync(CancellationToken ct = default)
    {
        var root = NormalizeRoot(Root);
        var entries = Snapshot(OwnedDirectory(root, false), ct);
        if (entries.Any(e => e.Unsafe)) throw new IOException("Cache contains an unsafe reparse point.");
        var files = entries.Where(e => !e.Directory && !IsMetadata(e.Path)).ToArray();
        return Task.FromResult(new PackageCacheSummary(files.Length, files.Sum(e => e.Size), root));
    }

    public async Task<PackageCacheClearResult> ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = NormalizeRoot(Root);
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        // 非阻塞获取：另一个下载或安装正在使用缓存时立即返回 busy，不排队等待。
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new PackageCacheClearResult(0, 0, 0, 0, true);
        try
        {
            var owned = OwnedDirectory(root, false);
            if (!Directory.Exists(owned) || !File.Exists(Path.Combine(owned, MarkerName)))
                return new PackageCacheClearResult(0, 0, 0, 0, false);
            FileStream fileLock;
            try { fileLock = OpenLock(owned); }
            catch (IOException ex) when (IsSharingViolation(ex))
            { return new PackageCacheClearResult(0, 0, 0, 0, true); }
            using (fileLock)
            {
                var entries = Snapshot(owned, ct);
                var deleted = 0;
                var skipped = 0;
                long freed = 0;
                foreach (var entry in entries.Where(e => !e.Directory || e.Unsafe))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Unsafe) { skipped++; continue; }
                    try
                    {
                        EnsureSafePath(entry.Path);
                        using (var probe = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Delete))
                        {
                            var size = probe.Length;
                            EnsureSafePath(entry.Path);
                            File.Delete(entry.Path);
                            deleted++;
                            freed += size;
                        }
                    }
                    catch (Exception ex) when (IsPathFailure(ex)) { skipped++; }
                }
                // Keep directories, marker and lock. Never recursively delete a directory.
                var remaining = Snapshot(owned, ct).Count(e => !e.Directory || e.Unsafe);
                return new PackageCacheClearResult(deleted, skipped, remaining, freed, false);
            }
        }
        finally { gate.Release(); }
    }

    public static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        return text.Length == 64 && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;
    }

    private static bool HasDigest(string? value) => !string.IsNullOrEmpty(value);
    private static bool IsPathFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33 or 11;
    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
            || root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("Unsafe package cache root.");
        var rawRoot = Path.GetPathRoot(root)!;
        if (root[rawRoot.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Length > 0 && !IsSafeFileName(s))) throw new IOException("Unsafe package cache root segment.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) throw new IOException("A volume root cannot be a package cache.");
        EnsureSafePath(full);
        return full;
    }

    private static void EnsureSafePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Expected an absolute cache path.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache reparse points are refused.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string OwnedDirectory(string root, bool create)
    {
        EnsureSafePath(root);
        var owned = Path.Combine(root, NamespaceName);
        EnsureSafePath(owned);
        if (!Directory.Exists(owned))
        {
            if (!create) return owned;
            Directory.CreateDirectory(owned);
            EnsureSafePath(owned);
        }
        var marker = Path.Combine(owned, MarkerName);
        EnsureSafePath(marker);
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(owned).Any()) throw new IOException("Unmarked nonempty package namespace is not owned.");
            if (!create) return owned;
            using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(Encoding.UTF8.GetBytes(MarkerText));
            output.Flush(true);
        }
        else
        {
            using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != Encoding.UTF8.GetByteCount(MarkerText)) throw new IOException("Invalid cache ownership marker.");
            using var reader = new StreamReader(input, Encoding.UTF8);
            if (reader.ReadToEnd() != MarkerText) throw new IOException("Invalid cache ownership marker.");
        }
        return owned;
    }

    private string RequirePath(Uri source, string filename, bool create)
    {
        var root = NormalizeRoot(Root);
        OwnedDirectory(root, create);
        var path = new PackageCacheService(root).TryResolvePath(source, filename)
            ?? throw new IOException("Unsafe package cache asset path.");
        if (create) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EnsureSafePath(path);
        return path;
    }

    private static FileStream OpenLock(string owned)
    {
        var path = Path.Combine(owned, LockName);
        EnsureSafePath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static FileStream OpenPackage(string path)
    {
        EnsureSafePath(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }
    private static string ReceiptPath(string path) => Path.Combine(Path.GetDirectoryName(path)!,
        ".cache-receipt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path)))) + ".json");
    private static async Task<Receipt?> ReadReceiptAsync(string package, CancellationToken ct)
    {
        var path = ReceiptPath(package);
        EnsureSafePath(path);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is <= 0 or > MaximumReceiptBytes) throw new IOException("Invalid receipt size.");
        return await JsonSerializer.DeserializeAsync<Receipt>(stream, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new IOException("Empty package receipt.");
    }
    private static bool IsMetadata(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(".cache-", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith(".retained-invalid-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".cache-receipt", StringComparison.OrdinalIgnoreCase));
    }
    private static List<Entry> Snapshot(string owned, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Entry>();
        if (!Directory.Exists(owned)) return result;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((owned, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            EnsureSafePath(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > MaximumEntries) throw new IOException("Package cache traversal limit exceeded.");
                var attributes = File.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var unsafeEntry = (attributes & FileAttributes.ReparsePoint) != 0;
                if (!unsafeEntry) EnsureSafePath(path);
                if (directory == owned && Path.GetFileName(path) is MarkerName or LockName)
                {
                    if (unsafeEntry) throw new IOException("Unsafe cache metadata link.");
                    continue;
                }
                result.Add(new Entry(path, isDirectory, unsafeEntry, isDirectory || unsafeEntry ? 0 : new FileInfo(path).Length));
                if (isDirectory && !unsafeEntry)
                {
                    if (depth >= 8) throw new IOException("Package cache depth limit exceeded.");
                    pending.Push((path, depth + 1));
                }
            }
        }
        return result;
    }
    private sealed record Receipt(string Source, string FileName, long Size, string Sha256);
    private sealed record Entry(string Path, bool Directory, bool Unsafe, long Size);
}
