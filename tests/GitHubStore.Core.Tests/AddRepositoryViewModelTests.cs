using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class AddRepositoryViewModelTests
{
    private static async Task<CatalogViewModel> CatalogAsync()
    {
        var catalog = new CatalogViewModel(new MockCatalogService(), new MockCatalogService(), new LocalizationService("zh-CN"));
        await catalog.InitializeAsync();
        return catalog;
    }

    [Fact]
    public async Task Constructor_PopulatesLocalizedCategoriesAndDefaultsToSystem()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var viewModel = new AddRepositoryViewModel(fake, catalog);

        Assert.Equal(12, viewModel.Categories.Count);
        Assert.Equal("系统工具", viewModel.SelectedCategory!.Name);
        Assert.Empty(viewModel.ErrorMessage);
        Assert.False(viewModel.CanAdd);
    }

    [Fact]
    public async Task EditingTheInput_ClearsAnyPreviousPreview()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var viewModel = new AddRepositoryViewModel(fake, catalog)
        {
            RepositoryInput = "fixture/ToolBox",
        };
        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();
        Assert.True(viewModel.CanAdd);

        viewModel.RepositoryInput += "X";

        Assert.Null(viewModel.PreviewDetail);
        Assert.False(viewModel.CanAdd);
        Assert.Empty(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Add_WithoutPreview_ReportsPreviewFirstAndNeverCallsTheService()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var viewModel = new AddRepositoryViewModel(fake, catalog)
        {
            RepositoryInput = "fixture/ToolBox",
        };

        var result = await viewModel.AddAsync();

        Assert.Null(result);
        Assert.Equal(viewModel.Text["WizardReviewFirst"], viewModel.ErrorMessage);
        Assert.Equal(0, fake.AddCalls);
        Assert.Equal(0, fake.PreviewCalls);
    }

    [Fact]
    public async Task Preview_OnSuccess_EnablesAddAndForwardsTheTrimmedInput()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var viewModel = new AddRepositoryViewModel(fake, catalog)
        {
            RepositoryInput = "  fixture/ToolBox  ",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();

        Assert.Equal(new[] { "fixture/ToolBox" }, fake.PreviewedInputs);
        Assert.NotNull(viewModel.PreviewDetail);
        Assert.True(viewModel.CanAdd);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsEditable);
        Assert.Empty(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Preview_OnFailure_ReportsLocalizedReasonAndKeepsAddDisabled()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService { PreviewError = new RepositoryCatalogException("repository_not_found") };
        using var viewModel = new AddRepositoryViewModel(fake, catalog)
        {
            RepositoryInput = "fixture/missing",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();

        Assert.Null(viewModel.PreviewDetail);
        Assert.False(viewModel.CanAdd);
        Assert.False(viewModel.IsBusy);
        Assert.Contains(viewModel.Text["RepositoryNotFound"], viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_WithInvalidAddress_SurfacesInvalidRepositoryMessage()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService { PreviewError = new RepositoryCatalogException("invalid_repository") };
        using var viewModel = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "not a repo" };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();

        Assert.Contains(viewModel.Text["InvalidRepository"], viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CanAdd);
    }

    [Fact]
    public async Task Add_AfterPreview_PassesTheChosenCategoryAndReturnsTheDetail()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var viewModel = new AddRepositoryViewModel(fake, catalog)
        {
            RepositoryInput = "fixture/ToolBox",
        };
        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();
        viewModel.SelectedCategory = viewModel.Categories.First(category => category.Id.Value == "dev-tools");

        var added = await viewModel.AddAsync();

        Assert.NotNull(added);
        Assert.Equal(new CategoryId("dev-tools"), fake.LastCategory);
        Assert.Equal(1, fake.AddCalls);
        Assert.Empty(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Add_OnDuplicate_ReportsDuplicateMessageAndReturnsNull()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService { AddError = new RepositoryCatalogException("duplicate_repository") };
        using var viewModel = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();

        var added = await viewModel.AddAsync();

        Assert.Null(added);
        Assert.Contains(viewModel.Text["DuplicateRepository"], viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_AfterDispose_DoesNothing()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        var viewModel = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        viewModel.Dispose();
        viewModel.Dispose();

        await viewModel.PreviewCommand.ExecuteAsync(null);
        viewModel.SelectedAsset = viewModel.Assets.FirstOrDefault();

        Assert.Equal(0, fake.PreviewCalls);
    }

    [Fact]
    public async Task Preview_RequiresExplicitAsset_AndManualAddPersistsReviewFieldsWithoutAi()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.False(vm.CanAdd);
        Assert.False(vm.CanSuggest);
        vm.SelectedAsset = Assert.Single(vm.Assets);
        Assert.Equal("ToolBox-*-windows-portable.zip", vm.FilenamePattern);
        vm.Description = "Edited description";
        vm.ExecutableRelativePath = "bin/ToolBox.exe";
        vm.SelectedFormat = vm.Formats.Single(c => c.Value == PackageInstallFormat.PortableArchive);
        Assert.NotNull(await vm.AddAsync());
        Assert.Equal("Edited description", fake.LastDescription);
        Assert.Equal("ToolBox-1.2.3-windows-portable.zip", fake.ConfirmedName);
        Assert.Equal("v1.2.3", fake.ConfirmedTag);
        Assert.Equal("bin/ToolBox.exe", fake.LastPolicy!.ExecutableRelativePath);
        Assert.Equal("ToolBox-*-windows-portable.zip", Assert.Single(fake.LastPolicy.AssetRules).Include[0]);
    }

    [Fact]
    public async Task ChannelAndTagChanges_InvalidatePreview_AndRepreviewUsesConfiguredChannel()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        vm.SelectedChannel = vm.Channels.Single(c => c.Value == PackageReleaseChannel.Pinned);
        vm.PinnedTag = "v1.2.3";
        Assert.Null(vm.PreviewDetail);
        Assert.Empty(vm.Assets);
        Assert.False(vm.CanAdd);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(PackageReleaseChannel.Pinned, fake.LastPolicy!.ReleaseChannel);
        Assert.Equal("v1.2.3", fake.LastPolicy.PinnedTag);
    }

    [Fact]
    public async Task LatePreview_CannotRestoreChangedInputOrClosedDialog()
    {
        using var catalog = await CatalogAsync();
        var pending = new TaskCompletionSource<AppDetail>();
        var fake = new FakeRepositoryService { PendingPreview = pending };
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        var request = vm.PreviewCommand.ExecuteAsync(null);
        vm.RepositoryInput = "fixture/Other";
        pending.SetResult(FakeRepositoryService.Detail("fixture/ToolBox"));
        await request;
        Assert.Null(vm.PreviewDetail);
        Assert.False(vm.IsBusy);
        fake.PendingPreview = new TaskCompletionSource<AppDetail>();
        request = vm.PreviewCommand.ExecuteAsync(null);
        vm.EndDialog(); vm.BeginDialog();
        fake.PendingPreview.SetResult(FakeRepositoryService.Detail("fixture/Other"));
        await request;
        Assert.Null(vm.PreviewDetail);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ReopenedDialog_CanPreviewWhileCancelledServiceIgnoresCancellation()
    {
        using var catalog = await CatalogAsync();
        var oldPreview = new TaskCompletionSource<AppDetail>();
        var fake = new FakeRepositoryService { PendingPreview = oldPreview };
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/Old" };
        var oldRequest = vm.PreviewCommand.ExecuteAsync(null);
        vm.EndDialog(); vm.BeginDialog();
        Assert.True(vm.PreviewCommand.CanExecute(null));
        vm.RepositoryInput = "fixture/New";
        fake.PendingPreview = null;
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("New", vm.PreviewRepo);
        oldPreview.SetResult(FakeRepositoryService.Detail("fixture/Old"));
        await oldRequest;
        Assert.Equal("New", vm.PreviewRepo);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AiSuggestion_RequiresApply_AndSeparateAddConfirmation()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        var ai = new FakeSuggestions();
        using var vm = new AddRepositoryViewModel(fake, catalog, ai) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        await vm.SuggestCommand.ExecuteAsync(null);
        Assert.True(vm.HasSuggestion);
        Assert.Null(vm.SelectedAsset);
        Assert.Equal(0, fake.AddCalls);
        vm.ApplySuggestionCommand.Execute(null);
        Assert.Equal("Suggested", vm.Description);
        Assert.Equal("dev-tools", vm.SelectedCategory!.Id.Value);
        Assert.NotNull(vm.SelectedAsset);
        Assert.Equal(0, fake.AddCalls);
        Assert.NotNull(await vm.AddAsync());
    }

    [Fact]
    public async Task LateAiSuggestion_CannotOverwriteUserEdits_OrSelectUnknownAsset()
    {
        using var catalog = await CatalogAsync();
        var ai = new FakeSuggestions { Pending = new TaskCompletionSource<GitHubStore.App.Services.Ai.RepositorySuggestion>() };
        using var vm = new AddRepositoryViewModel(new FakeRepositoryService(), catalog, ai) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        var request = vm.SuggestCommand.ExecuteAsync(null);
        vm.Description = "Manual edit";
        ai.Pending.SetResult(new("asset-1", "dev-tools", "Late overwrite"));
        await request;
        Assert.False(vm.HasSuggestion);
        Assert.Equal("Manual edit", vm.Description);
        ai.Pending = null; ai.AssetId = "unknown";
        await vm.SuggestCommand.ExecuteAsync(null);
        Assert.False(vm.HasSuggestion);
        Assert.Null(vm.SelectedAsset);
        Assert.Equal(vm.Text["WizardAiFailed"], vm.ErrorMessage);
    }

    [Fact]
    public async Task NoRelease_RequiresExplicitCatalogOnlyConsent_AndPreservesEditableDescription()
    {
        using var catalog = await CatalogAsync();
        var detail = FakeRepositoryService.Detail("fixture/ToolBox") with { Releases = [] };
        var pending = new TaskCompletionSource<AppDetail>(); pending.SetResult(detail);
        var fake = new FakeRepositoryService { PendingPreview = pending };
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.CanChooseCatalogOnly);
        Assert.False(vm.CanAdd);
        vm.CatalogOnly = true;
        vm.Description = "Track future releases";
        Assert.True(vm.CanAdd);
        Assert.NotNull(await vm.AddAsync());
        Assert.Null(fake.ConfirmedName);
        Assert.Null(fake.ConfirmedTag);
        Assert.Equal("Track future releases", fake.LastDescription);
        vm.RepositoryInput = "fixture/Other";
        Assert.False(vm.CatalogOnly);
        Assert.False(vm.CanAdd);
    }

    [Fact]
    public async Task CatalogOnlyFlag_CannotBypassAnAvailablePackageSelection()
    {
        using var catalog = await CatalogAsync();
        var fake = new FakeRepositoryService();
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.False(vm.CanChooseCatalogOnly);
        vm.CatalogOnly = true;
        Assert.False(vm.CanAdd);
        Assert.Null(await vm.AddAsync());
        Assert.Equal(0, fake.AddCalls);
    }

    [Fact]
    public async Task Preview_ExcludesUnsafeAndNonWindowsFiles_AndSavesInferredArchitecture()
    {
        using var catalog = await CatalogAsync();
        var detail = FakeRepositoryService.Detail("fixture/ToolBox");
        var release = detail.Releases[0];
        var asset = release.Assets[0];
        detail = detail with { Releases = [release with { Assets = [
            asset with { Name = "RustDesk-x86_64.exe", Arch = Arch.X86, Kind = AssetKind.InstallerExe },
            asset with { InstallerId = "linux", Name = "tool-linux-x64.zip" },
            asset with { InstallerId = "unsafe", Name = "../tool-windows.zip" },
            asset with { InstallerId = "reserved", Name = "CON.exe" },
        ] }] };
        var pending = new TaskCompletionSource<AppDetail>(); pending.SetResult(detail);
        var fake = new FakeRepositoryService { PendingPreview = pending };
        using var vm = new AddRepositoryViewModel(fake, catalog) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            vm.SelectedAsset = Assert.Single(vm.Assets);
            Assert.NotNull(await vm.AddAsync());
            Assert.Equal(Arch.Amd64, Assert.Single(fake.LastPolicy!.AssetRules).Architecture);
        }
        else Assert.DoesNotContain(vm.Assets, a => a.InstallerId is "linux" or "unsafe" or "reserved");
    }

    [Fact]
    public async Task EndDialog_ContainsThrowingCancellationCallbacks()
    {
        using var catalog = await CatalogAsync();
        var ai = new FakeSuggestions { Pending = new TaskCompletionSource<GitHubStore.App.Services.Ai.RepositorySuggestion>(), ThrowOnCancel = true };
        using var vm = new AddRepositoryViewModel(new FakeRepositoryService(), catalog, ai) { RepositoryInput = "fixture/ToolBox" };
        await vm.PreviewCommand.ExecuteAsync(null);
        var request = vm.SuggestCommand.ExecuteAsync(null);
        vm.EndDialog();
        vm.BeginDialog();
        ai.Pending.SetResult(new("asset-1", "system", "Late"));
        await request;
        Assert.False(vm.HasSuggestion);
        Assert.False(vm.IsBusy);
    }

    private sealed class FakeSuggestions : GitHubStore.App.Services.Ai.IRepositorySuggestionService
    {
        public bool IsConfigured => true;
        public string AssetId { get; set; } = "asset-1";
        public TaskCompletionSource<GitHubStore.App.Services.Ai.RepositorySuggestion>? Pending { get; set; }
        public bool ThrowOnCancel { get; set; }
        public Task<GitHubStore.App.Services.Ai.RepositorySuggestion> SuggestAsync(AppDetail detail, IReadOnlyList<Category> categories, string language, CancellationToken ct = default)
        {
            if (ThrowOnCancel) ct.Register(() => throw new InvalidOperationException("Fixture cancellation callback"));
            return Pending?.Task ?? Task.FromResult(new GitHubStore.App.Services.Ai.RepositorySuggestion(AssetId, "dev-tools", "Suggested"));
        }
    }

    private sealed class FakeRepositoryService : IRepositoryCatalogService, IPackagePolicyCatalogService
    {
        public PackagePolicy? LastPolicy { get; private set; }
        public string? LastDescription { get; private set; }
        public string? ConfirmedName { get; private set; }
        public string? ConfirmedTag { get; private set; }
        public Task<AppDetail> AddCatalogOnlyAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
            string? confirmedReleaseTag, CancellationToken ct = default)
        {
            LastPolicy = policy; LastDescription = description; ConfirmedTag = confirmedReleaseTag;
            return AddAsync(repository, category, ct);
        }
        public TaskCompletionSource<AppDetail>? PendingPreview { get; set; }
        public Task<AppDetail> UpdatePackagePolicyAsync(AppId id, PackagePolicy? policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDetail> PreviewAsync(string repository, PackagePolicy policy, CancellationToken ct = default)
        {
            LastPolicy = policy;
            return PreviewAsync(repository, ct);
        }
        public Task<AppDetail> AddAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
            string confirmedAssetName, string confirmedReleaseTag, CancellationToken ct = default)
        {
            LastPolicy = policy; LastDescription = description; ConfirmedName = confirmedAssetName; ConfirmedTag = confirmedReleaseTag;
            return AddAsync(repository, category, ct);
        }
        public int PreviewCalls { get; private set; }
        public int AddCalls { get; private set; }
        public List<string> PreviewedInputs { get; } = [];
        public CategoryId? LastCategory { get; private set; }
        public RepositoryCatalogException? PreviewError { get; init; }
        public RepositoryCatalogException? AddError { get; init; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<AppDetail> PreviewAsync(string repository, CancellationToken ct = default)
        {
            PreviewCalls++;
            PreviewedInputs.Add(repository);
            return PreviewError is not null
                ? Task.FromException<AppDetail>(PreviewError)
                : PendingPreview?.Task ?? Task.FromResult(Detail(repository));
        }

        public Task<AppDetail> AddAsync(string repository, CategoryId? category = null, CancellationToken ct = default)
        {
            AddCalls++;
            LastCategory = category;
            return AddError is not null
                ? Task.FromException<AppDetail>(AddError)
                : Task.FromResult(Detail(repository));
        }

        public Task RemoveAsync(AppId id, CancellationToken ct = default) => Task.CompletedTask;

        public static AppDetail Detail(string repository)
        {
            var address = RepositoryAddress.Parse(repository);
            return new AppDetail
            {
                Releases = [new Release
                {
                    Key = "release-1", AppId = new AppId(GitHubCatalogService.ManualSourceId, new PackageId(address.Key)),
                    TagName = "v1.2.3", Assets = [new Asset
                    {
                        InstallerId = "asset-1", ReleaseKey = "release-1", Name = "ToolBox-1.2.3-windows-portable.zip",
                        DownloadUrl = "https://github.com/fixture/ToolBox/releases/download/v1.2.3/ToolBox-1.2.3-windows-portable.zip",
                        Kind = AssetKind.ArchiveZip, Installer = InstallerKind.Zip,
                    }],
                }],
                App = new CatalogApp
                {
                    Id = new AppId(GitHubCatalogService.ManualSourceId, new PackageId(address.Key)),
                    SourceRevision = 1,
                    DisplayName = address.Repo,
                    Owner = address.Owner,
                    Repo = address.Repo,
                    Description = "Synthetic fixture repository used by offline tests.",
                    Stars = 1200,
                    License = "MIT",
                    PrimaryCategory = new CategoryId("system"),
                    Categories = [new CategoryId("system")],
                    FetchedAt = DateTimeOffset.UtcNow,
                },
            };
        }
    }
}
