using System.ComponentModel;
using GitHubStore.App.Services.Inventory;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class RegisteredUninstallTests
{
    [Fact]
    public async Task OlderMsiReceiptFindsWindowsRegistrationAndUsesInteractiveElevatedUninstaller()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Launcher.Receipt.ProductCode);
        var called = false;
        var service = fixture.Service((command, elevate, _) =>
        {
            called = true;
            Assert.True(elevate);
            Assert.DoesNotContain("/qn", command.Arguments);
            fixture.Rows = [];
            return Task.FromResult(0);
        });
        var inventory = new ManagedInstalledProgramService(fixture.Launcher, registered: service);
        var program = Assert.Single(await inventory.ScanAsync());
        Assert.True(program.CanUninstall);
        // UI commands are not executed: the service independently rereads the Windows entry.
        var result = await inventory.LaunchUninstallAsync(program with { UninstallString = "cmd.exe /c forged" });
        Assert.True(result.Started);
        Assert.True(called);
        Assert.True(fixture.Launcher.Unregistered);
    }

    [Fact]
    public async Task CancellingAdministratorApprovalKeepsReceipt()
    {
        using var fixture = new Fixture();
        var service = fixture.Service((_, _, _) => throw new Win32Exception(1223));
        var result = await service.UninstallAsync("install");
        Assert.False(result.Started);
        Assert.Equal("uninstall_cancelled", result.ErrorCode);
        Assert.False(fixture.Launcher.Unregistered);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(1602)]
    public async Task FailedOrCancelledWindowsUninstallerKeepsReceipt(int exit)
    {
        using var fixture = new Fixture();
        var result = await fixture.Service((_, _, _) => Task.FromResult(exit)).UninstallAsync("install");
        Assert.False(result.Started);
        Assert.False(fixture.Launcher.Unregistered);
    }

    [Fact]
    public async Task ReturningBeforeWindowsRegistrationDisappearsDoesNotClaimCompletion()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service((_, _, _) => Task.FromResult(0)).UninstallAsync("install");
        Assert.True(result.Started);
        Assert.Equal(UninstallRemovalKind.UninstallerLaunched, result.Kind);
        Assert.False(fixture.Launcher.Unregistered);
    }

    [Fact]
    public void SameDisplayNameAtDifferentLocationDoesNotAuthorizeUninstall()
    {
        using var fixture = new Fixture();
        fixture.Rows = [fixture.Rows[0] with { InstallLocation = Path.Combine(fixture.Root, "unrelated") }];
        Assert.Null(fixture.Service((_, _, _) => throw new Exception("must not run")).Find(fixture.Launcher.Receipt, "KeePassXC"));
    }

    [Fact]
    public void AmbiguousWindowsEntriesDoNotSelectAnArbitraryUninstaller()
    {
        using var fixture = new Fixture();
        fixture.Rows = [fixture.Rows[0], fixture.Rows[0] with { Key = "second" }];
        Assert.Null(fixture.Service((_, _, _) => throw new Exception("must not run")).Find(fixture.Launcher.Receipt, "KeePassXC"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitReceiptRemovalPreservesFilesAndDoesNotConsultRegistry(bool registryFails)
    {
        using var fixture = new Fixture();
        fixture.Launcher.Receipt = fixture.Launcher.Receipt with { Installer = InstallerKind.Inno };
        Directory.CreateDirectory(fixture.Launcher.Receipt.TargetDir!);
        File.WriteAllText(fixture.Launcher.Receipt.ExePath!, "application");
        var userFile = Path.Combine(fixture.Launcher.Receipt.TargetDir!, "settings.json");
        File.WriteAllText(userFile, "user settings");
        var registered = new RegisteredUninstallService(fixture.Launcher,
            () => registryFails ? throw new IOException("registry unavailable") : [],
            (_, _, _) => throw new Exception("must not launch"));
        var inventory = new ManagedInstalledProgramService(fixture.Launcher, registered: registered);
        var program = Assert.Single(await inventory.ScanAsync());
        Assert.False(program.CanUninstall);
        Assert.True(program.CanRemoveRegistration);
        Assert.False(fixture.Launcher.Unregistered); // Missing registry alone never removes a receipt.
        var result = await inventory.RemoveRegistrationAsync(program);
        Assert.Equal(UninstallRemovalKind.RegistrationRemoved, result.Kind);
        Assert.True(result.Started);
        Assert.Null(result.CommandLine);
        Assert.True(fixture.Launcher.Unregistered);
        Assert.Equal("application", File.ReadAllText(fixture.Launcher.Receipt.ExePath!));
        Assert.Equal("user settings", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task ForgedUiOwnershipCannotForgetPortableOrExternalReceipt()
    {
        using var fixture = new Fixture();
        var inventory = new ManagedInstalledProgramService(fixture.Launcher, registered: fixture.Service((_, _, _) => throw new Exception()));
        var program = Assert.Single(await inventory.ScanAsync()) with { CanRemoveRegistration = true };
        fixture.Launcher.Receipt = fixture.Launcher.Receipt with { Installer = InstallerKind.Zip, Strategy = Strategy.Green };
        Assert.False((await inventory.RemoveRegistrationAsync(program)).Started);
        Assert.False(fixture.Launcher.Unregistered);
        fixture.Launcher.Receipt = fixture.Launcher.Receipt with { Installer = InstallerKind.Inno, Origin = Origin.External };
        Assert.False((await inventory.RemoveRegistrationAsync(program)).Started);
        Assert.False(fixture.Launcher.Unregistered);
    }

    [Fact]
    public async Task MissingExecutableAndRegistryRemainVisibleUntilExplicitReceiptRemoval()
    {
        using var fixture = new Fixture();
        fixture.Rows = [];
        Directory.CreateDirectory(fixture.Launcher.Receipt.TargetDir!);
        var userFile = Path.Combine(fixture.Launcher.Receipt.TargetDir!, "notes.txt");
        File.WriteAllText(userFile, "keep me");
        Assert.False(File.Exists(fixture.Launcher.Receipt.ExePath));
        var inventory = new ManagedInstalledProgramService(fixture.Launcher,
            registered: fixture.Service((_, _, _) => throw new Exception("must not launch")));
        var program = Assert.Single(await inventory.ScanAsync());
        Assert.False(fixture.Launcher.Unregistered);
        Assert.False((await inventory.LaunchUninstallAsync(program)).Started);
        Assert.False(fixture.Launcher.Unregistered);
        Assert.True((await inventory.RemoveRegistrationAsync(program)).Started);
        Assert.Equal("keep me", File.ReadAllText(userFile));
        Assert.Empty(await inventory.ScanAsync());
    }

    [Fact]
    public async Task ConfirmedReceiptRemovalUpdatesInstalledListAndReportsFilesPreserved()
    {
        using var fixture = new Fixture();
        fixture.Rows = [];
        var inventory = new ManagedInstalledProgramService(fixture.Launcher, registered: fixture.Service((_, _, _) => throw new Exception()));
        using var vm = new GitHubStore.App.ViewModels.InstalledViewModel(inventory,
            new GitHubStore.App.Services.Localization.LocalizationService("en-US"));
        await vm.InitializeAsync();
        var confirmed = Assert.Single(vm.Programs);
        vm.SelectedProgram = confirmed;
        Assert.True(vm.CanRemoveRegistration);
        Assert.False(vm.CanUninstall);
        vm.SelectedProgram = null; // A changed selection must not redirect the confirmed operation.
        await vm.RemoveRegistrationConfirmedAsync(confirmed);
        Assert.Empty(vm.Programs);
        Assert.Contains("Application and user files were preserved", vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public string Root => _temp.DirectoryPath;
        public LauncherFake Launcher { get; }
        public RegisteredUninstaller[] Rows { get; set; }
        public Fixture()
        {
            var exe = Path.Combine(Root, "uninstall.exe");
            File.WriteAllText(exe, "never executed");
            var id = new AppId(new SourceInstanceId(Guid.NewGuid()), new PackageId("fixture/keepassxc"));
            Launcher = new(new InstallRecord
            {
                InstallId = "install", AppId = id, ReleaseKey = "stable:1",
                Source = new SourceBinding { SourceId = id.SourceId, PackageId = id.PackageId, SourceRevision = 1,
                    SourceEntryUrl = new Uri("https://github.com/fixture/keepassxc"), InstallerId = "fixture-msi" },
                Version = new GitHubStore.Core.Abstractions.Version { Raw = "1" },
                Origin = Origin.Store, State = InstallState.Done, Installer = InstallerKind.Msi,
                Strategy = Strategy.Custom, TargetDir = Path.Combine(Root, "keepassxc"),
                ExePath = Path.Combine(Root, "keepassxc", "KeePassXC.exe"),
            });
            Rows = [new("windows-key", "KeePassXC", Launcher.Receipt.TargetDir!, "\"" + exe + "\" /x " + Guid.NewGuid().ToString("B"), true, Guid.NewGuid().ToString("B"))];
        }
        public RegisteredUninstallService Service(Func<UninstallCommand, bool, CancellationToken, Task<int>> run) =>
            new(Launcher, () => Rows, run);
        public void Dispose() => _temp.Dispose();
    }

    private sealed class LauncherFake(InstallRecord receipt) : IManagedAppLauncher
    {
        public InstallRecord Receipt { get; set; } = receipt;
        public bool Unregistered;
        public event EventHandler? Changed { add { } remove { } }
        public Task<InstallRecord?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<InstallRecord?>(id == Receipt.InstallId && !Unregistered ? Receipt : null);
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherApp>>(
            Unregistered ? [] : [new LauncherApp { InstallId = Receipt.InstallId, AppId = Receipt.AppId,
                DisplayName = "keepassxc", InstallDirectory = Receipt.TargetDir!, ExecutablePath = Receipt.ExePath! }]);
        public Task UnregisterAsync(string id, CancellationToken ct = default) { Unregistered = true; return Task.CompletedTask; }
        public Task RegisterCompletedInstallAsync(InstallRecord record, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
