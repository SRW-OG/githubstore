using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GitHubStore.App.Services.Download;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class PackageCacheTests
{
    private static readonly Uri Source = new("https://example.invalid/releases/v1/setup.exe");
    private const string Name = "setup.exe";

    [Fact]
    public async Task ReceiptAllowsReuseWithoutFetchingAndLeaseDisposalPreservesPackage()
    {
        using var temp = new TempDirectory();
        var service = new PackageCacheService(temp.DirectoryPath);
        string path;
        await using (var lease = await service.AcquireAsync())
        {
            path = await WritePackageAsync(lease.Cache, "installer");
            await lease.Cache.RecordCompletedAsync(Source, Name, 9, null);
        }
        var fetches = 0;
        await using (var lease = await service.AcquireAsync())
        {
            if (await lease.Cache.FindVerifiedAsync(Source, Name, 9, null) is null) fetches++;
            Assert.NotNull(await lease.Cache.FindVerifiedAsync(Source, Name, null, null));
        }
        Assert.Equal(0, fetches);
        Assert.Equal("installer", File.ReadAllText(path));
    }

    [Fact]
    public async Task UnreceiptedSizeOnlyFileIsNeverAHitButPublisherHashCanVerifyIt()
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var path = await WritePackageAsync(lease.Cache, "original");
        Assert.Null(await lease.Cache.FindVerifiedAsync(Source, Name, 8, null));
        Assert.NotNull(await lease.Cache.FindVerifiedAsync(Source, Name, 8, "sha256:" + Digest("original")));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ChangedSizeHashAndSameSizeCorruptionMissWithoutDeletingBytes()
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var cache = lease.Cache;
        var path = await WritePackageAsync(cache, "original");
        await cache.RecordCompletedAsync(Source, Name, 8, Digest("original"));
        Assert.Null(await cache.FindVerifiedAsync(Source, Name, 9, null));
        Assert.Null(await cache.FindVerifiedAsync(Source, Name, 8, Digest("changed!")));
        File.WriteAllText(path, "tampered");
        Assert.Null(await cache.FindVerifiedAsync(Source, Name, 8, null));
        Assert.Null(await cache.FindVerifiedAsync(Source, Name, null, null));
        Assert.Equal("tampered", File.ReadAllText(path));
    }

    [Fact]
    public async Task ReceiptMustMatchSourceFilenameAndSize()
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var cache = lease.Cache;
        var path = await WritePackageAsync(cache, "original");
        foreach (var field in new[] { "Source", "FileName", "Size" })
        {
            await cache.RecordCompletedAsync(Source, Name, null, null);
            var receipt = Directory.GetFiles(Path.GetDirectoryName(path)!, ".cache-receipt-*.json").Single();
            var json = JsonNode.Parse(File.ReadAllText(receipt))!;
            if (field == "Size") json[field] = 99;
            else json[field] = "different";
            File.WriteAllText(receipt, json.ToJsonString());
            Assert.Null(await cache.FindVerifiedAsync(Source, Name, 8, null));
        }
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("sha256:bad")]
    [InlineData("md5:00000000000000000000000000000000")]
    [InlineData(" ")]
    public async Task InvalidNonemptyDigestRejectsEvenWithValidLocalReceipt(string invalid)
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var path = await WritePackageAsync(lease.Cache, "original");
        await lease.Cache.RecordCompletedAsync(Source, Name, 8, null);
        Assert.Null(await lease.Cache.FindVerifiedAsync(Source, Name, 8, invalid));
        await Assert.ThrowsAsync<ArgumentException>(() => lease.Cache.RecordCompletedAsync(Source, Name, 8, invalid));
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public async Task RecordIndependentlyChecksBytesAndMetadataFailureDoesNotDeletePackage()
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var cache = lease.Cache;
        var path = await WritePackageAsync(cache, "original");
        await Assert.ThrowsAsync<IOException>(() => cache.RecordCompletedAsync(Source, Name, 9, null));
        await Assert.ThrowsAsync<IOException>(() => cache.RecordCompletedAsync(Source, Name, 8, Digest("changed!")));
        await cache.RecordCompletedAsync(Source, Name, 8, null);
        var receipt = Directory.GetFiles(Path.GetDirectoryName(path)!, ".cache-receipt-*.json").Single();
        using (new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => cache.RecordCompletedAsync(Source, Name, 8, null));
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public async Task DifferentUrlsWithSameFilenameUseSeparateFullKeySlots()
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var first = await WritePackageAsync(lease.Cache, "original");
        await lease.Cache.RecordCompletedAsync(Source, Name, 8, null);
        var other = new Uri("https://example.invalid/releases/v2/setup.exe");
        var second = lease.Cache.TryResolvePath(other, Name);
        Assert.NotEqual(first, second);
        Assert.Equal(64, PackageCacheService.KeyFor(Source).Length);
        Assert.Null(await lease.Cache.FindVerifiedAsync(other, Name, 8, null));
    }

    [Fact]
    public async Task RepeatedPrepareRetainsEveryInvalidPackageAndResumeState()
    {
        using var temp = new TempDirectory();
        await using var lease = await new PackageCacheService(temp.DirectoryPath).AcquireAsync();
        var cache = lease.Cache;
        var path = await WritePackageAsync(cache, "bad-first");
        await cache.RecordCompletedAsync(Source, Name, null, null);
        File.WriteAllText(path + ".part", "resume");
        await cache.PrepareDownloadAsync(Source, Name);
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "bad-second");
        await cache.PrepareDownloadAsync(Source, Name);
        await cache.PrepareDownloadAsync(Source, Name);
        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, ".retained-invalid-*.package");
        Assert.Equal(2, backups.Length);
        Assert.Contains(backups, p => File.ReadAllText(p) == "bad-first");
        Assert.Contains(backups, p => File.ReadAllText(p) == "bad-second");
        Assert.Equal("resume", File.ReadAllText(path + ".part"));
        var summary = await cache.SummarizeAsync();
        Assert.Equal(3, summary.FileCount);
        Assert.Equal(25L, summary.TotalBytes);
    }

    [Fact]
    public async Task ExplicitClearDeletesOnlyOwnedContentsAndSummaryExcludesMetadata()
    {
        using var temp = new TempDirectory();
        var service = new PackageCacheService(temp.DirectoryPath);
        var foreign = temp.Combine("foreign-installer.exe");
        File.WriteAllText(foreign, "leave me");
        var oldSlot = Directory.CreateDirectory(temp.Combine("0123456789ABCDEF")).FullName;
        File.WriteAllText(Path.Combine(oldSlot, Name), "legacy");
        string path;
        await using (var lease = await service.AcquireAsync())
        {
            path = await WritePackageAsync(lease.Cache, "original");
            await lease.Cache.RecordCompletedAsync(Source, Name, 8, null);
            var summary = await lease.Cache.SummarizeAsync();
            Assert.Equal(1, summary.FileCount);
            Assert.Equal(8L, summary.TotalBytes);
        }
        var result = await service.ClearAsync();
        Assert.False(result.Busy);
        Assert.Equal(2, result.DeletedFiles); // Package and receipt.
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, result.RemainingFiles);
        Assert.True(result.FreedBytes > 8);
        Assert.False(File.Exists(path));
        Assert.Equal("leave me", File.ReadAllText(foreign));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(oldSlot, Name)));
        Assert.True(Directory.Exists(temp.DirectoryPath));
        Assert.Equal(0, (await service.SummarizeAsync()).FileCount);
        Assert.Equal(0, (await service.ClearAsync()).DeletedFiles);
        await using var next = await service.AcquireAsync(); // Persistent marker and lock remain valid.
    }

    [Fact]
    public async Task ActiveLeaseProtectsAcrossInstancesAndSnapshotsRoot()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("first");
        var service = new PackageCacheService(() => root);
        var lease = await service.AcquireAsync();
        try
        {
            var path = await WritePackageAsync(lease.Cache, "original");
            root = temp.Combine("second");
            Assert.Equal(temp.Combine("first"), lease.Cache.Root);
            var other = new PackageCacheService(temp.Combine("first") + Path.DirectorySeparatorChar);
            Assert.True((await other.ClearAsync()).Busy);
            using var cancel = new CancellationTokenSource();
            var pending = other.AcquireAsync(cancel.Token);
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { await lease.DisposeAsync(); }
        await lease.DisposeAsync(); // Idempotent; must not release the gate twice.
        Assert.False((await new PackageCacheService(temp.Combine("first")).ClearAsync()).Busy);
    }

    [Fact]
    public async Task PersistentFileLockAlsoMakesClearBusy()
    {
        using var temp = new TempDirectory();
        var service = new PackageCacheService(temp.DirectoryPath);
        await using (var lease = await service.AcquireAsync()) { }
        var lockPath = Path.Combine(temp.DirectoryPath, "packages-v1", ".cache-lease");
        using var external = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True((await new PackageCacheService(temp.DirectoryPath).ClearAsync()).Busy);
    }

    [Fact]
    public async Task LockedPackageReportsSkippedAndRemainingInsteadOfClaimingSuccess()
    {
        using var temp = new TempDirectory();
        var service = new PackageCacheService(temp.DirectoryPath);
        string path;
        await using (var lease = await service.AcquireAsync()) path = await WritePackageAsync(lease.Cache, "locked");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.ClearAsync();
            Assert.False(result.Busy);
            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.Equal(1, result.RemainingFiles);
            Assert.Equal(0L, result.FreedBytes);
        }
        Assert.Equal("locked", File.ReadAllText(path));
        var cleared = await service.ClearAsync();
        Assert.Equal(1, cleared.DeletedFiles);
        Assert.Equal(6L, cleared.FreedBytes);
    }

    [Fact]
    public async Task ClearingAnEmptyUnmarkedNamespaceDoesNotPoisonLaterAcquisition()
    {
        using var temp = new TempDirectory();
        var owned = Directory.CreateDirectory(temp.Combine("packages-v1")).FullName;
        var cache = new PackageCacheService(temp.DirectoryPath);
        Assert.Equal(0, (await cache.ClearAsync()).DeletedFiles);
        Assert.Empty(Directory.GetFileSystemEntries(owned));
        await using var lease = await cache.AcquireAsync();
        Assert.NotNull(lease.Cache.TryResolvePath(Source, Name));
    }

    [Fact]
    public async Task UnmarkedNonemptyNamespaceAndInvalidMarkerAreRefused()
    {
        using var temp = new TempDirectory();
        var owned = Directory.CreateDirectory(temp.Combine("packages-v1")).FullName;
        var foreign = Path.Combine(owned, "foreign.txt");
        File.WriteAllText(foreign, "preserve");
        var service = new PackageCacheService(temp.DirectoryPath);
        Assert.Null(service.TryResolvePath(Source, Name));
        await Assert.ThrowsAsync<IOException>(() => service.AcquireAsync());
        await Assert.ThrowsAsync<IOException>(() => service.ClearAsync());
        await Assert.ThrowsAsync<IOException>(() => service.SummarizeAsync());
        File.WriteAllText(Path.Combine(owned, ".cache-owner"), "fake");
        await Assert.ThrowsAsync<IOException>(() => service.ClearAsync());
        Assert.Equal("preserve", File.ReadAllText(foreign));
    }

    [Theory]
    [InlineData("../setup.exe")]
    [InlineData("setup.exe:stream")]
    [InlineData("CON.exe")]
    [InlineData("LPT1.zip")]
    [InlineData("NUL")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(".cache-owner")]
    public void UnsafeFilenamesAreRejected(string name)
    {
        using var temp = new TempDirectory();
        Assert.False(PackageCacheService.IsSafeFileName(name));
        Assert.Null(new PackageCacheService(temp.DirectoryPath).TryResolvePath(Source, name));
    }

    [Fact]
    public void UnsafeRootsAreRejected()
    {
        using var temp = new TempDirectory();
        foreach (var root in new[] { Path.GetPathRoot(temp.DirectoryPath)!, @"\\server\share\cache", temp.Combine(".."), temp.Combine("cache:stream"), temp.Combine("CON") })
            Assert.Null(new PackageCacheService(root).TryResolvePath(Source, Name));
    }

    [Fact]
    public async Task AncestorJunctionIsRefusedAndSubtreeJunctionIsSkipped()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var target = Directory.CreateDirectory(temp.Combine("real")).FullName;
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "untouched");
        var link = temp.Combine("ancestor-link");
        CreateJunction(link, target);
        var service = new PackageCacheService(temp.Combine("cache"));
        try
        {
            var unsafeService = new PackageCacheService(Path.Combine(link, "cache"));
            Assert.Null(unsafeService.TryResolvePath(Source, Name));
            await Assert.ThrowsAsync<IOException>(() => unsafeService.AcquireAsync());
            await Assert.ThrowsAsync<IOException>(() => unsafeService.ClearAsync());
            await using (var lease = await service.AcquireAsync()) { }
            var subtree = Path.Combine(service.Root, "packages-v1", "linked-slot");
            CreateJunction(subtree, target);
            try
            {
                var result = await service.ClearAsync();
                Assert.Equal(0, result.DeletedFiles);
                Assert.Equal(1, result.SkippedFiles);
                Assert.Equal(1, result.RemainingFiles);
                await Assert.ThrowsAsync<IOException>(() => service.SummarizeAsync());
                Assert.Equal("untouched", File.ReadAllText(sentinel));
            }
            finally { Directory.Delete(subtree, false); }
        }
        finally { Directory.Delete(link, false); }
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    private static async Task<string> WritePackageAsync(PackageCacheService cache, string content)
    {
        await cache.PrepareDownloadAsync(Source, Name);
        var path = cache.TryResolvePath(Source, Name)!;
        File.WriteAllText(path, content);
        return path;
    }
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot create test junction.");
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }
}
