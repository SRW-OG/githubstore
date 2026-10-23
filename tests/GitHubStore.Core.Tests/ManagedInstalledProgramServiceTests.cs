using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ManagedInstalledProgramServiceTests
{
    [Fact]
    public async Task InstalledPageUsesOnlyRegisteredAppsAndPreservesReceiptMetadata()
    {
        var launcher = new StubLauncher { Entries = [Entry("beta", "Beta"), Entry("alpha", "Alpha")] };
        var service = new ManagedInstalledProgramService(launcher);
        using var vm = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        Assert.Equal(new[] { "Alpha", "Beta" }, vm.Programs.Select(program => program.DisplayName));
        var program = vm.Programs[0];
        Assert.Equal("1.2.3", program.DisplayVersion);
        Assert.Equal(@"C:\githubapps\alpha", program.InstallLocation);
        Assert.Equal("install-alpha", program.SourceKeyPath);
        Assert.Equal(RegistryOrigin.StoreInstall, program.Origin);
        Assert.Null(program.UninstallString);
        Assert.False(program.CanUninstall);
        Assert.Contains("本程序已安装 2", vm.StatusMessage);
    }

    [Fact]
    public async Task RefreshAndSearchReflectChangedStoreRegistrations()
    {
        var launcher = new StubLauncher();
        using var vm = new InstalledViewModel(new ManagedInstalledProgramService(launcher), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        Assert.Empty(vm.Programs);
        launcher.Entries = [Entry("alpha", "Alpha")];
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Equal("Alpha", Assert.Single(vm.Programs).DisplayName);
        vm.Filter = "not-installed";
        Assert.Empty(vm.Programs);
        vm.Filter = "alpha";
        Assert.Single(vm.Programs);
        launcher.Entries = [];
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Empty(vm.Programs);
    }

    [Fact]
    public async Task ReceiptReadFailureIsReportedRatherThanScanningExternalApplications()
    {
        var launcher = new StubLauncher { Failure = new LauncherException("launcher_invalid_manifest") };
        using var vm = new InstalledViewModel(new ManagedInstalledProgramService(launcher), new LocalizationService("zh-CN"));
        await vm.InitializeAsync();
        Assert.Empty(vm.Programs);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.Equal(1, launcher.Reads);
    }

    [Fact]
    public async Task CallerSuppliedUninstallerCannotBeExecutedByStoreInventory()
    {
        var service = new ManagedInstalledProgramService(new StubLauncher());
        var result = await service.LaunchUninstallAsync(new InstalledProgram
        {
            DisplayName = "External app", SourceKeyPath = "external", Origin = RegistryOrigin.Hklm64,
            UninstallString = "untrusted.exe",
        });
        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
    }

    private static LauncherApp Entry(string repository, string name)
    {
        var app = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", repository), null,
            SeedFixture.FetchedAt, new CategoryId("system")).App;
        return new LauncherApp
        {
            InstallId = "install-" + repository, AppId = app.Id, DisplayName = name,
            InstallDirectory = @"C:\githubapps\" + repository,
            ExecutablePath = @"C:\githubapps\" + repository + @"\app.exe",
            DisplayVersion = "1.2.3", IsAvailable = false,
        };
    }

    private sealed class StubLauncher : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<LauncherApp> Entries { get; set; } = [];
        public Exception? Failure { get; init; }
        public int Reads { get; private set; }
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Entries);
        }
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnregisterAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
