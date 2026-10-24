using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LibraryPolicyEditorTests
{
    [Fact]
    public async Task EditRequiresPreviewAndInvalidatesAfterAnyChange()
    {
        var service = new PolicyService(await EntryAsync());
        using var vm = Create(service);
        Assert.False(vm.CanSave);
        vm.PreviewPolicy();
        Assert.True(vm.CanSave);
        vm.X64Include = "*.zip";
        Assert.False(vm.CanSave);
        await vm.SavePolicyAsync();
        Assert.Equal(0, service.Saves);
        vm.PreviewPolicy();
        Assert.True(vm.CanSave);
        await vm.SavePolicyAsync();
        Assert.Equal(1, service.Saves);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task AllArchitectureRulesAndPreservationSurviveAnUnrelatedEdit()
    {
        var original = await EntryAsync();
        var policy = new PackagePolicy
        {
            ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "v2",
            InstallFormat = PackageInstallFormat.PortableArchive, ExecutableRelativePath = "bin/app.exe",
            PreservePaths = ["config", "settings.json"],
            AssetRules = Enum.GetValues<Arch>().Select(a => new PackageAssetRule { Architecture = a, Include = ["product-*.zip"], Exclude = ["*debug*"] }).ToArray(),
        };
        var service = new PolicyService(original with { PackagePolicy = policy });
        using var vm = Create(service);
        vm.ExecutablePath = "bin/main.exe";
        vm.PreviewPolicy();
        await vm.SavePolicyAsync();
        Assert.Equal(1, service.Saves);
        Assert.Equal("bin/main.exe", service.Saved!.ExecutableRelativePath);
        Assert.Equal("v2", service.Saved.PinnedTag);
        Assert.Equal(policy.PreservePaths, service.Saved.PreservePaths);
        foreach (var rule in policy.AssetRules)
        {
            var saved = Assert.Single(service.Saved.AssetRules, r => r.Architecture == rule.Architecture);
            Assert.Equal(rule.Include, saved.Include);
            Assert.Equal(rule.Exclude, saved.Exclude);
        }
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:\\outside.exe")]
    [InlineData("bin/CON.exe")]
    public async Task UnsafePathsCannotBeSaved(string path)
    {
        var service = new PolicyService(await EntryAsync());
        using var vm = Create(service);
        vm.ExecutablePath = path;
        vm.PreviewPolicy();
        Assert.False(vm.CanSave);
        await vm.SavePolicyAsync();
        Assert.Equal(0, service.Saves);
        Assert.NotEmpty(vm.Status);
    }

    [Fact]
    public async Task FailedSavePreservesDraftAndDoesNotClaimSuccess()
    {
        var service = new PolicyService(await EntryAsync()) { Fail = true };
        using var vm = Create(service);
        vm.X64Exclude = "*debug*";
        vm.PreviewPolicy();
        await vm.SavePolicyAsync();
        Assert.Equal("*debug*", vm.X64Exclude);
        Assert.Equal("Could not save rules. Check the network, channel and directory permissions.", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SwitchingRepositoryInvalidatesPreview()
    {
        var entry = await EntryAsync();
        var service = new PolicyService(entry);
        using var vm = Create(service);
        vm.PreviewPolicy();
        Assert.True(vm.CanSave);
        vm.SelectedEntry = null;
        Assert.False(vm.CanSave);
        Assert.False(vm.CanEdit);
        await vm.SavePolicyAsync();
        Assert.Equal(0, service.Saves);
    }

    [Fact]
    public async Task DisposePreventsSaving()
    {
        var service = new PolicyService(await EntryAsync());
        var vm = Create(service);
        vm.PreviewPolicy();
        vm.Dispose();
        await vm.SavePolicyAsync();
        Assert.Equal(0, service.Saves);
    }

    [Fact]
    public async Task EmptyArchitectureRuleRemainsPresentAfterUnrelatedEdit()
    {
        var entry = await EntryAsync();
        var service = new PolicyService(entry with
        {
            PackagePolicy = new PackagePolicy
            {
                AssetRules = [new PackageAssetRule { Architecture = Arch.Amd64 },
                    new PackageAssetRule { Architecture = Arch.Arm64, Include = ["*.zip"] }],
            },
        });
        using var vm = Create(service);
        Assert.True(vm.UseX64Rule);
        Assert.Empty(vm.X64Include);
        vm.ExecutablePath = "bin/main.exe";
        vm.PreviewPolicy();
        await vm.SavePolicyAsync();
        var x64 = Assert.Single(service.Saved!.AssetRules, rule => rule.Architecture == Arch.Amd64);
        Assert.Empty(x64.Include);
        Assert.Empty(x64.Exclude);
        Assert.Equal(2, service.Saved.AssetRules.Count);
    }

    [Fact]
    public async Task ChangingLanguagePreservesDraftAndPreview()
    {
        var service = new PolicyService(await EntryAsync());
        using var vm = Create(service);
        vm.X64Include = "*.zip";
        vm.PreviewPolicy();
        Assert.True(vm.CanSave);
        var channel = vm.Channel!.Value;
        vm.Text.ApplyLanguage("zh-CN");
        Assert.Equal(channel, vm.Channel!.Value);
        Assert.Equal(vm.Text["WizardChannelStable"], vm.Channels[0].Name);
        Assert.Equal("*.zip", vm.X64Include);
        Assert.True(vm.CanSave);
    }

    private static LibraryPolicyEditorViewModel Create(PolicyService service)
    {
        var vm = new LibraryPolicyEditorViewModel(service, new LocalizationService("en-US"));
        vm.SetEntries([service.Entry]);
        return vm;
    }

    private static async Task<AppDetail> EntryAsync()
    {
        using var stream = typeof(GitHubCatalogService).Assembly.GetManifestResourceStream(CuratedCatalogLoader.ResourceName)!;
        return (await CuratedCatalogLoader.LoadAsync(stream))[0];
    }

    private sealed class PolicyService(AppDetail entry) : IPackagePolicyCatalogService
    {
        public AppDetail Entry { get; } = entry;
        public int Saves { get; private set; }
        public PackagePolicy? Saved { get; private set; }
        public bool Fail { get; init; }
        public Task<AppDetail> UpdatePackagePolicyAsync(AppId id, PackagePolicy? policy, CancellationToken ct = default)
        {
            Saves++;
            if (Fail) throw new RepositoryCatalogException("network_error");
            Assert.Equal(Entry.App.Id, id);
            Saved = policy;
            return Task.FromResult(Entry with { PackagePolicy = policy });
        }
        public Task<AppDetail> PreviewAsync(string repository, PackagePolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> AddAsync(string repository, CategoryId? category, PackagePolicy policy, string? description, string confirmedAssetName, string confirmedReleaseTag, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> AddCatalogOnlyAsync(string repository, CategoryId? category, PackagePolicy policy, string? description, string? confirmedReleaseTag, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
