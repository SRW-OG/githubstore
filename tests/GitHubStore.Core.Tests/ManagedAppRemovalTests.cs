using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 移除本程序自建安装。这组测试决定用户的文件会不会被删掉，因此每条用例都同时断言
/// “该删的删了”和“不该动的没动”。所有目录都在测试自建的临时目录内，
/// 不读注册表、不启动任何进程、不需要管理员权限。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ManagedAppRemovalTests
{
    [Fact]
    public async Task GreenReceipt_IsRemovedAndItsDirectoryIsDeleted()
    {
        using var fixture = new RemovalFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.GreenReceipt());
        var service = new ManagedInstalledProgramService(launcher);

        var program = Assert.Single(await service.ScanAsync());
        Assert.True(program.StoreManagedRemoval);
        Assert.True(program.CanUninstall);

        var result = await service.LaunchUninstallAsync(program);

        Assert.True(result.Started);
        Assert.Null(result.ErrorCode);
        // 关键区分：没有启动任何卸载器，因此没有命令行。UI 据此显示“已移除文件”而非“已启动卸载程序”。
        Assert.Null(result.CommandLine);
        Assert.False(Directory.Exists(fixture.Target));
        Assert.Equal(1, launcher.UnregisterCalls);
        Assert.Empty(await service.ScanAsync());
    }

    [Fact]
    public async Task ThirdPartyInstallerReceipt_IsRefusedAndNothingIsDeleted()
    {
        using var fixture = new RemovalFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        // 第三方安装器（MSI/NSIS 等）登记：本程序无法证明哪些文件是它创建的。
        launcher.Add(fixture.Receipt(Strategy.Custom, InstallerKind.Msi));
        var service = new ManagedInstalledProgramService(launcher);

        var program = Assert.Single(await service.ScanAsync());
        Assert.False(program.StoreManagedRemoval);
        Assert.False(program.CanUninstall);

        var result = await service.LaunchUninstallAsync(program);

        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        // 一个字节都不能动。
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(File.Exists(fixture.Executable));
        Assert.True(Directory.Exists(fixture.Target));
        Assert.Single(await service.ScanAsync());
    }

    [Theory]
    [InlineData(Strategy.Custom, InstallerKind.Msi)]
    [InlineData(Strategy.Default, InstallerKind.Nsis)]
    [InlineData(Strategy.Custom, InstallerKind.Inno)]
    public async Task RemovalService_RefusesReceiptsThisStoreDidNotExtract(Strategy strategy, InstallerKind installer)
    {
        using var fixture = new RemovalFixture("tool");
        var receipt = fixture.Receipt(strategy, installer);
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(receipt);
        var removal = new ManagedAppRemovalService(launcher);

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.NotStoreOwned, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(File.Exists(fixture.Executable));
    }

    [Fact]
    public async Task CallerSnapshotCannotAuthorizeDeletionOfAThirdPartyInstallation()
    {
        using var fixture = new RemovalFixture("tool");
        var receipt = fixture.Receipt(Strategy.Custom, InstallerKind.Msi);
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(receipt);
        var removal = new ManagedAppRemovalService(launcher);

        // 谎称这是本程序解包的安装；真正的判定必须回到按 ID 重读的登记原文。
        var forged = new InstalledProgram
        {
            DisplayName = "Tool",
            Origin = RegistryOrigin.StoreInstall,
            SourceKeyPath = receipt.InstallId,
            InstallLocation = fixture.Target,
            StoreManagedRemoval = true,
        };
        var service = new ManagedInstalledProgramService(launcher, removal);

        var result = await service.LaunchUninstallAsync(forged);

        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(File.Exists(fixture.Executable));
    }

    [Fact]
    public async Task DirectoryThatIsNotADirectChildOfTheInstallRoot_IsRefusedAndKept()
    {
        using var fixture = new RemovalFixture("tool");
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Root, "nested", "tool")).FullName;
        var executable = Path.Combine(nested, "tool.exe");
        File.WriteAllText(executable, "fake test exe; never executed");
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.Receipt(Strategy.Green, InstallerKind.Portable, nested, executable));
        var removal = new ManagedAppRemovalService(launcher);

        var result = await removal.RemoveAsync(launcher.SingleId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.UnsafeRemovalPath, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(File.Exists(executable));
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task DirectoryOutsideTheInstallRoot_IsRefusedAndKept()
    {
        using var fixture = new RemovalFixture("tool");
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Base, "elsewhere")).FullName;
        var executable = Path.Combine(outside, "tool.exe");
        File.WriteAllText(executable, "fake test exe; never executed");
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.Receipt(Strategy.Green, InstallerKind.Portable, outside, executable));
        var removal = new ManagedAppRemovalService(launcher);

        var result = await removal.RemoveAsync(launcher.SingleId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.UnsafeRemovalPath, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(File.Exists(executable));
    }

    [Fact]
    public async Task TheInstallRootItselfIsNeverADeletionTarget()
    {
        using var fixture = new RemovalFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        // 登记的 TargetDir 就是根目录本身：一旦放行就会删掉整个受管目录。
        launcher.Add(fixture.Receipt(Strategy.Green, InstallerKind.Portable, fixture.Root, fixture.Executable));
        var removal = new ManagedAppRemovalService(launcher);

        var result = await removal.RemoveAsync(launcher.SingleId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.UnsafeRemovalPath, result.ErrorCode);
        Assert.True(Directory.Exists(fixture.Root));
        Assert.True(File.Exists(fixture.Executable));
    }

    [Fact]
    public async Task ReparsePointTargetIsRefusedAndTheLinkedDirectorySurvives()
    {
        using var fixture = new RemovalFixture("tool");
        var realDirectory = fixture.Directory("real-target");
        var sentinel = Path.Combine(realDirectory, "keep.txt");
        File.WriteAllText(sentinel, "must survive");
        var link = Path.Combine(fixture.Root, "linked");
        CreateJunction(link, realDirectory);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);

        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.Receipt(Strategy.Green, InstallerKind.Portable, link, Path.Combine(link, "tool.exe")));
        var removal = new ManagedAppRemovalService(launcher);

        var result = await removal.RemoveAsync(launcher.SingleId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.UnsafeRemovalPath, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        // 绝不沿链接删除到受管目录之外。
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task DeletionFailureAfterUnregister_ReportsHonestlyAndKeepsTheReceiptRemoved()
    {
        using var fixture = new RemovalFixture("tool");
        var receipt = fixture.GreenReceipt();
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(receipt);
        var removal = new ManagedAppRemovalService(launcher, ThrowLocked);
        var service = new ManagedInstalledProgramService(launcher, removal);
        var program = Assert.Single(await service.ScanAsync());

        var result = await service.LaunchUninstallAsync(program);

        // 先解除登记、再删文件：删除失败也不能让登记指回已经消失的文件。
        Assert.Equal(1, launcher.UnregisterCalls);
        Assert.False(launcher.Has(receipt.InstallId));
        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.RemovalIncomplete, result.ErrorCode);
        // 文件还在，就必须如实说明可能残留，不能假装清理干净。
        Assert.True(Directory.Exists(fixture.Target));
    }

    [Fact]
    public async Task UnknownInstallId_IsRefusedWithoutTouchingAnything()
    {
        using var fixture = new RemovalFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var removal = new ManagedAppRemovalService(launcher);

        var result = await removal.RemoveAsync("install-does-not-exist");

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.InstallationNotFound, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(File.Exists(fixture.Executable));
    }

    [Fact]
    public async Task Service_RefusesAPathThatBecameUnsafeAndKeepsTheFiles()
    {
        using var fixture = new RemovalFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.GreenReceipt();
        launcher.Add(receipt);
        var service = new ManagedInstalledProgramService(launcher, new ManagedAppRemovalService(launcher));
        var program = Assert.Single(await service.ScanAsync());

        // 安装根目录被改成别处后，原目标目录不再是“当前根目录的直接子目录”。
        launcher.Root = fixture.Directory("other-root");
        var result = await service.LaunchUninstallAsync(program);

        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.UnsafeRemovalPath, result.ErrorCode);
        Assert.True(File.Exists(fixture.Executable));
        Assert.Equal(0, launcher.UnregisterCalls);
    }

    [Fact]
    public async Task ViewModel_ReportsRemovalAsRemovedFilesRatherThanAStartedUninstaller()
    {
        var service = new FakeInventory { Launch = UninstallLaunchResult.Removed() };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        var program = StoreProgram();
        await viewModel.UninstallConfirmedAsync(program);

        Assert.Empty(viewModel.ErrorMessage);
        Assert.Contains("已移除", viewModel.StatusMessage, StringComparison.Ordinal);
        // 没有启动卸载器，就不能说“已启动卸载程序”。
        Assert.DoesNotContain("已启动卸载程序", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_MapsIncompleteRemovalToAnHonestMessageWithoutLeakingTheCode()
    {
        var service = new FakeInventory { Launch = UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalIncomplete) };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.UninstallConfirmedAsync(StoreProgram());

        Assert.Contains("残留", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(UninstallErrorCodes.RemovalIncomplete, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.StatusMessage);
    }

    private static InstalledProgram StoreProgram() => new()
    {
        DisplayName = "Tool",
        Origin = RegistryOrigin.StoreInstall,
        SourceKeyPath = "install-one",
        InstallLocation = @"C:\githubapps\tool",
        StoreManagedRemoval = true,
    };

    private static Task ThrowLocked(string directory, CancellationToken ct) =>
        throw new IOException("simulated locked file");

    private static void CreateJunction(string link, string target)
    {
        // 只在测试夹具里用 mklink 造真实的重解析点；生产代码从不调用 shell。
        Assert.DoesNotContain('"', link);
        Assert.DoesNotContain('"', target);
        var start = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not create test junction.");
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>测试用临时目录：安装根目录、目标目录与一个假 exe 都在其中。</summary>
    private sealed class RemovalFixture : IDisposable
    {
        public RemovalFixture(string name)
        {
            Base = CreateRoot();
            // 本类有名为 Directory 的成员方法，这里必须限定，否则 Directory 会解析成方法组。
            Root = System.IO.Directory.CreateDirectory(Path.Combine(Base, "apps")).FullName;
            Target = System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            Executable = Path.Combine(Target, "tool.exe");
            File.WriteAllText(Executable, "fake test exe; never executed");
        }

        public string Base { get; }
        public string Root { get; }
        public string Target { get; }
        public string Executable { get; }

        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Base, name)).FullName;

        public InstallRecord GreenReceipt() => Receipt(Strategy.Green, InstallerKind.Portable, Target, Executable);

        public InstallRecord Receipt(Strategy strategy, InstallerKind installer) =>
            Receipt(strategy, installer, Target, Executable);

        /// <summary>登记原文；只有 Green/Portable 才代表本程序自己解包出来的文件。</summary>
        public InstallRecord Receipt(Strategy strategy, InstallerKind installer, string target, string executable)
        {
            var sourceId = new SourceInstanceId(Guid.NewGuid());
            var appId = new AppId(sourceId, new PackageId("fixture/tool"));
            return new InstallRecord
            {
                InstallId = "install-" + Guid.NewGuid().ToString("N"),
                AppId = appId,
                Source = new SourceBinding
                {
                    SourceId = sourceId, PackageId = appId.PackageId, SourceRevision = 1,
                    SourceEntryUrl = new Uri("https://example.invalid/source.json"), InstallerId = "portable-x64",
                },
                ReleaseKey = "stable:1.0", Origin = Origin.Store, State = InstallState.Done,
                Strategy = strategy, Installer = installer, Channel = Channel.Http,
                Version = new GitHubStore.Core.Abstractions.Version { Raw = "1.0", Scheme = VersionScheme.Semver },
                TargetDir = target, ExePath = executable, InstalledAt = DateTimeOffset.UtcNow,
            };
        }

        private static string CreateRoot()
        {
            // 与其它库存测试一致：系统临时目录不可写时回退到工作区，避免受限环境误报。
            foreach (var candidate in new[]
                     {
                         Path.GetTempPath(),
                         Path.Combine(System.IO.Directory.GetCurrentDirectory(), "test-temp"),
                     })
            {
                try
                {
                    var path = Path.Combine(candidate, "GitHubStore-removal-" + Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(path);
                    return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 尝试下一个可写根目录。
                }
            }
            throw new InvalidOperationException("No writable temporary directory is available for this test run.");
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Base)) System.IO.Directory.Delete(Base, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不应遮蔽测试结果。
            }
        }
    }

    /// <summary>内存中的受管启动器替身：不读文件、不启动进程，登记移除会被记录。</summary>
    private sealed class FakeLauncher(string root) : IManagedAppLauncher
    {
        private readonly Dictionary<string, InstallRecord> _receipts = new(StringComparer.Ordinal);

        public string Root { get; set; } = root;
        public string InstallRoot => Root;
        public int UnregisterCalls { get; private set; }

        /// <summary>唯一一条登记的安装 ID；只有一条登记时使用。</summary>
        public string SingleId => _receipts.Keys.Single();

        public event EventHandler? Changed { add { } remove { } }

        public void Add(InstallRecord receipt) => _receipts[receipt.InstallId] = receipt;

        public bool Has(string installId) => _receipts.ContainsKey(installId);

        public Task<InstallRecord?> GetAsync(string installId, CancellationToken ct = default) =>
            Task.FromResult(_receipts.TryGetValue(installId, out var receipt) ? receipt : null);

        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<LauncherApp> entries = _receipts.Values.Select(receipt => new LauncherApp
            {
                InstallId = receipt.InstallId,
                AppId = receipt.AppId,
                DisplayName = "Tool",
                InstallDirectory = receipt.TargetDir!,
                ExecutablePath = receipt.ExePath!,
                StoreManagedRemoval = StoreOwnedInstallation.OwnsExtractedFiles(receipt),
            }).ToArray();
            return Task.FromResult(entries);
        }

        public Task UnregisterAsync(string installId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UnregisterCalls++;
            _receipts.Remove(installId);
            return Task.CompletedTask;
        }

        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeInventory : IInstalledProgramService
    {
        public UninstallLaunchResult Launch { get; set; } = UninstallLaunchResult.Removed();

        public Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstalledProgram>>([]);

        public Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default) =>
            Task.FromResult(Launch);
    }
}
