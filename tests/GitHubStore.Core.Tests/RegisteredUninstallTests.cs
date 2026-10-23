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
        public InstallRecord Receipt => receipt;
        public bool Unregistered;
        public event EventHandler? Changed { add { } remove { } }
        public Task<InstallRecord?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<InstallRecord?>(id == receipt.InstallId && !Unregistered ? receipt : null);
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LauncherApp>>(
            Unregistered ? [] : [new LauncherApp { InstallId = receipt.InstallId, AppId = receipt.AppId,
                DisplayName = "keepassxc", InstallDirectory = receipt.TargetDir!, ExecutablePath = receipt.ExePath! }]);
        public Task UnregisterAsync(string id, CancellationToken ct = default) { Unregistered = true; return Task.CompletedTask; }
        public Task RegisterCompletedInstallAsync(InstallRecord record, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
