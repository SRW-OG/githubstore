using GitHubStore.App.Services.Ai;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

public sealed class CatalogChineseReadmeTests
{
    private static readonly Uri Source = new("https://github.com/owner/tool/blob/main/README.md");
    [Fact]
    public async Task TranslatedCacheDisplaysImmediatelyWithoutGeneration()
    {
        var service = new FakeChinese { Cached = new("# 中文正文", "# Original", Source, FromCache: true) };
        using var vm = Create(service);
        await vm.LoadChineseReadmeForSelectedAsync();
        Assert.Equal("# 中文正文", vm.DisplayReadmeMarkdown);
        Assert.Equal(0, service.LoadCalls);
        vm.ShowOriginalReadme = true;
        Assert.Equal("# Original", vm.DisplayReadmeMarkdown);
        Assert.False(vm.IsChineseReadmeBusy);
    }

    [Fact]
    public async Task OriginalOnlyCacheContinuesConfiguredTranslation()
    {
        var service = new FakeChinese { Cached = new("Original", "Original", Source, IsTranslated: false),
            Handler = (_, _) => Task.FromResult(new ChineseReadmeDocument("真实译文", "Original", Source)) };
        using var vm = Create(service);
        await vm.LoadChineseReadmeForSelectedAsync();
        Assert.Equal(1, service.LoadCalls);
        Assert.Equal("真实译文", vm.DisplayReadmeMarkdown);
        Assert.False(vm.ShowOriginalReadme);
    }

    [Fact]
    public async Task SelectionChangeCancelsAndRejectsOldTranslation()
    {
        var pending = new TaskCompletionSource<ChineseReadmeDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeChinese { Handler = (_, _) => pending.Task };
        using var vm = Create(service);
        var load = vm.LoadChineseReadmeForSelectedAsync();
        Assert.True(vm.IsChineseReadmeBusy);
        vm.SelectedDetail = new AppDetail { App = App("other") };
        Assert.True(service.Token.IsCancellationRequested);
        pending.SetResult(new("Old translation", "Old original", Source));
        await load;
        Assert.Empty(vm.DisplayReadmeMarkdown);
        Assert.False(vm.IsChineseReadmeBusy);
    }

    [Fact]
    public async Task ClosingAndRegeneratingDoesNotWaitForIgnoredCancellation()
    {
        var pending = new TaskCompletionSource<ChineseReadmeDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeChinese { Handler = (force, _) => force
            ? Task.FromResult(new ChineseReadmeDocument("New translation", "Original", Source)) : pending.Task };
        using var vm = Create(service);
        var old = vm.LoadChineseReadmeForSelectedAsync();
        vm.CancelChineseReadmeCommand.Execute(null);
        Assert.False(vm.IsChineseReadmeBusy);
        Assert.True(service.Token.IsCancellationRequested);
        await vm.GenerateChineseReadmeCommand.ExecuteAsync(null);
        pending.SetResult(new("Old translation", "Original", Source));
        await old;
        Assert.Equal("New translation", vm.DisplayReadmeMarkdown);
    }

    [Fact]
    public async Task UnconfiguredCacheShowsOriginalWithoutPretendingItIsChinese()
    {
        var service = new FakeChinese { IsConfigured = false,
            Cached = new("Original English README", "Original English README", Source, IsTranslated: false) };
        using var vm = Create(service);
        await vm.LoadChineseReadmeForSelectedAsync();
        Assert.True(vm.ShowOriginalReadme);
        Assert.False(vm.HasChineseReadme);
        Assert.Equal("Original English README", vm.DisplayReadmeMarkdown);
        Assert.Equal(0, service.LoadCalls);
    }

    [Fact]
    public async Task ProviderFailureDoesNotExposeDetailsAndCanRetry()
    {
        var service = new FakeChinese { Handler = (_, _) => throw new IOException("Secret provider text C:/private/token") };
        using var vm = Create(service);
        await vm.LoadChineseReadmeForSelectedAsync();
        Assert.NotEmpty(vm.ChineseReadmeError);
        Assert.DoesNotContain("Secret", vm.ChineseReadmeError);
        Assert.False(vm.IsChineseReadmeBusy);
        service.Handler = (_, _) => Task.FromResult(new ChineseReadmeDocument("Retry", "Original", Source));
        await vm.LoadChineseReadmeForSelectedAsync(true);
        Assert.Empty(vm.ChineseReadmeError);
        Assert.Equal("Retry", vm.DisplayReadmeMarkdown);
    }

    private static CatalogViewModel Create(FakeChinese service)
    {
        var catalog = new MockCatalogService();
        var vm = new CatalogViewModel(catalog, catalog, chineseReadme: service);
        vm.SelectedDetail = new AppDetail { App = App("tool") };
        return vm;
    }
    private static CatalogApp App(string repo) => new()
    {
        Id = new(new(Guid.NewGuid()), new("owner/" + repo)), SourceRevision = 1,
        Owner = "owner", Repo = repo, DisplayName = repo,
    };
    private sealed class FakeChinese : IChineseReadmeService
    {
        public bool IsConfigured { get; init; } = true;
        public ChineseReadmeDocument? Cached { get; init; }
        public int LoadCalls { get; private set; }
        public CancellationToken Token { get; private set; }
        public Func<bool, CancellationToken, Task<ChineseReadmeDocument>>? Handler { get; set; }
        public Task<ChineseReadmeDocument?> GetCachedAsync(CatalogApp app, CancellationToken ct = default) => Task.FromResult(Cached);
        public Task<ChineseReadmeDocument> LoadAsync(CatalogApp app, bool force = false,
            IProgress<ChineseReadmeProgress>? progress = null, CancellationToken ct = default)
        {
            LoadCalls++;
            Token = ct;
            return Handler?.Invoke(force, ct) ?? Task.FromResult(new ChineseReadmeDocument("中文", "Original", Source));
        }
    }
}
