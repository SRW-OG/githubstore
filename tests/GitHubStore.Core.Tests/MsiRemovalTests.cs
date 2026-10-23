using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 本商店自己执行的 MSI 的移除路径。这条路径会用产品码调用 msiexec（可能具有提升效果），
/// 并谎报成功，因此每条用例都同时断言三件事：
/// “该调用时调用了什么”“不该执行时一次都没有执行”“只有核验通过才允许报告成功”。
/// 所有测试都注入替身，不启动任何真实进程、不读注册表、不需要管理员权限。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MsiRemovalTests
{
    private const string ValidProductCode = "{7B154FB8-DD64-452C-9B09-64AD98205DE1}";

    [Theory]
    // 真实产品码形态（大小写都应接受）。
    [InlineData("{7B154FB8-DD64-452C-9B09-64AD98205DE1}", true)]
    [InlineData("{7b154fb8-dd64-452c-9b09-64ad98205de1}", true)]
    [InlineData("{00000000-0000-0000-0000-000000000000}", true)]
    // 缺花括号、长度不符、连字符位置错误、含非十六进制字符、相对/可疑文本。
    [InlineData("7B154FB8-DD64-452C-9B09-64AD98205DE1", false)]
    [InlineData("{7B154FB8DD64452C9B0964AD98205DE1}", false)]
    [InlineData("{7B154FB8-DD64-452C-9B09-64AD98205DE}", false)]
    [InlineData("{7B154FB8-DD64-452C-9B09-64AD98205DE11}", false)]
    [InlineData("{7B154FB8DD64-452C-9B09-64AD98205DE1}", false)]
    [InlineData("{7B154FB8-DD64-452C-9B09-64AD98205DEZ}", false)]
    [InlineData("{7B154FB8-DD64-452C-9B09-64AD98205DE }", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("..\\msiexec.exe", false)]
    [InlineData("msiexec", false)]
    [InlineData("{7B154FB8-DD64-452C-9B09-64AD98205DE1} /qn", false)]
    [InlineData(null, false)]
    public void ProductCode_IsAcceptedOnlyInTheExactBracedGuidShape(string? value, bool expected)
    {
        Assert.Equal(expected, ProductCodeFormat.IsValid(value));
        Assert.Equal(expected, MsiPackageInspector.TryNormalizeProductCode(value, out var normalized));
        // 归一化不得改写内容：接受即原样返回，否则必须是 null。
        Assert.Equal(expected ? value : null, normalized);
    }

    [Fact]
    public async Task ValidMsiReceipt_RunsMsiExecWithRecordedProductCodeAndRemovesReceipt()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(ValidProductCode);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls);

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.True(result.Removed);
        Assert.Null(result.ErrorCode);
        // 只调用一次，且产品码必须与登记原文完全一致——不接受任何调用方提供的值。
        var call = Assert.Single(calls);
        Assert.Equal(ValidProductCode, call.ProductCode);
        Assert.Equal(fixture.MsiExec, call.MsiExec);
        // 核验通过后才移除登记：成功返回时登记已经不在，不会指向已卸载的文件。
        Assert.Equal(1, launcher.UnregisterCalls);
        Assert.False(launcher.Has(receipt.InstallId));
    }

    [Fact]
    public async Task MsiReceiptWithoutProductCode_IsRefusedAndNothingIsExecuted()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(productCode: null);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls);

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.NotStoreOwned, result.ErrorCode);
        // 没有可信产品码就没有可信的卸载依据：一次进程都不许启动。
        Assert.Empty(calls);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(launcher.Has(receipt.InstallId));
    }

    [Fact]
    public async Task TamperedProductCode_IsRejectedBeforeAnyProcessLaunch()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        // 登记被改成注入式文本：含额外参数、路径与自定义开关。
        var receipt = fixture.MsiReceipt("{7B154FB8-DD64-452C-9B09-64AD98205DE1} /qn C:\\evil\\x.exe");
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls);

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.NotStoreOwned, result.ErrorCode);
        Assert.Empty(calls);
        Assert.Equal(0, launcher.UnregisterCalls);
    }

    [Fact]
    public async Task MsiReceiptWithoutProductCode_FromInstalledPageStillReportsNoUninstallString()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.MsiReceipt(productCode: null));
        var calls = new List<(string MsiExec, string ProductCode)>();
        var service = new ManagedInstalledProgramService(launcher, fixture.Removal(launcher, calls));

        var program = Assert.Single(await service.ScanAsync());
        // 没有产品码：按钮不亮，也不允许走移除路径。
        Assert.False(program.StorePerformedMsiRemoval);
        Assert.False(program.CanUninstall);

        var result = await service.LaunchUninstallAsync(program);

        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        Assert.Empty(calls);
        Assert.Equal(0, launcher.UnregisterCalls);
    }

    [Theory]
    [InlineData(Strategy.Custom, InstallerKind.Nsis)]
    [InlineData(Strategy.Default, InstallerKind.Inno)]
    [InlineData(Strategy.Custom, InstallerKind.Squirrel)]
    public async Task NonMsiThirdPartyReceipt_IsStillRefusedAndNothingIsExecuted(Strategy strategy, InstallerKind installer)
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.Receipt(strategy, installer, ValidProductCode));
        var calls = new List<(string MsiExec, string ProductCode)>();
        var service = new ManagedInstalledProgramService(launcher, fixture.Removal(launcher, calls));

        var program = Assert.Single(await service.ScanAsync());
        Assert.False(program.StoreManagedRemoval);
        Assert.False(program.StorePerformedMsiRemoval);
        var result = await service.LaunchUninstallAsync(program);

        // 非 MSI 的第三方安装器仍然拿不到卸载能力，连产品码都不看。
        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        Assert.Empty(calls);
        Assert.Equal(0, launcher.UnregisterCalls);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(1925)]
    public async Task MsiExecElevationOrAccessFailures_MapToAccessDenied(int exitCode)
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(ValidProductCode);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls, new MsiUninstallOutcome(exitCode));

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.AccessDenied, result.ErrorCode);
        // 失败时登记必须原样保留，才能重试；绝不留下“登记已删但产品还在”的状态。
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(launcher.Has(receipt.InstallId));
        Assert.Single(calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1619)]
    [InlineData(-2147024891)]
    public async Task OtherNonZeroMsiExecExits_MapToRemovalFailed(int exitCode)
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(ValidProductCode);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls, new MsiUninstallOutcome(exitCode));

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.RemovalFailed, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(launcher.Has(receipt.InstallId));
    }

    [Theory]
    // 退出码为 0 不等于结果：产品或目录任一仍存在，都不能报告成功。
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MsiExecExitZeroWithoutVerifiedAbsence_ReportsRemovalIncomplete(bool productRemains, bool directoryRemains)
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(ValidProductCode);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls, new MsiUninstallOutcome(0),
            productRegistered: productRemains, directoryExists: directoryRemains);

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.False(result.Removed);
        Assert.Equal(UninstallErrorCodes.RemovalIncomplete, result.ErrorCode);
        Assert.Equal(0, launcher.UnregisterCalls);
        Assert.True(launcher.Has(receipt.InstallId));
    }

    [Fact]
    public async Task MsiExecExitZeroWithVerifiedAbsence_ReportsSuccessAndKeepsNoReceiptPointingAtRemovedFiles()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(ValidProductCode);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        var removal = fixture.Removal(launcher, calls, new MsiUninstallOutcome(0),
            productRegistered: false, directoryExists: false);

        var result = await removal.RemoveAsync(receipt.InstallId);

        Assert.True(result.Removed);
        Assert.Equal(1, launcher.UnregisterCalls);
        Assert.False(launcher.Has(receipt.InstallId));
        // 成功路径必须如实携带实际执行的命令行，UI 才能区分“静默核验完成”与“启动了交互式卸载器”。
        Assert.Contains(ValidProductCode, result.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MsiExecFailureCannotBeAuthorizedByAForcedManagedDirectoryClaim()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        var receipt = fixture.MsiReceipt(ValidProductCode);
        launcher.Add(receipt);
        var calls = new List<(string MsiExec, string ProductCode)>();
        // 伪造快照：谎称这是本程序解包的文件，并要求删除目录。
        var forged = new InstalledProgram
        {
            DisplayName = "Tool",
            Origin = RegistryOrigin.StoreInstall,
            SourceKeyPath = receipt.InstallId,
            InstallLocation = fixture.Target,
            StoreManagedRemoval = true,
            StorePerformedMsiRemoval = true,
        };
        var deletes = 0;
        var removal = fixture.Removal(launcher, calls, new MsiUninstallOutcome(1603),
            deleteDirectory: (_, _) => { deletes++; return Task.CompletedTask; });
        var service = new ManagedInstalledProgramService(launcher, removal);

        var result = await service.LaunchUninstallAsync(forged);

        // MSI 不再走静默删除路径：没有 Windows 登记时拒绝，不执行进程，也不删除目录和登记。
        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        Assert.Empty(calls);
        Assert.Equal(0, deletes);
        Assert.Equal(0, launcher.UnregisterCalls);
    }

    [Fact]
    public async Task InstalledPageRequiresWindowsRegistrationBeforeEnablingMsiUninstall()
    {
        using var fixture = new MsiFixture("tool");
        var launcher = new FakeLauncher(fixture.Root);
        launcher.Add(fixture.MsiReceipt(ValidProductCode));
        var calls = new List<(string MsiExec, string ProductCode)>();
        var registered = new RegisteredUninstallService(launcher, () => [],
            (_, _, _) => throw new InvalidOperationException("Must not launch without Windows registration"));
        var service = new ManagedInstalledProgramService(launcher, fixture.Removal(launcher, calls), registered);
        var program = Assert.Single(await service.ScanAsync());
        Assert.False(program.CanUninstall);
        var result = await service.LaunchUninstallAsync(program);
        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        Assert.Empty(calls);
        Assert.Equal(0, launcher.UnregisterCalls);
    }

    [Fact]
    public async Task ViewModel_ReportsProductCodeRemovalWithItsOwnWordingInsteadOfAStartedUninstaller()
    {
        var service = new FakeInventory
        {
            Launch = UninstallLaunchResult.RemovedByProductCode("\"C:\\Windows\\System32\\msiexec.exe\" /x " + ValidProductCode),
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));

        await viewModel.UninstallConfirmedAsync(new InstalledProgram
        {
            DisplayName = "Tool",
            Origin = RegistryOrigin.StoreInstall,
            SourceKeyPath = "install-msi",
            InstallLocation = @"C:\githubapps\tool",
            StorePerformedMsiRemoval = true,
        });

        Assert.Empty(viewModel.ErrorMessage);
        // 用产品码卸载不是“启动了第三方卸载器”，文案必须说清楚。
        Assert.Contains("Windows 卸载程序已完成", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("已启动卸载程序", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_MapsIncompleteMsiRemovalWithoutLeakingTheCode()
    {
        var service = new FakeInventory { Launch = UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalIncomplete) };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));

        await viewModel.UninstallConfirmedAsync(new InstalledProgram
        {
            DisplayName = "Tool",
            Origin = RegistryOrigin.StoreInstall,
            SourceKeyPath = "install-msi",
            InstallLocation = @"C:\githubapps\tool",
            StorePerformedMsiRemoval = true,
        });

        Assert.NotEmpty(viewModel.ErrorMessage);
        Assert.DoesNotContain(UninstallErrorCodes.RemovalIncomplete, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.StatusMessage);
    }

    [Fact]
    public void Inspector_SelectsOnlyPublicInstallRootProperties()
    {
        // 真实 KeePassXC 2.8.0 的 Directory 表形态：INSTALL_ROOT 挂在 ProgramFiles64Folder 下。
        Assert.Equal("INSTALL_ROOT", MsiPackageInspector.SelectInstallRootProperty(
        [
            new MsiDirectoryRow("TARGETDIR", null),
            new MsiDirectoryRow("ProgramFiles64Folder", "TARGETDIR"),
            new MsiDirectoryRow("INSTALL_ROOT", "ProgramFiles64Folder"),
        ]));

        // 小写键是私有属性，命令行无法设置：必须返回 null，而不是接受它。
        Assert.Null(MsiPackageInspector.SelectInstallRootProperty(
        [
            new MsiDirectoryRow("TARGETDIR", null),
            new MsiDirectoryRow("ProgramFiles64Folder", "TARGETDIR"),
            new MsiDirectoryRow("InstallRoot", "ProgramFiles64Folder"),
        ]));

        // 只有 OS 位置别名时同样没有可预填的应用目录。
        Assert.Null(MsiPackageInspector.SelectInstallRootProperty(
        [
            new MsiDirectoryRow("TARGETDIR", null),
            new MsiDirectoryRow("ProgramFiles64Folder", "TARGETDIR"),
            new MsiDirectoryRow("DesktopFolder", "TARGETDIR"),
        ]));

        // 空表、null 与超长键都不能抛出，也绝不臆造属性名。
        Assert.Null(MsiPackageInspector.SelectInstallRootProperty([]));
        Assert.Null(MsiPackageInspector.SelectInstallRootProperty(null));
        Assert.Null(MsiPackageInspector.SelectInstallRootProperty(
            [new MsiDirectoryRow(new string('A', 500), "ProgramFiles64Folder")]));
    }

    [Fact]
    public void Inspector_PublicPropertyNameRulesMatchWindowsInstaller()
    {
        Assert.True(MsiPackageInspector.IsPublicPropertyName("INSTALL_ROOT"));
        Assert.True(MsiPackageInspector.IsPublicPropertyName("INSTALLLOCATION"));
        Assert.True(MsiPackageInspector.IsPublicPropertyName("A1"));
        Assert.False(MsiPackageInspector.IsPublicPropertyName("InstallRoot"));
        Assert.False(MsiPackageInspector.IsPublicPropertyName("_INSTALLROOT"));
        Assert.False(MsiPackageInspector.IsPublicPropertyName("1ROOT"));
        Assert.False(MsiPackageInspector.IsPublicPropertyName(""));
        Assert.False(MsiPackageInspector.IsPublicPropertyName(null));
        Assert.False(MsiPackageInspector.IsPublicPropertyName("ROOT DIR"));
    }

    [Fact]
    public void Inspector_FallsBackWhenTheFileIsNotAnMsiOrIsMissing()
    {
        // 非 OLE 复合文档：直接失败，不做任何猜测（也不会调用 COM）。
        var text = Path.Combine(Path.GetTempPath(), "GitHubStore-not-msi-" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllText(text, "this is not an installer");
        try
        {
            var inspection = MsiPackageInspector.Inspect(text);
            Assert.False(inspection.Opened);
            Assert.Null(inspection.InstallRootProperty);
            Assert.Null(inspection.ProductCode);
            Assert.NotNull(inspection.FailureCode);
        }
        finally { File.Delete(text); }

        var missing = MsiPackageInspector.Inspect(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".msi"));
        Assert.False(missing.Opened);
        Assert.Null(missing.InstallRootProperty);

        Assert.False(MsiPackageInspector.Inspect(null).Opened);
        Assert.False(MsiPackageInspector.Inspect("   ").Opened);
    }

    /// <summary>测试用临时目录：受管安装根、目标目录与一个假 exe 都在其中。</summary>
    private sealed class MsiFixture : IDisposable
    {
        public MsiFixture(string name)
        {
            Base = CreateRoot();
            Root = System.IO.Directory.CreateDirectory(Path.Combine(Base, "apps")).FullName;
            Target = System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            Executable = Path.Combine(Target, "tool.exe");
            File.WriteAllText(Executable, "fake test exe; never executed");
            MsiExec = Path.Combine(Base, "msiexec.exe");
        }

        public string Base { get; }
        public string Root { get; }
        public string Target { get; }
        public string Executable { get; }
        /// <summary>替身 msiexec 路径：只用于断言，绝不会被真正启动。</summary>
        public string MsiExec { get; }

        public InstallRecord MsiReceipt(string? productCode) =>
            Receipt(Strategy.Custom, InstallerKind.Msi, productCode);

        public InstallRecord Receipt(Strategy strategy, InstallerKind installer, string? productCode)
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
                    SourceEntryUrl = new Uri("https://example.invalid/source.json"), InstallerId = "msi-x64",
                },
                ReleaseKey = "stable:1.0", Origin = Origin.Store, State = InstallState.Done,
                Strategy = strategy, Installer = installer, Channel = Channel.Http,
                Version = new GitHubStore.Core.Abstractions.Version { Raw = "1.0", Scheme = VersionScheme.Semver },
                TargetDir = Target, ExePath = Executable, ProductCode = productCode,
                InstalledAt = DateTimeOffset.UtcNow,
            };
        }

        /// <summary>
        /// 全部外部效果都注入替身：msiexec 调用、产品码注册检查、目录存在性、msiexec 解析与目录删除。
        /// 因此这条“会用产品码调用 msiexec 并删用户文件”的路径可以在无进程、无注册表、无管理员权限下被验证。
        /// </summary>
        public ManagedAppRemovalService Removal(FakeLauncher launcher,
            List<(string MsiExec, string ProductCode)> calls,
            MsiUninstallOutcome? outcome = null,
            bool productRegistered = false, bool directoryExists = false,
            Func<string, CancellationToken, Task>? deleteDirectory = null) =>
            new(launcher,
                deleteDirectory ?? ((_, _) => Task.CompletedTask),
                (msiExec, productCode, _) =>
                {
                    calls.Add((msiExec, productCode));
                    return Task.FromResult(outcome ?? new MsiUninstallOutcome(0));
                },
                _ => productRegistered,
                _ => directoryExists,
                () => MsiExec);

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
                    var path = Path.Combine(candidate, "GitHubStore-msi-" + Guid.NewGuid().ToString("N"));
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

        public string InstallRoot => root;
        public int UnregisterCalls { get; private set; }

        public event EventHandler? Changed { add { } remove { } }

        public void Add(InstallRecord receipt) => _receipts[receipt.InstallId] = receipt;

        public bool Has(string installId) => _receipts.ContainsKey(installId);

        public Task<InstallRecord?> GetAsync(string installId, CancellationToken ct = default) =>
            Task.FromResult(_receipts.TryGetValue(installId, out var receipt) ? receipt : null);

        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // 与真实 ManagedAppLauncher 一致：两个标记都由登记算出，而不是由调用方提供。
            IReadOnlyList<LauncherApp> entries = _receipts.Values.Select(receipt => new LauncherApp
            {
                InstallId = receipt.InstallId,
                AppId = receipt.AppId,
                DisplayName = "Tool",
                InstallDirectory = receipt.TargetDir!,
                ExecutablePath = receipt.ExePath!,
                StoreManagedRemoval = StoreOwnedInstallation.OwnsExtractedFiles(receipt),
                StorePerformedMsiRemoval = StoreOwnedInstallation.CanRemoveMsi(receipt),
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
