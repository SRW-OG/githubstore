using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class SourcesInstallViewModelTests
{
    [Fact]
    public async Task SelectionOnlyEnablesInstallWithoutExecutingIt()
    {
        var service = new FakeWinget();
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        Assert.False(vm.CanInstall);
        await vm.LoadDetailsAsync(new WingetPackage { Id = "Vendor.Tool", Name = "Tool" });
        Assert.True(vm.CanInstall);
        Assert.Equal(0, service.Calls);
        await vm.InstallSelectedAsync();
        Assert.Equal("Vendor.Tool", service.LastId);
        Assert.Contains("已完成安装", vm.InstallStatus);
        Assert.Equal("complete", vm.InstallOutput);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task DuplicateInstallIsBlockedAndResultKeepsOriginalTargetWhenSelectionChanges()
    {
        var pending = new TaskCompletionSource<WingetInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeWinget { Respond = (_, _) => pending.Task };
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        await vm.LoadDetailsAsync(new WingetPackage { Id = "First.Tool", Name = "First" });
        var install = vm.InstallSelectedAsync();
        Assert.True(vm.IsInstalling);
        Assert.False(vm.CanInstall);
        await vm.LoadDetailsAsync(new WingetPackage { Id = "Second.Tool", Name = "Second" });
        await vm.InstallSelectedAsync();
        Assert.Equal(1, service.Calls);
        pending.SetResult(new WingetInstallResult { Succeeded = true, RebootRequired = true });
        await install;
        Assert.Contains("First", vm.InstallStatus);
        Assert.DoesNotContain("Second", vm.InstallStatus);
        Assert.Contains("重新启动", vm.InstallStatus);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task FailureShowsExitCodeAndOutputWithoutReportingSuccess()
    {
        var service = new FakeWinget { Respond = (_, _) => Task.FromResult(new WingetInstallResult { ExitCode = 5, ErrorCode = WingetErrorCodes.CommandFailed, Output = "Access denied" }) };
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        await vm.LoadDetailsAsync(new WingetPackage { Id = "Vendor.Tool" });
        await vm.InstallSelectedAsync();
        Assert.Contains("未能完成安装", vm.InstallStatus);
        Assert.Contains("0x00000005", vm.InstallStatus);
        Assert.Equal("Access denied", vm.InstallOutput);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task CancelStopsOwnedTaskAndDoesNotClaimRollbackOrSuccess()
    {
        var service = new FakeWinget { Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new WingetInstallResult(); } };
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        await vm.LoadDetailsAsync(new WingetPackage { Id = "Vendor.Tool" });
        var install = vm.InstallSelectedAsync();
        await vm.CancelInstallAsync();
        await install;
        Assert.False(vm.IsInstalling);
        Assert.Contains("可能已更改系统", vm.InstallStatus);
        Assert.DoesNotContain("已完成安装", vm.InstallStatus);
        Assert.True(vm.CanInstall);
    }

    private sealed class FakeWinget : IWingetService
    {
        public int Calls;
        public string? LastId;
        public Func<string, CancellationToken, Task<WingetInstallResult>> Respond = (_, _) => Task.FromResult(new WingetInstallResult { Succeeded = true, Output = "complete" });
        public string? LastProbeError => null;
        public Task<bool> ProbeAsync(CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyList<WingetPackage> ListFeatured() => [];
        public Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default) => Task.FromResult(new WingetSearchResult { Succeeded = true });
        public Task<WingetDetailsResult> GetDetailsAsync(string id, CancellationToken ct = default) => Task.FromResult(new WingetDetailsResult { Succeeded = true, Package = new WingetPackage { Id = id } });
        public Task<WingetInstallResult> InstallAsync(string id, CancellationToken ct = default) { Calls++; LastId = id; return Respond(id, ct); }
    }
}
