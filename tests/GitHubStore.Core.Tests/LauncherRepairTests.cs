using System.Buffers.Binary;
using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 入口修复只改登记中的入口路径：它既不能代替卸载，也不能把启动指向安装目录之外。
/// 这些测试全部在临时目录内进行，不接触任何真实安装。
/// </summary>
public sealed class LauncherRepairTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "GitHubStore-repair-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "apps");
    private string Data => Path.Combine(_base, "data");
    private string Target => Path.Combine(Root, "Tool");

    public LauncherRepairTests() => Directory.CreateDirectory(_base);

    [Fact]
    public async Task PendingRecoveryBlocksLaunchAndReceiptMutationsEvenWhenExecutableIsValid()
    {
        var starts = 0;
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => { starts++; return true; });
        var receipt = await RegisterAsync(launcher, "Tool.exe");
        var originalManifest = await File.ReadAllBytesAsync(Path.Combine(Data, "managed-apps.json"));
        var journal = GitHubStore.App.Services.Installation.PortableRecoveryState.JournalPath(Data, receipt.InstallId);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllTextAsync(journal, "corrupt or legacy recovery data");

        var item = Assert.Single(await launcher.ListAsync());
        Assert.Equal(InstalledStatus.RecoveryRequired, item.Status);
        Assert.False(item.IsAvailable);
        Assert.Equal("launcher_recovery_required", (await launcher.LaunchAsync(receipt.InstallId)).ErrorCode);
        Assert.Equal("launcher_recovery_required", (await launcher.RepairExecutableAsync(receipt.InstallId, receipt.AppId, receipt.ExePath!)).ErrorCode);
        var unregister = await Assert.ThrowsAsync<LauncherException>(() => launcher.UnregisterAsync(receipt.InstallId));
        Assert.Equal("launcher_recovery_required", unregister.Code);
        Assert.Equal(0, starts);
        Assert.NotNull(await launcher.GetAsync(receipt.InstallId));
        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(Path.Combine(Data, "managed-apps.json")));
        var inventory = Assert.Single(await new ManagedInstalledProgramService(launcher).ScanAsync());
        Assert.Equal(InstalledStatus.RecoveryRequired, inventory.Status);
        Assert.False(inventory.CanUninstall);
        Assert.False(inventory.CanRepairExecutable);
        Assert.True(File.Exists(receipt.ExePath));
    }

    [Fact]
    public async Task OtherInstallJournalDoesNotBlockThisInstallation()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        var receipt = await RegisterAsync(launcher, "Tool.exe");
        var journal = GitHubStore.App.Services.Installation.PortableRecoveryState.JournalPath(Data, "unrelated-installation");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllTextAsync(journal, "unrelated");
        Assert.Equal(InstalledStatus.Installed, Assert.Single(await launcher.ListAsync()).Status);
        Assert.True((await launcher.LaunchAsync(receipt.InstallId)).Started);
    }

    [Fact]
    public async Task RepairUpdatesOnlyEntryPathAndKeepsIdentityAndMetadata()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        var receipt = await RegisterAsync(launcher, "Tool.exe");
        var moved = Path.Combine(Target, "bin", "Tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllBytes(moved, Executable());

        var result = await launcher.RepairExecutableAsync(receipt.InstallId, receipt.AppId, moved);

        Assert.True(result.Started, result.ErrorCode);
        var stored = await launcher.GetAsync(receipt.InstallId);
        Assert.NotNull(stored);
        Assert.Equal(Normalize(moved), stored!.ExePath);
        // 安装身份、目录、来源与移除依据都必须原样保留。
        Assert.Equal(receipt.InstallId, stored.InstallId);
        Assert.Equal(receipt.AppId, stored.AppId);
        Assert.Equal(receipt.TargetDir, stored.TargetDir);
        Assert.Equal(receipt.Source, stored.Source);
        Assert.Equal(receipt.Version.Raw, stored.Version.Raw);
        Assert.Equal(receipt.Installer, stored.Installer);
        Assert.Equal(receipt.Strategy, stored.Strategy);
        Assert.True(StoreOwnedInstallation.OwnsExtractedFiles(stored));
        Assert.True((await launcher.ListAsync()).Single().IsAvailable);
    }

    [Fact]
    public async Task RepairRejectsExecutableOutsideOriginalTarget()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        var receipt = await RegisterAsync(launcher, "Tool.exe");
        // 本程序管理的安装根目录下、但不在该应用的安装目录内：仍然必须拒绝。
        var outside = Path.Combine(Root, "Other", "Tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, Executable());

        var result = await launcher.RepairExecutableAsync(receipt.InstallId, receipt.AppId, outside);

        Assert.False(result.Started);
        Assert.Equal("launcher_invalid_path", result.ErrorCode);
        Assert.Equal(Normalize(Path.Combine(Target, "Tool.exe")), (await launcher.GetAsync(receipt.InstallId))!.ExePath);
    }

    [Fact]
    public async Task RepairRejectsForgedIdentityAndNonExecutableFile()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        var receipt = await RegisterAsync(launcher, "Tool.exe");
        var other = new AppId(new(Guid.NewGuid()), new("owner/other"));
        // 扩展名为 .exe 但内容不是程序：必须走可执行文件校验分支，而不是凭扩展名通过。
        var fake = Path.Combine(Target, "fake.exe");
        File.WriteAllText(fake, "not a program");
        var text = Path.Combine(Target, "notes.txt");
        File.WriteAllText(text, "not a program");

        var forged = await launcher.RepairExecutableAsync(receipt.InstallId, other, fake);
        var notExecutable = await launcher.RepairExecutableAsync(receipt.InstallId, receipt.AppId, fake);
        var wrongExtension = await launcher.RepairExecutableAsync(receipt.InstallId, receipt.AppId, text);

        Assert.Equal("launcher_identity_mismatch", forged.ErrorCode);
        Assert.Equal("launcher_executable_invalid", notExecutable.ErrorCode);
        Assert.Equal("launcher_invalid_path", wrongExtension.ErrorCode);
        Assert.Equal(Normalize(Path.Combine(Target, "Tool.exe")), (await launcher.GetAsync(receipt.InstallId))!.ExePath);
    }

    [Fact]
    public async Task MissingEntryReportsNeedsRepairWithoutLosingReceipt()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        await RegisterAsync(launcher, "Tool.exe");
        File.Delete(Path.Combine(Target, "Tool.exe"));

        var entry = Assert.Single(await launcher.ListAsync());

        Assert.False(entry.IsAvailable);
        Assert.Equal(InstalledStatus.NeedsRepair, entry.Status);
        // 入口缺失不等于未安装：登记必须仍然可以读到。
        Assert.NotNull(await launcher.GetAsync(entry.InstallId));
    }

    [Fact]
    public async Task CorruptIndexIsNotReportedAsMissingInstallation()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        await RegisterAsync(launcher, "Tool.exe");
        await File.WriteAllTextAsync(Path.Combine(Data, "managed-apps.json"), " not json");

        // 索引不可读必须抛出，让调用方显示“无法核对”，而不是空列表。
        await Assert.ThrowsAsync<LauncherException>(() => launcher.ListAsync());
    }

    [Fact]
    public async Task InventoryExposesStatusAndRepairAvailability()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        await RegisterAsync(launcher, "Tool.exe");
        var service = new ManagedInstalledProgramService(launcher);
        Assert.Equal(InstalledStatus.Installed, Assert.Single(await service.ScanAsync()).Status);

        File.Delete(Path.Combine(Target, "Tool.exe"));
        var broken = Assert.Single(await service.ScanAsync());
        Assert.Equal(InstalledStatus.NeedsRepair, broken.Status);
        Assert.True(broken.CanRepairExecutable);

        var moved = Path.Combine(Target, "bin", "Tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllBytes(moved, Executable());
        var repaired = await service.RepairExecutableAsync(broken, moved);
        Assert.True(repaired.Started, repaired.ErrorCode);
        Assert.Equal(UninstallRemovalKind.RepairApplied, repaired.Kind);
        Assert.Equal(InstalledStatus.Installed, Assert.Single(await service.ScanAsync()).Status);
    }

    [Fact]
    public async Task MissingUninstallRegistrationIsDistinctFromUnreadableRegistry()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        var receipt = await RegisterAsync(launcher, "Tool.exe", installer: InstallerKind.Inno, strategy: Strategy.Custom);

        // 读取成功但没有匹配项：如实显示“未匹配到”，而不是“无法核对”。仅凭这一条也不能断定已卸载。
        var none = new ManagedInstalledProgramService(launcher,
            registered: new RegisteredUninstallService(launcher, reader: () => []));
        var absent = Assert.Single(await none.ScanAsync());
        Assert.Equal(UninstallRegistrationStatus.NotFound, absent.RegistrationStatus);
        Assert.Equal(InstalledStatus.Installed, absent.Status);

        // 读取失败：必须显示“无法核对”，不能退化成“没有登记”。
        var failing = new ManagedInstalledProgramService(launcher,
            registered: new RegisteredUninstallService(launcher, reader: () => throw new InvalidOperationException("registry unavailable")));
        var unknown = Assert.Single(await failing.ScanAsync());
        Assert.Equal(UninstallRegistrationStatus.Unknown, unknown.RegistrationStatus);
        Assert.Equal(InstalledStatus.Installed, unknown.Status);
    }

    [Fact]
    public async Task RepairIsRefusedWhenIndexIsUnreadable()
    {
        var launcher = new ManagedAppLauncher(Data, Root, (_, _) => true);
        await RegisterAsync(launcher, "Tool.exe");
        await File.WriteAllTextAsync(Path.Combine(Data, "managed-apps.json"), " not json");

        var service = new ManagedInstalledProgramService(launcher);
        // 索引不可读时没有可核对的登记，修复必须拒绝，且不能凭空创建记录。
        var result = await service.RepairExecutableAsync(new InstalledProgram
        {
            DisplayName = "Tool", SourceKeyPath = "install-tool", Origin = RegistryOrigin.StoreInstall,
            Status = InstalledStatus.NeedsRepair,
        }, Path.Combine(Target, "Tool.exe"));

        Assert.False(result.Started);
        Assert.Equal(" not json", await File.ReadAllTextAsync(Path.Combine(Data, "managed-apps.json")));
    }

    private async Task<InstallRecord> RegisterAsync(ManagedAppLauncher launcher, string executable,
        InstallerKind installer = InstallerKind.Portable, Strategy strategy = Strategy.Green)
    {
        Directory.CreateDirectory(Target);
        var path = Path.Combine(Target, executable);
        await File.WriteAllBytesAsync(path, Executable());
        var id = new AppId(new(Guid.NewGuid()), new("owner/tool"));
        var receipt = new InstallRecord
        {
            InstallId = "install-tool", AppId = id, ReleaseKey = "stable:v1",
            Source = new SourceBinding
            {
                SourceId = id.SourceId, PackageId = id.PackageId, SourceRevision = 1,
                SourceEntryUrl = new Uri("https://github.com/owner/tool"), InstallerId = "asset-1",
            },
            Version = new GitHubStore.Core.Abstractions.Version { Raw = "v1" }, DisplayVersion = "v1",
            Origin = Origin.Store, State = InstallState.Done, Strategy = strategy,
            Installer = installer, TargetDir = Target, ExePath = path,
            InstalledAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        return (await launcher.GetAsync(receipt.InstallId))!;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>最小可校验的 PE 头；不依赖任何真实程序文件。</summary>
    private static byte[] Executable()
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 2);
        return bytes;
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, true);
    }
}
