using System.Text.Json;
using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Discovery;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class DiscoveryReviewTests
{
    private static DiscoveryEntry Entry() => new()
    {
        RepositoryFullName = "fixture/tool", RepositoryUri = new("https://github.com/fixture/tool"),
        SourceUri = new("https://www.star-history.com/"), Rank = 2,
        Description = "A terminal document viewer", Language = "Rust", TotalStars = 12000, StarsGained = 420,
    };

    [Fact]
    public async Task CommentaryUsesActualDescriptionAndDistinctMetricFieldsWithGroundingLimits()
    {
        var client = new ClientStub();
        var service = new DiscoveryReviewService(client);
        var result = await service.ReviewAsync(DiscoveryFeedKind.StarHistoryWeekly, Entry(), "zh-CN");
        Assert.Equal("AI commentary", result);
        var request = Assert.Single(client.Requests);
        using var data = JsonDocument.Parse(request.UserPrompt);
        Assert.Equal(12000, data.RootElement.GetProperty("TotalStars").GetInt32());
        Assert.Equal(420, data.RootElement.GetProperty("StarsGained").GetInt32());
        Assert.Equal("A terminal document viewer", data.RootElement.GetProperty("description").GetString());
        Assert.Null(data.RootElement.GetProperty("sourceReview").GetString());
        Assert.Contains("Simplified Chinese", request.SystemPrompt);
        Assert.Contains("MUST NOT be called weekly growth", request.SystemPrompt);
        Assert.Contains("not a source rating or community opinion", request.SystemPrompt);
        Assert.Contains("security", request.SystemPrompt);
        Assert.Contains("Windows readiness", request.SystemPrompt);
        Assert.Contains("untrusted reference data", request.SystemPrompt);
    }

    [Fact]
    public async Task DoesNotCreateSourceReviewsFromProjectDescriptionsOrInventDailyGrowth()
    {
        var client = new ClientStub();
        var service = new DiscoveryReviewService(client);
        await service.ReviewAsync(DiscoveryFeedKind.TrendshiftDaily, Entry() with { StarsGained = null, TrendScore = 73.5m }, "en");
        var request = Assert.Single(client.Requests);
        using var data = JsonDocument.Parse(request.UserPrompt);
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("StarsGained").ValueKind);
        Assert.Equal(73.5m, data.RootElement.GetProperty("TrendScore").GetDecimal());
        Assert.Contains("not daily gained stars", request.SystemPrompt);
        Assert.Contains("English", request.SystemPrompt);
    }

    [Theory]
    [InlineData("https://github.com/other/repository", "fixture/tool", 1)]
    [InlineData("https://github.com/fixture/tool", "../bad", 1)]
    [InlineData("http://github.com/fixture/tool", "fixture/tool", 1)]
    [InlineData("https://github.com/fixture/tool", "fixture/tool", 0)]
    public async Task InvalidIdentityOrRankNeverCallsModel(string url, string name, int rank)
    {
        var client = new ClientStub();
        var service = new DiscoveryReviewService(client);
        var error = await Assert.ThrowsAsync<AiSummaryException>(() => service.ReviewAsync(DiscoveryFeedKind.StarHistoryWeekly,
            Entry() with { RepositoryUri = new(url), RepositoryFullName = name, Rank = rank }, "zh-CN"));
        Assert.Equal("ai_invalid_request", error.Code);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task UnconfiguredAndCancelledReviewsDoNotCallModel()
    {
        var client = new ClientStub { IsConfigured = false };
        var service = new DiscoveryReviewService(client);
        Assert.Equal("ai_not_configured", (await Assert.ThrowsAsync<AiSummaryException>(() => service.ReviewAsync(DiscoveryFeedKind.TrendshiftDaily, Entry(), "zh"))).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReviewAsync(DiscoveryFeedKind.TrendshiftDaily, Entry(), "zh", cancelled.Token));
        Assert.Empty(client.Requests);
    }

    private sealed class ClientStub : IAiClient
    {
        public bool IsConfigured { get; set; } = true;
        public List<AiClientRequest> Requests { get; } = [];
        public Task<string> CompleteAsync(AiClientRequest request, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult("AI commentary"); }
    }
}
