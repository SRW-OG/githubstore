using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using GitHubStore.App.Services.Installation;
using GitHubStore.App.Services.Launcher;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class AppInstallServiceTests
{
    [Fact]
    public async Task RealZipExtractsBytesAndRegistersBoundCompletedReceipt()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("bin/Tool.exe", Pe()), ("readme.txt", new byte[] { 1, 2, 3 }));
        var request = f.Request();
        var asset = request.Asset with { Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(f.Download.Bytes)), Size = f.Download.Bytes.Length };
        request = Bind(request, asset);
        var progress = new List<AppInstallPhase>();
        var result = await f.Service().InstallAsync(request, new Sink<AppInstallProgress>(p => progress.Add(p.Phase)));
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(Pe(), await File.ReadAllBytesAsync(result.ExecutablePath!));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(result.TargetDirectory!, "readme.txt")));
        var receipt = Assert.Single(f.Launcher.Receipts);
        Assert.Equal(Origin.Store, receipt.Origin);
        Assert.Equal(InstallState.Done, receipt.State);
        Assert.Equal(request.App.Id, receipt.AppId);
        Assert.Equal(request.App.Id.SourceId, receipt.Source.SourceId);
        Assert.Equal(request.App.Id.PackageId, receipt.Source.PackageId);
        Assert.Equal(request.App.SourceRevision, receipt.Source.SourceRevision);
        Assert.Equal(request.Asset.InstallerId, receipt.Source.InstallerId);
        Assert.Equal(request.Asset.DownloadUrl, receipt.Source.ArtifactUrl!.AbsoluteUri);
        Assert.Equal(result.ExecutablePath, receipt.ExePath);
        Assert.Equal(asset.Digest, f.Download.Last!.ExpectedSha256);
        Assert.Equal(asset.Size, f.Download.Last.ExpectedSize);
        Assert.Equal(AppInstallPhase.Completed, progress.Last());
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-stage-*"));
        // 安装后必须保留已校验的安装包，供再次安装复用；不再自动删除。
        Assert.True(File.Exists(f.Download.Last.TargetPath));

        // 第二次安装同一资产：校验命中缓存后直接复用，不再联网下载。
        // 首次安装已占用目标目录，重新安装前必须先移除它（覆盖已有安装被明确拒绝）。
        f.Launcher.Items.Clear();
        f.Launcher.Receipts.Clear();
        Directory.Delete(result.TargetDirectory!, true);
        var downloads = f.Download.Calls;
        var reused = new List<AppInstallPhase>();
        var again = await f.Service().InstallAsync(Bind(f.Request(), asset),
            new Sink<AppInstallProgress>(p => reused.Add(p.Phase)));
        Assert.True(again.Succeeded, again.ErrorCode);
        Assert.Equal(downloads, f.Download.Calls);
        Assert.Contains(AppInstallPhase.UsingCache, reused);
        Assert.DoesNotContain(AppInstallPhase.Downloading, reused);
    }

    [Fact]
    public async Task ReceiptIsAcceptedByRealManagedLauncher()
    {
        using var f = new Fixture();
        var launcher = new ManagedAppLauncher(f.Data, f.Root, (_, _) => throw new Exception("must not launch"));
        var service = new AppInstallService(f.Download, launcher, f.Preferences, f.Data, installRootProvider: () => f.Root);
        var result = await service.InstallAsync(f.Request());
        Assert.True(result.Succeeded, result.ErrorCode);
        var item = Assert.Single(await launcher.ListAsync());
        Assert.True(item.IsAvailable);
        Assert.Equal(result.ExecutablePath, item.ExecutablePath);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:/absolute.exe")]
    [InlineData("dir/Tool.exe:stream")]
    [InlineData("dir/NUL.exe")]
    [InlineData("dir/trailing./Tool.exe")]
    [InlineData("dir /Tool.exe")]
    [InlineData("dir//Tool.exe")]
    [InlineData("..\\escape.exe")]
    public async Task UnsafeZipNamesNeverPublishOrRegister(string name)
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip((name, Pe()));
        var result = await f.Service().InstallAsync(f.Request());
        Assert.Equal("install_unsafe_archive", result.ErrorCode);
        Assert.Empty(f.Launcher.Receipts);
        Assert.False(Directory.Exists(f.Target));
        Assert.False(File.Exists(Path.Combine(f.Root, "escape.exe")));
    }

    [Fact]
    public async Task ZipSymlinkIsRejectedWithoutFollowingIt()
    {
        using var f = new Fixture();
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("link.exe");
            entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var output = entry.Open();
            output.Write(Pe());
        }
        f.Download.Bytes = memory.ToArray();
        Assert.Equal("install_unsafe_archive", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.Empty(f.Launcher.Receipts);
    }

    [Fact]
    public async Task CaseInsensitiveDuplicateZipEntriesAreRejected()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("Tool.exe", Pe()), ("TOOL.EXE", Pe()));
        Assert.Equal("install_unsafe_archive", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.False(Directory.Exists(f.Target));
    }

    [Fact]
    public async Task ZipEntryCountBombIsRejectedBeforeExtraction()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(Enumerable.Range(0, 10001).Select(i => ($"{i}.txt", Array.Empty<byte>())).ToArray());
        Assert.Equal("install_archive_limit", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.Empty(f.Launcher.Receipts);
    }

    [Fact]
    public async Task DeclaredExpandedSizeBombIsRejectedBeforeAllocatingOrExtracting()
    {
        using var f = new Fixture();
        var bytes = Zip(("Tool.exe", Pe()));
        // ZIP central directory uncompressed length: claim 0x80000001 bytes with a tiny real payload.
        var signature = new byte[] { 0x50, 0x4b, 0x01, 0x02 };
        var index = Enumerable.Range(0, bytes.Length - 4).Single(i => bytes.AsSpan(i, 4).SequenceEqual(signature));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24, 4), 0x80000001);
        f.Download.Bytes = bytes;
        Assert.Equal("install_archive_limit", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.False(Directory.Exists(f.Target));
    }

    [Fact]
    public async Task AmbiguousExecutablesRequireExactRelativeCallbackSelection()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("bin/Alpha.exe", Pe()), ("Beta.exe", Pe()), ("setup.exe", Pe()), ("unins000.exe", Pe()));
        IReadOnlyList<string>? offered = null;
        var result = await f.Service().InstallAsync(f.Request(), chooseExecutable: (paths, _) =>
        {
            offered = paths;
            Assert.DoesNotContain("setup.exe", paths);
            Assert.DoesNotContain("unins000.exe", paths);
            Assert.All(paths, p => Assert.False(Path.IsPathFullyQualified(p)));
            return Task.FromResult<string?>(Path.Combine("bin", "Alpha.exe"));
        });
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(2, offered!.Count);
        Assert.EndsWith(Path.Combine("bin", "Alpha.exe"), result.ExecutablePath);
    }

    [Theory]
    [InlineData("../elsewhere.exe", "install_invalid_executable_choice")]
    [InlineData("alpha.exe", "install_invalid_executable_choice")]
    [InlineData(null, "install_cancelled")]
    public async Task CallbackCannotInventOrNormalizeASelection(string? selected, string code)
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("Alpha.exe", Pe()), ("Beta.exe", Pe()));
        var result = await f.Service().InstallAsync(f.Request(), chooseExecutable: (_, _) => Task.FromResult(selected));
        Assert.Equal(code, result.ErrorCode);
        Assert.False(Directory.Exists(f.Target));
        Assert.Empty(f.Launcher.Receipts);
    }

    [Fact]
    public async Task StrongProductBasenameSelectsWithoutCallback()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("Tool.exe", Pe()), ("Other.exe", Pe()));
        var result = await f.Service().InstallAsync(f.Request(), chooseExecutable: (_, _) => throw new Exception("unexpected chooser"));
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.EndsWith("Tool.exe", result.ExecutablePath);
    }

    [Fact]
    public async Task AmbiguousArchiveWithoutChooserHasStableError()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("Alpha.exe", Pe()), ("Beta.exe", Pe()));
        Assert.Equal("install_executable_choice_required", (await f.Service().InstallAsync(f.Request())).ErrorCode);
    }

    [Fact]
    public async Task TextNamedExeNeverCreatesReceipt()
    {
        using var f = new Fixture();
        f.Download.Bytes = Zip(("Tool.exe", new byte[] { 1, 2, 3 }));
        Assert.Equal("install_executable_missing", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.Empty(f.Launcher.Receipts);
    }

    [Theory]
    [InlineData("checksum", "install_checksum_mismatch")]
    [InlineData("size", "install_length_mismatch")]
    [InlineData("digest", "install_invalid_digest")]
    public async Task IndependentlyChecksFakeDownloaderIntegrity(string fault, string code)
    {
        using var f = new Fixture();
        var request = f.Request();
        request = Bind(request, fault switch
        {
            "checksum" => request.Asset with { Digest = new string('0', 64) },
            "size" => request.Asset with { Size = f.Download.Bytes.Length + 1 },
            _ => request.Asset with { Digest = "sha512:arbitrary" },
        });
        Assert.Equal(code, (await f.Service().InstallAsync(request)).ErrorCode);
        Assert.Empty(f.Launcher.Receipts);
        Assert.False(Directory.Exists(f.Target));
    }

    [Fact]
    public async Task DownloadCancellationCleansOwnedCacheAndDoesNotPublish()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Download.AfterWrite = () => cts.Cancel();
        var result = await f.Service().InstallAsync(f.Request(), ct: cts.Token);
        Assert.Equal("install_cancelled", result.ErrorCode);
        Assert.False(result.FilesMayRemain);
        // 取消不会发布安装目标；已下载的安装包则保留在缓存中，供下次复用。
        Assert.True(File.Exists(f.Download.Last!.TargetPath));
        Assert.False(Directory.Exists(f.Target));
    }

    [Fact]
    public async Task DownloaderRemoteErrorsAreNeverExposed()
    {
        using var f = new Fixture();
        f.Download.Error = "https://private.example/?secret=credentials";
        var result = await f.Service().InstallAsync(f.Request());
        Assert.Equal("install_download_failed", result.ErrorCode);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("download_failed", result.Diagnostic.DownloadErrorCode);
        Assert.DoesNotContain("credentials", System.Text.Json.JsonSerializer.Serialize(result.Diagnostic));
    }

    [Fact]
    public async Task DownloadDiagnosticPreservesKnownErrorAndPhase()
    {
        using var f = new Fixture();
        f.Download.Error = DownloadErrorCodes.NetworkError;
        var result = await f.Service().InstallAsync(f.Request());
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(AppInstallPhase.Downloading, result.Diagnostic.Phase);
        Assert.Equal(DownloadErrorCodes.NetworkError, result.Diagnostic.DownloadErrorCode);
        Assert.False(result.Diagnostic.ExternalInstallerStarted);
        Assert.DoesNotContain(f.Root, System.Text.Json.JsonSerializer.Serialize(result.Diagnostic));
    }

    [Fact]
    public async Task ExistingTargetIsNeverOverwrittenOrRemoved()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Target);
        var sentinel = Path.Combine(f.Target, "mine.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        Assert.Equal("install_target_exists", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task AlreadyRegisteredAppDoesNotDownloadEvenWithDifferentTarget()
    {
        using var f = new Fixture();
        f.Launcher.Items.Add(new LauncherApp
        {
            InstallId = "existing", AppId = f.App.Id, DisplayName = "Old", InstallDirectory = Path.Combine(f.Root, "Old"), ExecutablePath = "old.exe",
        });
        Assert.Equal("install_already_installed", (await f.Service().InstallAsync(f.Request())).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task RegistrationFailureRetainsOwnedPortableTargetAndReportsIt()
    {
        using var f = new Fixture();
        f.Launcher.Reject = true;
        var result = await f.Service().InstallAsync(f.Request());
        Assert.Equal("install_registration_failed", result.ErrorCode);
        Assert.True(result.FilesMayRemain);
        Assert.True(File.Exists(Path.Combine(f.Target, "Tool.exe")));
        Assert.Empty(f.Launcher.Receipts);
    }

    [Theory]
    [InlineData(".exe", 0)]
    [InlineData(".exe", 3010)]
    [InlineData(".msi", 0)]
    [InlineData(".msi", 3010)]
    public async Task ExternalInstallersUseInteractiveArgumentsAndVerifyTarget(string extension, int exitCode)
    {
        using var f = new Fixture();
        f.Download.Bytes = Pe();
        var request = f.Request("Tool" + extension);
        InstallerProcessRequest? observed = null;
        var service = f.Service(async (process, ct) =>
        {
            observed = process;
            Assert.True(File.Exists(extension == ".msi" ? process.Arguments[1] : process.FileName));
            Assert.Empty(f.Launcher.Receipts);
            Directory.CreateDirectory(f.Target);
            await File.WriteAllBytesAsync(Path.Combine(f.Target, "Tool.exe"), Pe(), ct);
            return new(exitCode);
        });
        var result = await service.InstallAsync(request);
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(exitCode == 3010, result.RequiresRestart);
        Assert.True(observed!.UseShellExecute);
        Assert.Equal(Path.GetDirectoryName(f.Download.Last!.TargetPath), observed.WorkingDirectory);
        if (extension == ".msi")
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), observed.FileName);
            Assert.Equal(new[] { "/i", f.Download.Last.TargetPath, "/norestart" }, observed.Arguments);
        }
        else
        {
            Assert.Equal(f.Download.Last.TargetPath, observed.FileName);
            Assert.Empty(observed.Arguments);
        }
        Assert.Single(f.Launcher.Receipts);
    }

    [Theory]
    [InlineData(1, "install_installer_failed")]
    [InlineData(1603, "install_installer_failed")]
    [InlineData(1602, "install_installer_cancelled")]
    [InlineData(1223, "install_installer_cancelled")]
    [InlineData(0, "install_target_not_honored")]
    public async Task InstallerFailureOrIgnoredTargetNeverRegisters(int exitCode, string code)
    {
        using var f = new Fixture();
        f.Download.Bytes = Pe();
        var result = await f.Service((_, _) => Task.FromResult(new InstallerProcessResult(exitCode))).InstallAsync(f.Request("Tool.exe"));
        Assert.Equal(code, result.ErrorCode);
        Assert.True(result.FilesMayRemain);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(exitCode, result.Diagnostic.InstallerExitCode);
        Assert.Equal(exitCode == 0 ? AppInstallPhase.Verifying : AppInstallPhase.RunningInstaller, result.Diagnostic.Phase);
        Assert.True(result.Diagnostic.ExternalInstallerStarted);
        Assert.True(result.Diagnostic.ExternalInstallerFinished);
        Assert.True(result.Diagnostic.FilesMayRemain);
        Assert.Equal("Tool.exe", result.Diagnostic.AssetName);
        Assert.Empty(f.Launcher.Receipts);
    }

    [Fact]
    public async Task CancellingExternalWaitRetainsPackageAndTargetWithoutClaimingRollback()
    {
        using var f = new Fixture();
        f.Download.Bytes = Pe();
        using var cts = new CancellationTokenSource();
        var service = f.Service((_, ct) =>
        {
            Directory.CreateDirectory(f.Target);
            File.WriteAllBytes(Path.Combine(f.Target, "Tool.exe"), Pe());
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new InstallerProcessResult(0));
        });
        var result = await service.InstallAsync(f.Request("Tool.exe"), ct: cts.Token);
        Assert.Equal("install_installer_cancelled", result.ErrorCode);
        Assert.True(result.FilesMayRemain);
        Assert.True(File.Exists(f.Download.Last!.TargetPath));
        Assert.True(File.Exists(Path.Combine(f.Target, "Tool.exe")));
        Assert.Empty(f.Launcher.Receipts);
    }

    [Fact]
    public async Task PortableExeIsCopiedWithoutInvokingRunner()
    {
        using var f = new Fixture();
        f.Download.Bytes = Pe();
        var result = await f.Service((_, _) => throw new Exception("must not execute portable artifact"))
            .InstallAsync(f.Request("Tool-portable.exe"));
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(Pe(), await File.ReadAllBytesAsync(result.ExecutablePath!));
    }

    [Fact]
    public async Task SevenZipIsExplicitlyUnsupportedBeforeDownloadOrExecution()
    {
        using var f = new Fixture();
        var result = await f.Service((_, _) => throw new Exception("must not execute"))
            .InstallAsync(f.Request("Tool.7z"));
        Assert.Equal("install_unsupported_archive", result.ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Empty(f.Launcher.Receipts);
    }

    [Theory]
    [InlineData("Tool-linux.zip")]
    [InlineData("Tool-macos.zip")]
    [InlineData("Tool-symbols.zip")]
    [InlineData("Tool-x64-arm64.zip")]
    public async Task IncompatibleAndAuxiliaryAssetsAreRejected(string name)
    {
        using var f = new Fixture();
        Assert.Equal("install_incompatible_asset", (await f.Service().InstallAsync(f.Request(name))).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task ForeignAssetAndInsecureSourceAreRejectedBeforeDownload()
    {
        using var f = new Fixture();
        var request = f.Request();
        Assert.Equal("install_invalid_request", (await f.Service().InstallAsync(request with { Asset = request.Asset with { InstallerId = "foreign" } })).ErrorCode);
        Assert.Equal("install_invalid_request", (await f.Service().InstallAsync(Bind(request, request.Asset with { DownloadUrl = "http://example.com/Tool.zip" }))).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public void SanitizedTargetIsImmediateChildAndAtMostEightyCharacters()
    {
        using var f = new Fixture();
        var service = f.Service();
        var target = service.GetTargetDirectory(f.App with { DisplayName = "../CON:unsafe?" + new string('a', 100) });
        Assert.Equal(f.Root, Path.GetDirectoryName(target));
        Assert.InRange(Path.GetFileName(target).Length, 1, 80);
        Assert.DoesNotContain(':', Path.GetFileName(target));
    }

    [Fact]
    public async Task PreferenceSnapshotControlsDownloaderCacheAndDefaultRoot()
    {
        using var f = new Fixture();
        var cache = Path.Combine(f.Data, "cache-choice");
        await f.Preferences.SaveAsync(f.Preferences.Current with
        {
            InstallRootDirectory = f.Root, DownloadCacheDirectory = cache,
            DownloadEngine = DownloadEngine.Aria2, UseAccelerator = true,
        });
        f.Download.BeforeWrite = () => f.Preferences.SaveAsync(f.Preferences.Current with
        {
            InstallRootDirectory = Path.Combine(f.Data, "other-root"), DownloadEngine = DownloadEngine.Http, UseAccelerator = false,
        });
        var service = new AppInstallService(f.Download, f.Launcher, f.Preferences, f.Data,
            (_, _) => throw new Exception("must not execute ZIP"));
        var result = await service.InstallAsync(f.Request());
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(f.Target, result.TargetDirectory);
        Assert.Equal(DownloadEngine.Aria2, f.Download.Last!.Engine);
        Assert.True(f.Download.Last.UseAccelerator);
        Assert.StartsWith(cache + Path.DirectorySeparatorChar, f.Download.Last.TargetPath);
    }

    [Theory]
    [InlineData(@"\\\\server\\share\\apps")]
    [InlineData(@"C:\\apps\\..\\escape")]
    [InlineData(@"C:\\apps\\name:stream")]
    public async Task UnsafeRootsAreRejectedBeforeDownload(string root)
    {
        using var f = new Fixture();
        var service = new AppInstallService(f.Download, f.Launcher, f.Preferences, f.Data, installRootProvider: () => root);
        Assert.Equal("install_unsafe_path", (await service.InstallAsync(f.Request())).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task GlobalGateReturnsBusyWithoutSecondDownload()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Download.BeforeWrite = async () => { entered.SetResult(); await release.Task; };
        var first = f.Service().InstallAsync(f.Request());
        await entered.Task;
        try
        {
            using var other = new Fixture();
            Assert.Equal("install_busy", (await other.Service().InstallAsync(other.Request())).ErrorCode);
            Assert.Null(other.Download.Last);
        }
        finally { release.SetResult(); }
        Assert.True((await first).Succeeded);
    }

    private static AppInstallRequest Bind(AppInstallRequest request, Asset asset) => request with
    { Asset = asset, Release = request.Release with { Assets = new[] { asset } } };
    private static byte[] Pe()
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 2);
        return bytes;
    }
    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(file.Bytes);
            }
        return memory.ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "GitHubStore-install-tests-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(_base, "apps");
        public string Data => Path.Combine(_base, "data");
        public string Target => Path.Combine(Root, "Tool");
        public FakeDownload Download { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public PreferencesService Preferences { get; }
        public GitHubStore.Core.Abstractions.App App { get; } = new()
        {
            Id = new(new(Guid.NewGuid()), new("owner/tool")), SourceRevision = 3,
            DisplayName = "Tool", Owner = "owner", Repo = "tool",
        };
        public Fixture() { Directory.CreateDirectory(_base); Preferences = new(Path.Combine(Data, "preferences.json")); }
        public AppInstallService Service(Func<InstallerProcessRequest, CancellationToken, Task<InstallerProcessResult>>? runner = null) =>
            new(Download, Launcher, Preferences, Data, runner ?? ((_, _) => throw new Exception("unexpected external execution")), () => Root);
        public AppInstallRequest Request(string name = "Tool.zip")
        {
            var asset = new Asset { InstallerId = "asset-1", ReleaseKey = "release-1", Name = name, DownloadUrl = "https://example.com/" + name };
            return new() { App = App, Asset = asset, Release = new() { AppId = App.Id, Key = "release-1", TagName = "v1.0", Assets = new[] { asset } } };
        }
        public void Dispose() { if (Directory.Exists(_base)) Directory.Delete(_base, true); }
    }
    private sealed class FakeDownload : IDownloadService
    {
        public byte[] Bytes { get; set; } = Zip(("Tool.exe", Pe()));
        public string? Error { get; set; }
        public int Calls { get; private set; }
        public DownloadRequest? Last { get; private set; }
        public Action? AfterWrite { get; set; }
        public Func<Task>? BeforeWrite { get; set; }
        public IReadOnlyList<DownloadEngine> AvailableEngines => new[] { DownloadEngine.Http };
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);
        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Last = request;
            Calls++;
            if (BeforeWrite is not null) await BeforeWrite();
            if (Error is not null) return DownloadResult.Failure(Error);
            await File.WriteAllBytesAsync(request.TargetPath, Bytes, ct);
            AfterWrite?.Invoke();
            return DownloadResult.Success(request.Source, request.TargetPath, Bytes.Length, request.Engine);
        }
    }
    private sealed class FakeLauncher : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public List<InstallRecord> Receipts { get; } = new();
        public List<LauncherApp> Items { get; } = new();
        public bool Reject { get; set; }
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherApp>>(Items);
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Reject) throw new IOException("arbitrary private message");
            Assert.True(File.Exists(installation.ExePath));
            Receipts.Add(installation);
            return Task.CompletedTask;
        }
        public Task UnregisterAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Sink<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
