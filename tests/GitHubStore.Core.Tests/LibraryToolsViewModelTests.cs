using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LibraryToolsViewModelTests
{
    [Fact]
    public async Task InspectAndExportUseOneCompleteOfflineSnapshotEach()
    {
        using var directory = new TempDirectory();
        var service = await OfflineCatalog.CreateAsync();
        using var vm = new SettingsViewModel(new PreferencesService(directory.Combine("settings.json"), new Protector()),
            new LocalizationService("en-US"), catalog: service);
        await vm.InspectLibraryAsync();
        Assert.Equal(1, service.SnapshotReads);
        var destination = directory.Combine("backup.json");
        var unrelatedTemporary = destination + ".tmp";
        await File.WriteAllTextAsync(unrelatedTemporary, "unrelated");
        Assert.True(await vm.ExportLibraryAsync(destination));
        Assert.Equal(2, service.SnapshotReads);
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelatedTemporary));
        await using var stream = File.OpenRead(destination);
        var reloaded = await CatalogLibraryExporter.ImportLocalAsync(stream);
        Assert.Equal(service.Snapshot.Entries.Count, reloaded.Entries.Count);
        Assert.Equal(destination, vm.LibraryExportPath);
    }

    [Fact]
    public async Task FailedSnapshotDoesNotReplaceExistingFileOrClaimSuccess()
    {
        using var directory = new TempDirectory();
        var service = await OfflineCatalog.CreateAsync();
        service.Fail = true;
        using var vm = new SettingsViewModel(new PreferencesService(directory.Combine("settings.json"), new Protector()),
            new LocalizationService("en-US"), catalog: service);
        var destination = directory.Combine("backup.json");
        await File.WriteAllTextAsync(destination, "previous backup");
        Assert.False(await vm.ExportLibraryAsync(destination));
        Assert.Equal("previous backup", await File.ReadAllTextAsync(destination));
        Assert.NotEmpty(vm.LibraryToolError);
        Assert.Empty(vm.LibraryExportPath);
        Assert.False(vm.IsLibraryToolBusy);
    }

    [Fact]
    public async Task UnsupportedCatalogCannotSilentlyFallBackToNetworkEnumeration()
    {
        using var directory = new TempDirectory();
        using var vm = new SettingsViewModel(new PreferencesService(directory.Combine("settings.json"), new Protector()),
            new LocalizationService("en-US"));
        Assert.False(vm.CanRunLibraryTools);
        Assert.False(await vm.ExportLibraryAsync(directory.Combine("backup.json")));
        Assert.False(File.Exists(directory.Combine("backup.json")));
    }

    [Fact]
    public async Task FailedReinspectionInvalidatesPreviousCleanConclusion()
    {
        using var directory = new TempDirectory();
        var initial = await OfflineCatalog.CreateAsync();
        var service = new OfflineCatalog(initial.Snapshot with { Entries = [] });
        using var vm = new SettingsViewModel(new PreferencesService(directory.Combine("settings.json"), new Protector()),
            new LocalizationService("en-US"), catalog: service);
        await vm.InspectLibraryAsync();
        Assert.Equal(vm.Text["LibraryToolsNoProblems"], vm.LibraryToolStatus);
        Assert.DoesNotContain("LibraryToolsNoProblems", vm.LibraryToolStatus);
        service.Fail = true;
        await vm.InspectLibraryAsync();
        Assert.Equal(vm.Text["LibraryToolsIdle"], vm.LibraryToolStatus);
        Assert.NotEmpty(vm.LibraryToolError);
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class OfflineCatalog(LocalCatalogSnapshot snapshot) : ICatalogService, ILocalCatalogSnapshotService
    {
        public LocalCatalogSnapshot Snapshot { get; } = snapshot;
        public int SnapshotReads { get; private set; }
        public bool Fail { get; set; }
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public Task<LocalCatalogSnapshot> ReadLocalSnapshotAsync(CancellationToken ct = default)
        {
            SnapshotReads++;
            if (Fail) throw new RepositoryCatalogException("cache_read_failed");
            return Task.FromResult(Snapshot);
        }
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) => throw new InvalidOperationException("Must use snapshot.");
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) => throw new InvalidOperationException("Must not enumerate network-facing APIs.");
        public Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) => throw new InvalidOperationException("Must not fetch app details online.");
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public static async Task<OfflineCatalog> CreateAsync()
        {
            using var directory = new TempDirectory();
            var catalog = new GitHubCatalogService(new HttpClient(), directory.DirectoryPath, includeCuratedSeed: true);
            return new OfflineCatalog(await catalog.ReadLocalSnapshotAsync());
        }
    }
}
