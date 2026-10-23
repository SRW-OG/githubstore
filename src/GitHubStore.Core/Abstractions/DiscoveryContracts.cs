namespace GitHubStore.Core.Abstractions;

public enum DiscoveryFeedKind { TrendshiftDaily, StarHistoryWeekly }

/// <summary>A project and metrics published by a named discovery source. Not installation approval.</summary>
public sealed record DiscoveryEntry
{
    public required string RepositoryFullName { get; init; }
    public required Uri RepositoryUri { get; init; }
    public required Uri SourceUri { get; init; }
    public int Rank { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? Language { get; init; }
    public long? TotalStars { get; init; }
    public long? StarsGained { get; init; }
    public decimal? TrendScore { get; init; }
    /// <summary>Only text actually presented as a review by the source; a project description is not a review.</summary>
    public string? SourceReview { get; init; }
    public string? ReviewAttribution { get; init; }
    public Uri? ReviewUri { get; init; }
    public DateTimeOffset? ReviewPublishedAt { get; init; }
}

public sealed record DiscoveryFeedResult
{
    public required DiscoveryFeedKind Kind { get; init; }
    public required string SourceName { get; init; }
    public required Uri SourceUri { get; init; }
    public IReadOnlyList<DiscoveryEntry> Entries { get; init; } = [];
    /// <summary>Period as published by the source, never inferred from the local fetch time.</summary>
    public string? PeriodLabel { get; init; }
    public DateOnly? PeriodStart { get; init; }
    public DateOnly? PeriodEnd { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public bool FromCache { get; init; }
    public bool IsStale { get; init; }
    public string? ErrorCode { get; init; }
}

public interface IDiscoveryFeedService
{
    Task<DiscoveryFeedResult> GetAsync(DiscoveryFeedKind kind, bool refresh = false, CancellationToken ct = default);
}

/// <summary>Explicit AI commentary; never presented as an upstream rating or a community review.</summary>
public interface IDiscoveryReviewService
{
    bool IsConfigured { get; }
    Task<string> ReviewAsync(DiscoveryFeedKind kind, DiscoveryEntry entry, string language, CancellationToken ct = default);
}
