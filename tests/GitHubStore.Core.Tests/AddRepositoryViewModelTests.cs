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
        Assert.Equal(viewModel.Text["PreviewFirst"], viewModel.ErrorMessage);
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

        Assert.Equal(0, fake.PreviewCalls);
    }

    private sealed class FakeRepositoryService : IRepositoryCatalogService
    {
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
                : Task.FromResult(Detail(repository));
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

        private static AppDetail Detail(string repository)
        {
            var address = RepositoryAddress.Parse(repository);
            return new AppDetail
            {
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
