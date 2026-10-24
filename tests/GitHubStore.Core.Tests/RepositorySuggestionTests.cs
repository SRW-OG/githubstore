using GitHubStore.App.Services.Ai;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class RepositorySuggestionTests
{
    private static readonly AppId Id = new(new SourceInstanceId(Guid.Empty), new PackageId("fixture/tool"));
    private static AppDetail Detail => new()
    {
        App = new CatalogApp { Id = Id, SourceRevision = 1, DisplayName = "Tool", Owner = "fixture", Repo = "tool", Description = "A tool" },
        Releases = [new Release { Key = "r1", AppId = Id, TagName = "v1", Assets = [new Asset
        { InstallerId = "actual-asset", ReleaseKey = "r1", Name = "tool-windows.zip", DownloadUrl = "https://github.com/fixture/tool/releases/download/v1/tool-windows.zip" }] }],
    };
    private static readonly Category[] Categories = [new Category { Id = new("system"), Name = "系统", NameEn = "System" }];

    [Theory]
    [InlineData("{\"assetId\":\"unknown\",\"categoryId\":\"system\",\"description\":\"Text\"}")]
    [InlineData("{\"assetId\":\"actual-asset\",\"categoryId\":\"unknown\",\"description\":\"Text\"}")]
    [InlineData("{\"assetId\":\"actual-asset\",\"categoryId\":\"system\",\"description\":\"Text\",\"command\":\"install anything\"}")]
    [InlineData("{\"assetId\":\"actual-asset\",\"categoryId\":\"system\"}")]
    [InlineData("not json")]
    public async Task RejectsUnknownIdentifiersMissingDescriptionAndExtraCommandFields(string response)
    {
        var client = new FakeClient { Response = response };
        var service = new RepositorySuggestionService(client, new FakeReadme());
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => service.SuggestAsync(Detail, Categories, "en-US"));
        Assert.Equal("ai_invalid_response", error.Code);
    }

    [Fact]
    public async Task ValidSuggestion_IsGroundedInActualReadmeAndAssetMetadata()
    {
        var client = new FakeClient();
        var service = new RepositorySuggestionService(client, new FakeReadme());
        var result = await service.SuggestAsync(Detail, Categories, "en-US");
        Assert.Equal("actual-asset", result.AssetId);
        Assert.Contains("README fixture facts", client.Request!.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("actual-asset", client.Request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("untrusted", client.Request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredAi_DoesNotFetchReadmeOrInvokeTransport()
    {
        var client = new FakeClient { IsConfigured = false };
        var readme = new FakeReadme();
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => new RepositorySuggestionService(client, readme).SuggestAsync(Detail, Categories, "en-US"));
        Assert.Equal("ai_not_configured", error.Code);
        Assert.Equal(0, readme.Calls);
        Assert.Null(client.Request);
    }

    [Fact]
    public async Task ExcessiveAssetList_DoesNotInvokeAiTransport()
    {
        var client = new FakeClient();
        var detail = Detail;
        detail = detail with { Releases = [detail.Releases[0] with { Assets = Enumerable.Repeat(detail.Releases[0].Assets[0], 201).ToArray() }] };
        await Assert.ThrowsAsync<AiSummaryException>(() => new RepositorySuggestionService(client, new FakeReadme()).SuggestAsync(detail, Categories, "en-US"));
        Assert.Null(client.Request);
    }

    private sealed class FakeClient : IAiClient
    {
        public bool IsConfigured { get; init; } = true;
        public string Response { get; init; } = "{\"assetId\":\"actual-asset\",\"categoryId\":\"system\",\"description\":\"A factual tool\"}";
        public AiClientRequest? Request { get; private set; }
        public Task<string> CompleteAsync(AiClientRequest request, CancellationToken ct = default) { Request = request; return Task.FromResult(Response); }
    }
    private sealed class FakeReadme : IReadmeSummaryService
    {
        public bool IsConfigured => true;
        public int Calls { get; private set; }
        public Task<ReadmeDocument> LoadReadmeAsync(CatalogApp app, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new ReadmeDocument("README fixture facts", "sha", new Uri("https://github.com/fixture/tool"), DateTimeOffset.UtcNow)); }
        public Task<AiReadmeSummary?> GetCachedAsync(CatalogApp app, string language, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AiReadmeSummary> GenerateAsync(CatalogApp app, ReadmeDocument readme, string language, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
