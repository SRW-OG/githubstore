using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Tools;

/// <summary>Prepares bundled aria2 offline and resolves system/built-in tools without executing them.</summary>
public sealed class ManagedToolManager : IToolManager
{
    private static readonly Uri XgetEndpoint = new("https://xget.xi-xu.me/");
    private readonly IToolPackageSource _packageSource;
    private readonly Func<CancellationToken, Task<string?>> _systemWingetResolver;
    private readonly string _architecture;
    private readonly string _localAppDataDirectory;
    private readonly ManagedToolLimits _limits;

    public ManagedToolManager(string dataDirectory, IToolPackageSource packageSource,
        Func<CancellationToken, Task<string?>> systemWingetResolver, string? architecture = null,
        string? localAppDataDirectory = null, ManagedToolLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("The application data directory must be absolute.", nameof(dataDirectory));
        ToolDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "tools");
        _packageSource = packageSource ?? throw new ArgumentNullException(nameof(packageSource));
        _systemWingetResolver = systemWingetResolver ?? throw new ArgumentNullException(nameof(systemWingetResolver));
        _architecture = (architecture ?? RuntimeInformation.OSArchitecture.ToString()).ToLowerInvariant();
        _localAppDataDirectory = localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _limits = limits ?? new ManagedToolLimits();
        if (_limits.MaxArchiveBytes <= 0 || _limits.MaxExecutableBytes <= 0 || _limits.LockTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public string ToolDirectory { get; }

    public async Task<ToolResolution> EnsureAsync(ManagedToolKind kind, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return kind switch
            {
                ManagedToolKind.Xget => new ToolResolution
                {
                    Kind = kind, IsAvailable = true, Endpoint = XgetEndpoint, Origin = "built-in",
                },
                ManagedToolKind.Winget => await ResolveWingetAsync(ct).ConfigureAwait(false),
                ManagedToolKind.Aria2 => await PrepareAria2Async(ct).ConfigureAwait(false),
                _ => Failure(kind, ToolErrorCodes.UnsupportedTool),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(kind, ToolErrorCodes.Cancelled);
        }
        catch (ToolPackageException ex) { return Failure(kind, ex.ErrorCode); }
        catch (UnauthorizedAccessException) { return Failure(kind, ToolErrorCodes.AccessDenied); }
        catch (System.Security.SecurityException) { return Failure(kind, ToolErrorCodes.AccessDenied); }
        catch (InvalidDataException) { return Failure(kind, ToolErrorCodes.InvalidArchive); }
        catch (IOException) { return Failure(kind, ToolErrorCodes.IoError); }
        catch (ArgumentException) { return Failure(kind, ToolErrorCodes.InvalidManifest); }
        catch (NotSupportedException) { return Failure(kind, ToolErrorCodes.InvalidManifest); }
    }

    private async Task<ToolResolution> ResolveWingetAsync(CancellationToken ct)
    {
        var resolved = await _systemWingetResolver(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var path = ExistingAbsoluteFile(resolved);
        if (path is null && Path.IsPathFullyQualified(_localAppDataDirectory))
        {
            path = ExistingAbsoluteFile(Path.Combine(_localAppDataDirectory, "Microsoft", "WindowsApps", "winget.exe"));
        }
        return path is null
            ? Failure(ManagedToolKind.Winget, ToolErrorCodes.SystemToolMissing, "system")
            : new ToolResolution { Kind = ManagedToolKind.Winget, IsAvailable = true, ExecutablePath = path, Origin = "system" };
    }

    private static string? ExistingAbsoluteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        var absolute = Path.GetFullPath(path);
        return File.Exists(absolute) ? absolute : null;
    }

    private async Task<ToolResolution> PrepareAria2Async(CancellationToken ct)
    {
        var packageArchitecture = _architecture switch
        {
            "x64" => "x64",
            "x86" or "arm64" => "x86",
            _ => null,
        };
        if (packageArchitecture is null)
            return Failure(ManagedToolKind.Aria2, ToolErrorCodes.UnsupportedArchitecture);
        // Always read the bundled version before considering a cache; no preference or ambient path is consulted.
        var package = await _packageSource.GetPackageAsync(ManagedToolKind.Aria2, packageArchitecture, ct).ConfigureAwait(false);
        if (package is null) return Failure(ManagedToolKind.Aria2, ToolErrorCodes.PackageMissing);
        ValidatePackage(package, packageArchitecture);
        var origin = _architecture == "arm64" ? "bundled-x86-compatibility-non-native" : "bundled";
        var directory = Path.Combine(ToolDirectory, "aria2", package.Version, packageArchitecture);
        var executable = Path.Combine(directory, "aria2c.exe");
        EnsureSafeDirectory(directory);
        // A persistent file with an exclusive OS handle serializes independent manager instances and processes.
        // Never delete the lock: unlinking it while a waiter owns the old inode would break cross-process exclusion.
        await using var preparationLock = await AcquireLockAsync(Path.Combine(directory, ".prepare.lock"), ct).ConfigureAwait(false);
        EnsureNotReparsePoint(executable);
        if (await IsVerifiedExecutableAsync(executable, package.ExecutableSha256, ct).ConfigureAwait(false))
            return Ready(package, executable, origin);

        var temporaryDirectory = Path.Combine(directory, ".prepare-" + Guid.NewGuid().ToString("N"));
        var ownsTemporaryDirectory = false;
        var ownedFiles = new List<string>();
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporaryDirectory);
            ownsTemporaryDirectory = true;
            EnsureNotReparsePoint(temporaryDirectory);
            var archivePath = Path.Combine(temporaryDirectory, "package.zip");
            var candidatePath = Path.Combine(temporaryDirectory, "aria2c.exe");
            await using (var input = await _packageSource.OpenArchiveAsync(package, ct).ConfigureAwait(false))
            await using (var archiveOutput = CreateOwnedFile(archivePath, ownedFiles))
            {
                var hash = await CopyAndHashAsync(input, archiveOutput, _limits.MaxArchiveBytes, ct).ConfigureAwait(false);
                if (!HashEquals(hash, package.ArchiveSha256))
                    throw new ToolPackageException(ToolErrorCodes.ArchiveChecksumMismatch);
            }

            // ZIP parsing happens only after the entire compressed archive has passed its checksum.
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var entries = archive.Entries.Where(entry => entry.FullName == package.ExecutableEntry).ToArray();
                if (entries.Length != 1) throw new ToolPackageException(ToolErrorCodes.InvalidArchive);
                var entry = entries[0];
                if (entry.Length <= 0) throw new ToolPackageException(ToolErrorCodes.InvalidArchive);
                if (entry.Length > _limits.MaxExecutableBytes)
                    throw new ToolPackageException(ToolErrorCodes.PackageTooLarge);
                // Reject symlinks and directory entries even though extraction uses our own fixed filename.
                if (((entry.ExternalAttributes >> 16) & 0xf000) is 0xa000 or 0x4000)
                    throw new ToolPackageException(ToolErrorCodes.InvalidArchive);
                await using var input = entry.Open();
                await using var output = CreateOwnedFile(candidatePath, ownedFiles);
                var hash = await CopyAndHashAsync(input, output, _limits.MaxExecutableBytes, ct).ConfigureAwait(false);
                if (output.Length != entry.Length) throw new ToolPackageException(ToolErrorCodes.InvalidArchive);
                if (!HashEquals(hash, package.ExecutableSha256))
                    throw new ToolPackageException(ToolErrorCodes.ExecutableChecksumMismatch);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            EnsureSafeDirectory(directory);
            EnsureNotReparsePoint(executable);
            // Same-volume rename publishes a complete verified executable in one operation, including cache repair.
            File.Move(candidatePath, executable, overwrite: true);
            return Ready(package, executable, origin);
        }
        finally
        {
            if (ownsTemporaryDirectory) CleanupOwnTemporaryDirectory(temporaryDirectory, ownedFiles);
        }
    }

    private async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                EnsureNotReparsePoint(path);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (elapsed.Elapsed >= _limits.LockTimeout) throw new ToolPackageException(ToolErrorCodes.Busy);
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xffff;
        // Windows sharing/lock violation and Unix EAGAIN from an exclusive FileStream lock.
        return code is 32 or 33 or 11;
    }

    private async Task<bool> IsVerifiedExecutableAsync(string path, string expectedHash, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (input.Length <= 0 || input.Length > _limits.MaxExecutableBytes) return false;
        var hash = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        return HashEquals(hash, expectedHash);
    }

    private static FileStream CreateOwnedFile(string path, List<string> ownedFiles)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        ownedFiles.Add(path);
        return stream;
    }

    private static async Task<byte[]> CopyAndHashAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (read > limit - length) throw new ToolPackageException(ToolErrorCodes.PackageTooLarge);
            length += read;
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return hash.GetHashAndReset();
    }

    private static bool HashEquals(byte[] hash, string expected) =>
        CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));

    private static void ValidatePackage(BundledToolPackage package, string architecture)
    {
        if (!SafeVersion(package.Version) || !string.Equals(package.Architecture, architecture, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(package.ArchiveResourceName)
            || !ValidHash(package.ArchiveSha256) || !ValidHash(package.ExecutableSha256))
            throw new ToolPackageException(ToolErrorCodes.InvalidManifest);
        if (!SafeEntry(package.ExecutableEntry)) throw new ToolPackageException(ToolErrorCodes.UnsafePath);
    }

    private static bool SafeVersion(string? value) => value is { Length: > 0 and <= 128 }
        && value is not "." and not ".." && !value.EndsWith('.')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static bool SafeEntry(string? entry)
    {
        if (string.IsNullOrEmpty(entry) || entry.Length > 1024 || entry.Contains('\\') || entry.Contains(':') || entry.StartsWith('/'))
            return false;
        var segments = entry.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".."
                && !segment.Any(character => char.IsControl(character) || character is '*' or '?' or '"' or '<' or '>' or '|'))
            && string.Equals(segments[^1], "aria2c.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeDirectory(string path)
    {
        CheckDirectoryChain(path);
        Directory.CreateDirectory(path);
        CheckDirectoryChain(path);
    }

    private static void CheckDirectoryChain(string path)
    {
        for (DirectoryInfo? current = new DirectoryInfo(path); current is not null; current = current.Parent)
            EnsureNotReparsePoint(current.FullName);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ToolPackageException(ToolErrorCodes.UnsafePath);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void CleanupOwnTemporaryDirectory(string directory, List<string> ownedFiles)
    {
        // Do not follow a directory replaced by a reparse point during preparation.
        try { CheckDirectoryChain(directory); }
        catch (ToolPackageException) { return; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (System.Security.SecurityException) { return; }
        // Never recurse or enumerate; names are recorded only after this attempt creates a file.
        foreach (var path in ownedFiles)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        try { Directory.Delete(directory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    private static ToolResolution Ready(BundledToolPackage package, string path, string origin) => new()
    {
        Kind = ManagedToolKind.Aria2, IsAvailable = true, ExecutablePath = path, Version = package.Version, Origin = origin,
    };

    private static ToolResolution Failure(ManagedToolKind kind, string code, string? origin = null) => new()
    {
        Kind = kind, ErrorCode = code, Origin = origin,
    };
}
