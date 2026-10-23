using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GitHubStore.App.Services.Tools;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>Purely offline fixtures: no HTTP clients, process launches, system installs, or environment mutation.</summary>
[Trait("Category", "Unit")]
public sealed class ManagedToolManagerTests
{
    private static readonly byte[] Executable = Encoding.UTF8.GetBytes("synthetic aria2 executable; never execute");

    [Fact]
    public async Task OfflinePreparation_PublishesOnlyManifestExecutable_InVersionedToolDirectory()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource(extraEntry: "unrelated/do-not-extract.txt");
        var manager = Manager(directory, source);

        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        Assert.True(result.IsAvailable);
        Assert.Null(result.ErrorCode);
        Assert.Equal("1.37.0", result.Version);
        Assert.Equal("bundled", result.Origin);
        Assert.Equal(Path.Combine(directory.DirectoryPath, "tools"), manager.ToolDirectory);
        Assert.Equal(Path.Combine(manager.ToolDirectory, "aria2", "1.37.0", "x64", "aria2c.exe"), result.ExecutablePath);
        Assert.Equal(Executable, await File.ReadAllBytesAsync(result.ExecutablePath!));
        Assert.Equal(1, source.OpenCount);
        Assert.Single(Directory.GetFiles(manager.ToolDirectory, "*.exe", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(manager.ToolDirectory, "*.txt", SearchOption.AllDirectories));
        AssertNoTemporaryDirectories(manager);
    }

    [Fact]
    public async Task VerifiedCache_IsRehashedAndReused_WhileManifestIsReadEveryTime()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = Manager(directory, source);
        var first = await manager.EnsureAsync(ManagedToolKind.Aria2);
        var second = await manager.EnsureAsync(ManagedToolKind.Aria2);

        Assert.True(first.IsAvailable && second.IsAvailable);
        Assert.Equal(first.ExecutablePath, second.ExecutablePath);
        Assert.Equal(2, source.PackageReadCount);
        Assert.Equal(1, source.OpenCount);
    }

    [Fact]
    public async Task BundledVersionWinsOverAnOldCachedExecutable()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = Manager(directory, source);
        var first = await manager.EnsureAsync(ManagedToolKind.Aria2);
        source.Package = source.Package with { Version = "1.38.0" };
        var next = await manager.EnsureAsync(ManagedToolKind.Aria2);

        Assert.True(next.IsAvailable);
        Assert.Equal("1.38.0", next.Version);
        Assert.NotEqual(first.ExecutablePath, next.ExecutablePath);
        Assert.True(File.Exists(first.ExecutablePath));
        Assert.Equal(2, source.OpenCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Checksums_FailWithoutPublishingExecutable(bool archiveChecksum)
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        source.Package = archiveChecksum
            ? source.Package with { ArchiveSha256 = new string('0', 64) }
            : source.Package with { ExecutableSha256 = new string('0', 64) };
        var manager = Manager(directory, source);

        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        AssertUnavailable(result, archiveChecksum ? ToolErrorCodes.ArchiveChecksumMismatch : ToolErrorCodes.ExecutableChecksumMismatch);
        Assert.Empty(Directory.GetFiles(manager.ToolDirectory, "*.exe", SearchOption.AllDirectories));
        AssertNoTemporaryDirectories(manager);
    }

    [Fact]
    public async Task ArchiveChecksum_IsCheckedBeforeZipParsing()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource { Archive = Encoding.UTF8.GetBytes("not even a zip") };
        var result = await Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2);
        AssertUnavailable(result, ToolErrorCodes.ArchiveChecksumMismatch);
    }

    [Fact]
    public async Task CorruptCache_IsReplacedFromVerifiedOfflinePackage()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = Manager(directory, source);
        var first = await manager.EnsureAsync(ManagedToolKind.Aria2);
        await File.WriteAllTextAsync(first.ExecutablePath!, "tampered");

        var repaired = await manager.EnsureAsync(ManagedToolKind.Aria2);

        Assert.True(repaired.IsAvailable);
        Assert.Equal(Executable, await File.ReadAllBytesAsync(repaired.ExecutablePath!));
        Assert.Equal(2, source.OpenCount);
        AssertNoTemporaryDirectories(manager);
    }

    [Fact]
    public async Task CorruptCache_WithBadPackage_RemainsUnavailableAndDoesNotDeleteExistingFile()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = Manager(directory, source);
        var first = await manager.EnsureAsync(ManagedToolKind.Aria2);
        await File.WriteAllTextAsync(first.ExecutablePath!, "tampered but keep for diagnosis");
        source.Archive = [1, 2, 3];

        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        AssertUnavailable(result, ToolErrorCodes.ArchiveChecksumMismatch);
        Assert.Equal("tampered but keep for diagnosis", await File.ReadAllTextAsync(first.ExecutablePath!));
        AssertNoTemporaryDirectories(manager);
    }

    [Theory]
    [InlineData("../aria2c.exe")]
    [InlineData("/aria2c.exe")]
    [InlineData("dir/../../aria2c.exe")]
    [InlineData("dir\\aria2c.exe")]
    [InlineData("C:/aria2c.exe")]
    [InlineData("dir/./aria2c.exe")]
    public async Task UnsafeManifestEntry_IsRejectedBeforeArchiveIsOpened(string entry)
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource(entry);
        var result = await Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2);

        AssertUnavailable(result, ToolErrorCodes.UnsafePath);
        Assert.Equal(0, source.OpenCount);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnselectedZipSlipEntry_IsNeverExtracted()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource(extraEntry: "../../outside-user-file.txt");
        var manager = Manager(directory, source);
        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        Assert.True(result.IsAvailable);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.txt", SearchOption.AllDirectories));
        AssertNoTemporaryDirectories(manager);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SizeLimits_RejectArchiveOrExecutableWithoutPublication(bool archiveLimit)
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var limits = archiveLimit
            ? new ManagedToolLimits { MaxArchiveBytes = source.Archive.Length - 1 }
            : new ManagedToolLimits { MaxExecutableBytes = Executable.Length - 1 };
        var manager = Manager(directory, source, limits: limits);
        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        AssertUnavailable(result, ToolErrorCodes.PackageTooLarge);
        Assert.Empty(Directory.GetFiles(manager.ToolDirectory, "*.exe", SearchOption.AllDirectories));
        AssertNoTemporaryDirectories(manager);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("symlink")]
    [InlineData("invalidzip")]
    public async Task InvalidArchive_IsUnavailable(string defect)
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        if (defect == "invalidzip") source.Archive = [1, 2, 3];
        else
        {
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (defect != "missing")
                {
                    var entry = zip.CreateEntry(source.Package.ExecutableEntry);
                    if (defect == "symlink") entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                    using (var stream = entry.Open()) stream.Write(Executable);
                    if (defect == "duplicate")
                    {
                        var duplicate = zip.CreateEntry(source.Package.ExecutableEntry);
                        using var stream = duplicate.Open();
                        stream.Write(Executable);
                    }
                }
            }
            source.Archive = data.ToArray();
        }
        source.Package = source.Package with { ArchiveSha256 = Hash(source.Archive) };

        var result = await Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2);
        AssertUnavailable(result, ToolErrorCodes.InvalidArchive);
    }

    [Fact]
    public async Task IndependentManagers_UseOnePreparationAndPublishOneVerifiedCache()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Open = _ => Task.FromResult<Stream>(new GatedReadStream(source.Archive, entered, release));
        var firstManager = Manager(directory, source);
        var secondManager = Manager(directory, source);
        var firstTask = firstManager.EnsureAsync(ManagedToolKind.Aria2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = secondManager.EnsureAsync(ManagedToolKind.Aria2);
        release.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.IsAvailable));
        Assert.Equal(results[0].ExecutablePath, results[1].ExecutablePath);
        Assert.Equal(1, source.OpenCount);
        AssertNoTemporaryDirectories(firstManager);
    }

    [Fact]
    public async Task CancellingLockWait_DoesNotRemoveOtherInstancesTemporaryFiles()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Open = _ => Task.FromResult<Stream>(new GatedReadStream(source.Archive, entered, release));
        var owner = Manager(directory, source);
        var ownerTask = owner.EnsureAsync(ManagedToolKind.Aria2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiter = Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2, cancellation.Token);
        cancellation.Cancel();
        var waitingResult = await waiter;
        var ownerTemporaryDirectorySurvived = Directory.GetDirectories(owner.ToolDirectory, ".prepare-*", SearchOption.AllDirectories).Length == 1;
        release.TrySetResult();
        var ownerResult = await ownerTask;

        AssertUnavailable(waitingResult, ToolErrorCodes.Cancelled);
        Assert.True(ownerTemporaryDirectorySurvived);
        Assert.True(ownerResult.IsAvailable);
        AssertNoTemporaryDirectories(owner);
    }

    [Fact]
    public async Task CancellationDuringArchiveCopy_CleansOnlyOwnTemporaryFilesAndKeepsUserFiles()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Open = _ => Task.FromResult<Stream>(new GatedReadStream(source.Archive, entered, release));
        var manager = Manager(directory, source);
        var userDirectory = Path.Combine(manager.ToolDirectory, "aria2", "1.37.0", "x64", ".prepare-user");
        Directory.CreateDirectory(userDirectory);
        var userFile = Path.Combine(userDirectory, "package.zip");
        await File.WriteAllTextAsync(userFile, "user-owned");
        using var cancellation = new CancellationTokenSource();
        var task = manager.EnsureAsync(ManagedToolKind.Aria2, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await task;

        AssertUnavailable(result, ToolErrorCodes.Cancelled);
        Assert.Equal("user-owned", await File.ReadAllTextAsync(userFile));
        Assert.Single(Directory.GetDirectories(manager.ToolDirectory, ".prepare-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(manager.ToolDirectory, "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingLock_TimeoutReturnsStableBusyFailure()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = Manager(directory, source, limits: new ManagedToolLimits { LockTimeout = TimeSpan.Zero });
        var packageDirectory = Path.Combine(manager.ToolDirectory, "aria2", "1.37.0", "x64");
        Directory.CreateDirectory(packageDirectory);
        using var owner = new FileStream(Path.Combine(packageDirectory, ".prepare.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        AssertUnavailable(result, ToolErrorCodes.Busy);
        Assert.Equal(0, source.OpenCount);
    }

    [Theory]
    [InlineData("x64", "x64", "bundled")]
    [InlineData("X64", "x64", "bundled")]
    [InlineData("arm64", "x86", "bundled-x86-compatibility-non-native")]
    [InlineData("x86", "x86", "bundled")]
    public async Task Architecture_SelectsOfficialPackageAndReportsCompatibility(string architecture, string expected, string origin)
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        source.Package = source.Package with { Architecture = expected };
        var result = await Manager(directory, source, architecture).EnsureAsync(ManagedToolKind.Aria2);

        Assert.True(result.IsAvailable);
        Assert.Equal(expected, source.RequestedArchitecture);
        Assert.Equal(origin, result.Origin);
        Assert.Equal(expected, Path.GetFileName(Path.GetDirectoryName(result.ExecutablePath!)));
    }

    [Fact]
    public async Task UnsupportedArchitecture_DoesNotTryAnotherExecutableOrCreateFiles()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var result = await Manager(directory, source, "arm").EnsureAsync(ManagedToolKind.Aria2);
        AssertUnavailable(result, ToolErrorCodes.UnsupportedArchitecture);
        Assert.Equal(0, source.PackageReadCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.DirectoryPath));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("architecture")]
    public async Task InvalidManifest_IsRejectedBeforeAnyCacheIsTrusted(string defect)
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        source.Package = defect switch
        {
            "version" => source.Package with { Version = "../../escape" },
            "hash" => source.Package with { ArchiveSha256 = "not-a-hash" },
            _ => source.Package with { Architecture = "x86" },
        };
        AssertUnavailable(await Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2), ToolErrorCodes.InvalidManifest);
        Assert.Equal(0, source.OpenCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.DirectoryPath));
    }

    [Fact]
    public async Task MissingBundledPackage_DoesNotConsultExistingFiles()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource { MissingPackage = true };
        await File.WriteAllTextAsync(directory.Combine("aria2c.exe"), "ambient executable");
        AssertUnavailable(await Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2), ToolErrorCodes.PackageMissing);
        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    public async Task IoFailure_DoesNotMarkReadyOrDeleteUserFile()
    {
        using var directory = new TempDirectory();
        var obstruction = directory.Combine("tools");
        await File.WriteAllTextAsync(obstruction, "user file blocks the tools directory");
        var result = await Manager(directory, new MemoryPackageSource()).EnsureAsync(ManagedToolKind.Aria2);
        Assert.False(result.IsAvailable);
        Assert.Null(result.ExecutablePath);
        Assert.Contains(result.ErrorCode, new[] { ToolErrorCodes.IoError, ToolErrorCodes.AccessDenied });
        Assert.Equal("user file blocks the tools directory", await File.ReadAllTextAsync(obstruction));
    }

    [Fact]
    public async Task PackageAccessFailure_ReturnsStableErrorAndRemovesAttemptDirectory()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource { Open = _ => throw new UnauthorizedAccessException("synthetic") };
        var manager = Manager(directory, source);
        AssertUnavailable(await manager.EnsureAsync(ManagedToolKind.Aria2), ToolErrorCodes.AccessDenied);
        AssertNoTemporaryDirectories(manager);
    }

    [Fact]
    public async Task TemporaryFileCollision_IsStableFailureAndPreservesUnownedFile()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = Manager(directory, source);
        string? unownedFile = null;
        source.Open = _ =>
        {
            var attempt = Directory.GetDirectories(manager.ToolDirectory, ".prepare-*", SearchOption.AllDirectories).Single();
            unownedFile = Path.Combine(attempt, "package.zip");
            File.WriteAllText(unownedFile, "unowned collision");
            return Task.FromResult<Stream>(new MemoryStream(source.Archive, writable: false));
        };

        var result = await manager.EnsureAsync(ManagedToolKind.Aria2);

        AssertUnavailable(result, ToolErrorCodes.IoError);
        Assert.NotNull(unownedFile);
        Assert.Equal("unowned collision", await File.ReadAllTextAsync(unownedFile!));
    }

    [Fact]
    public async Task SystemWinget_IsReusedInPlaceWithoutPreparingPackages()
    {
        using var directory = new TempDirectory();
        var systemExecutable = directory.Combine("winget.exe");
        await File.WriteAllTextAsync(systemExecutable, "synthetic system alias");
        var source = new MemoryPackageSource();
        var manager = new ManagedToolManager(directory.DirectoryPath, source, _ => Task.FromResult<string?>(systemExecutable));

        var result = await manager.EnsureAsync(ManagedToolKind.Winget);

        Assert.True(result.IsAvailable);
        Assert.Equal(systemExecutable, result.ExecutablePath);
        Assert.Equal("system", result.Origin);
        Assert.Equal(0, source.PackageReadCount);
        Assert.False(Directory.Exists(manager.ToolDirectory));
    }

    [Fact]
    public async Task MissingSystemWinget_UsesOnlyExistingAbsoluteWindowsAppsAlias()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var localAppData = directory.Combine("local");
        var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        await File.WriteAllTextAsync(alias, "synthetic alias");
        var manager = new ManagedToolManager(directory.DirectoryPath, source, _ => Task.FromResult<string?>(null),
            localAppDataDirectory: localAppData);
        var result = await manager.EnsureAsync(ManagedToolKind.Winget);

        Assert.True(result.IsAvailable);
        Assert.Equal(alias, result.ExecutablePath);
        Assert.Equal("system", result.Origin);
        Assert.False(Directory.Exists(manager.ToolDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("winget.exe")]
    public async Task MissingOrRelativeSystemWinget_IsUnavailableWithoutSearchingAmbientPaths(string? resolved)
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.Combine("winget.exe"), "untrusted ambient executable");
        var manager = new ManagedToolManager(directory.DirectoryPath, new MemoryPackageSource(), _ => Task.FromResult(resolved),
            localAppDataDirectory: directory.Combine("no-local-app-data"));
        var result = await manager.EnsureAsync(ManagedToolKind.Winget);

        AssertUnavailable(result, ToolErrorCodes.SystemToolMissing);
        Assert.Equal("system", result.Origin);
        Assert.False(Directory.Exists(manager.ToolDirectory));
    }

    [Fact]
    public async Task Xget_IsBuiltInEndpointWithNoExecutableAndNoFileCreation()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        var manager = new ManagedToolManager(directory.DirectoryPath, source,
            _ => throw new InvalidOperationException("Xget must not query system tools"));
        var result = await manager.EnsureAsync(ManagedToolKind.Xget);

        Assert.True(result.IsAvailable);
        Assert.Equal(new Uri("https://xget.xi-xu.me/"), result.Endpoint);
        Assert.Null(result.ExecutablePath);
        Assert.Equal("built-in", result.Origin);
        Assert.Equal(0, source.PackageReadCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.DirectoryPath));
    }

    [Fact]
    public async Task PreCancelledEnsure_DoesNotCreateFilesOrReadPackage()
    {
        using var directory = new TempDirectory();
        var source = new MemoryPackageSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await Manager(directory, source).EnsureAsync(ManagedToolKind.Aria2, cancellation.Token);
        AssertUnavailable(result, ToolErrorCodes.Cancelled);
        Assert.Equal(0, source.PackageReadCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.DirectoryPath));
    }

    [Fact]
    public async Task EmbeddedAdapter_MissingManifestIsOfflineMissingPackage()
    {
        var source = new BundledToolPackageSource(typeof(ManagedToolManagerTests).Assembly, "fixture.missing.manifest.json");
        Assert.Null(await source.GetPackageAsync(ManagedToolKind.Aria2, "x64"));
    }

    private static ManagedToolManager Manager(TempDirectory directory, MemoryPackageSource source,
        string architecture = "x64", ManagedToolLimits? limits = null) =>
        new(directory.DirectoryPath, source, _ => Task.FromResult<string?>(null), architecture,
            directory.Combine("nonexistent-local-app-data"), limits);

    private static void AssertUnavailable(ToolResolution result, string error)
    {
        Assert.False(result.IsAvailable);
        Assert.Null(result.ExecutablePath);
        Assert.Equal(error, result.ErrorCode);
    }

    private static void AssertNoTemporaryDirectories(ManagedToolManager manager) =>
        Assert.Empty(Directory.GetDirectories(manager.ToolDirectory, ".prepare-*", SearchOption.AllDirectories));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class MemoryPackageSource : IToolPackageSource
    {
        private int _openCount;
        private int _packageReadCount;
        public MemoryPackageSource(string executableEntry = "aria2-1.37.0-win-64bit-build1/aria2c.exe", string? extraEntry = null)
        {
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var output = zip.CreateEntry(executableEntry).Open()) output.Write(Executable);
                if (extraEntry is not null)
                {
                    using var output = zip.CreateEntry(extraEntry).Open();
                    output.Write(Encoding.UTF8.GetBytes("never extract"));
                }
            }
            Archive = data.ToArray();
            Package = new("1.37.0", "x64", "fixture.aria2.zip", Hash(Archive), executableEntry, Hash(Executable));
        }

        public BundledToolPackage Package { get; set; }
        public byte[] Archive { get; set; }
        public bool MissingPackage { get; init; }
        public string? RequestedArchitecture { get; private set; }
        public Func<CancellationToken, Task<Stream>>? Open { get; set; }
        public int OpenCount => Volatile.Read(ref _openCount);
        public int PackageReadCount => Volatile.Read(ref _packageReadCount);

        public Task<BundledToolPackage?> GetPackageAsync(ManagedToolKind kind, string architecture, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _packageReadCount);
            RequestedArchitecture = architecture;
            return Task.FromResult(MissingPackage ? null : Package);
        }

        public Task<Stream> OpenArchiveAsync(BundledToolPackage package, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            return Open?.Invoke(ct) ?? Task.FromResult<Stream>(new MemoryStream(Archive, writable: false));
        }
    }

    private sealed class GatedReadStream : MemoryStream
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;
        public GatedReadStream(byte[] data, TaskCompletionSource entered, TaskCompletionSource release) : base(data, writable: false)
        {
            _entered = entered;
            _release = release;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
