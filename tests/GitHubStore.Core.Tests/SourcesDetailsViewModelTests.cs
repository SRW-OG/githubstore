using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class SourcesDetailsViewModelTests
{
    [Fact]
    public async Task SelectingResultLoadsDetailsAndCachesSuccessfulQueries()
    {
        var service = new FakeWinget();
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        var package = new WingetPackage { Id = "7zip.7zip", Name = "7-Zip" };
        await vm.LoadDetailsAsync(package);
        Assert.Equal("Publisher", vm.DetailPublisher);
        Assert.Equal("Description", vm.DetailDescription);
        Assert.Equal("源站未提供", vm.DetailLicense);
        await vm.LoadDetailsAsync(package);
        Assert.Equal(1, service.Calls);
        Assert.False(vm.IsDetailsBusy);
    }

    [Fact]
    public async Task OlderResponseCannotOverwriteNewSelectionEvenIfServiceIgnoresCancellation()
    {
        var first = new TaskCompletionSource<WingetDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeWinget { Respond = (id, _) => id == "First.Tool" ? first.Task : Task.FromResult(Success(id)) };
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        var old = vm.LoadDetailsAsync(new WingetPackage { Id = "First.Tool" });
        await vm.LoadDetailsAsync(new WingetPackage { Id = "Second.Tool" });
        first.SetResult(Success("First.Tool"));
        await old;
        Assert.Equal("Second.Tool", vm.PackageDetails?.Id);
        Assert.Equal("Second.Tool", vm.SelectedPackage?.Id);
        Assert.False(vm.IsDetailsBusy);
    }

    [Fact]
    public async Task FailedDetailsRetainSearchResultsAndRetryQueriesAgain()
    {
        var service = new FakeWinget { Respond = (_, _) => Task.FromResult(new WingetDetailsResult { ErrorCode = WingetErrorCodes.TimedOut }) };
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        var package = Assert.Single(vm.Packages);
        await vm.LoadDetailsAsync(package);
        Assert.True(vm.HasDetailsError);
        Assert.False(vm.HasPackageDetails);
        Assert.Single(vm.Packages);
        service.Respond = (id, _) => Task.FromResult(Success(id));
        await vm.LoadDetailsAsync(package);
        Assert.True(vm.HasPackageDetails);
        Assert.False(vm.HasDetailsError);
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task ClearingSelectionDiscardsPendingResponse()
    {
        var pending = new TaskCompletionSource<WingetDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeWinget { Respond = (_, _) => pending.Task };
        using var vm = new SourcesViewModel(service, new LocalizationService("zh-CN"));
        var task = vm.LoadDetailsAsync(new WingetPackage { Id = "First.Tool" });
        await vm.LoadDetailsAsync(null);
        pending.SetResult(Success("First.Tool"));
        await task;
        Assert.False(vm.HasSelectedPackage);
        Assert.Null(vm.PackageDetails);
    }

    private static WingetDetailsResult Success(string id) => new()
    {
        Succeeded = true,
        Package = new WingetPackage { Id = id, Name = "Tool", Publisher = "Publisher", Description = "Description" }
    };

    private sealed class FakeWinget : IWingetService
    {
        public int Calls;
        public Func<string, CancellationToken, Task<WingetDetailsResult>> Respond = (id, _) => Task.FromResult(Success(id));
        public string? LastProbeError => null;
        public Task<bool> ProbeAsync(CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyList<WingetPackage> ListFeatured() => [new() { Id = "Fixture.Tool", IsStatic = true }];
        public Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default) => Task.FromResult(new WingetSearchResult { Succeeded = true });
        public Task<WingetDetailsResult> GetDetailsAsync(string id, CancellationToken ct = default) { Calls++; return Respond(id, ct); }
    }
}
