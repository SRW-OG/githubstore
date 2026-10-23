using GitHubStore.App.Services.Ai;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

public sealed class CatalogAiTests
{
    [Fact]
    public async Task SelectionRestoresSummaryOfflineAndDoesNotFetchOrGenerate()
    {
        var catalog = new MockCatalogService();
        var fake = new SummaryStub { Cached = new AiReadmeSummary("# Cached", "model", "zh-CN", "sha", DateTimeOffset.UtcNow, true) };
        using var vm = new CatalogViewModel(catalog, catalog, readmeSummary: fake);
        var app = (await catalog.BrowseAsync(new CatalogQuery { Limit = 2 })).Items[0];
        await vm.SelectAsync(app);
        Assert.True(vm.HasAiSummary);
        Assert.Contains("Cached", vm.AiSummaryMarkdown);
        Assert.Equal(0, fake.Fetches);
        Assert.Equal(0, fake.Generations);
    }

    [Fact]
    public async Task GenerateLoadsReadmeDisplaysMarkdownAndRegenerateIsExplicit()
    {
        var catalog = new MockCatalogService();
        var fake = new SummaryStub();
        using var vm = new CatalogViewModel(catalog, catalog, readmeSummary: fake);
        await vm.SelectAsync((await catalog.BrowseAsync(new CatalogQuery { Limit = 1 })).Items[0]);
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.Equal("# README", vm.ReadmeMarkdown);
        Assert.Equal("# Generated", vm.AiSummaryMarkdown);
        Assert.False(vm.IsAiBusy);
        Assert.Equal(1, fake.Fetches);
        Assert.False(fake.Force);
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.True(fake.Force);
        Assert.Equal(1, fake.Fetches);
        Assert.Equal(2, fake.Generations);
    }

    [Fact]
    public async Task DelayedGenerationCannotReplaceDifferentSelectedRepository()
    {
        var catalog = new MockCatalogService();
        var pending = new TaskCompletionSource<AiReadmeSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new SummaryStub
        {
            Generate = _ => { entered.SetResult(); return pending.Task; },
        };
        using var vm = new CatalogViewModel(catalog, catalog, readmeSummary: fake);
        var apps = (await catalog.BrowseAsync(new CatalogQuery { Limit = 2 })).Items;
        await vm.SelectAsync(apps[0]);
        var generating = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.SelectAsync(apps[1]);
        pending.SetResult(new AiReadmeSummary("Stale old result", "model", "zh-CN", "sha", DateTimeOffset.UtcNow));
        await generating;
        Assert.Equal(apps[1].Id, vm.SelectedDetail!.App.Id);
        Assert.False(vm.HasAiSummary);
        Assert.Empty(vm.ReadmeMarkdown);
        Assert.False(vm.IsAiBusy);
    }

    [Fact]
    public async Task CancellationLeavesNoErrorAndModelErrorsDoNotLeakSensitiveDetails()
    {
        var catalog = new MockCatalogService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new SummaryStub
        {
            Generate = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); },
        };
        using var vm = new CatalogViewModel(catalog, catalog, readmeSummary: fake);
        await vm.SelectAsync((await catalog.BrowseAsync(new CatalogQuery { Limit = 1 })).Items[0]);
        var pending = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelAiCommand.Execute(null);
        await pending;
        Assert.False(vm.IsAiBusy);
        Assert.Empty(vm.AiError);
        fake.Generate = _ => throw new HttpRequestException("secret-api-key should never be shown");
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.AiError);
        Assert.DoesNotContain("secret-api-key", vm.AiError);
        Assert.False(vm.HasAiSummary);
    }

    [Fact]
    public async Task UnconfiguredGenerationReportsImmediatelyWithoutFetchingReadme()
    {
        var catalog = new MockCatalogService();
        var fake = new SummaryStub { IsConfigured = false };
        using var vm = new CatalogViewModel(catalog, catalog, readmeSummary: fake);
        await vm.SelectAsync((await catalog.BrowseAsync(new CatalogQuery { Limit = 1 })).Items[0]);
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.AiError);
        Assert.Equal(0, fake.Fetches);
        Assert.Equal(0, fake.Generations);
        await vm.LoadReadmeCommand.ExecuteAsync(null);
        Assert.Equal(1, fake.Fetches);
    }

    private sealed class SummaryStub : IReadmeSummaryService
    {
        public bool IsConfigured { get; set; } = true;
        public int Fetches;
        public int Generations;
        public bool Force;
        public AiReadmeSummary? Cached;
        public Func<CancellationToken, Task<AiReadmeSummary>>? Generate;
        public Task<ReadmeDocument> LoadReadmeAsync(CatalogApp app, CancellationToken ct = default)
        {
            Fetches++;
            return Task.FromResult(new ReadmeDocument("# README", "sha", new Uri("https://github.com/fixture/tool"), DateTimeOffset.UtcNow));
        }
        public Task<AiReadmeSummary?> GetCachedAsync(CatalogApp app, string language, CancellationToken ct = default) => Task.FromResult(Cached);
        public Task<AiReadmeSummary> GenerateAsync(CatalogApp app, ReadmeDocument readme, string language, bool force = false, CancellationToken ct = default)
        {
            Generations++;
            Force = force;
            return Generate?.Invoke(ct) ?? Task.FromResult(new AiReadmeSummary("# Generated", "model", language, readme.Sha, DateTimeOffset.UtcNow));
        }
    }
}
