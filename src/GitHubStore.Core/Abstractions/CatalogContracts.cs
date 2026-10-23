namespace GitHubStore.Core.Abstractions;

public interface ICatalogService
{
    event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated;
    Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default);
    Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default);
    Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default);
    Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default);
}

public interface ISearchService
{
    Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default);
}

public enum SortKey { Stars, Trending, Updated, Name }
public enum BoardKind { Stars, Trending, New }

public sealed record CatalogQuery
{
    public CategoryId? Category { get; init; }
    public SortKey Sort { get; init; } = SortKey.Stars;
    public int Offset { get; init; }
    public int Limit { get; init; } = 60;
    public bool IncludeArchived { get; init; }
}

public sealed record CatalogPage
{
    public IReadOnlyList<App> Items { get; init; } = [];
    public int Total { get; init; }
    public bool HasMore { get; init; }
}

public sealed record AppDetail
{
    public required App App { get; init; }
    public IReadOnlyList<Release> Releases { get; init; } = [];
    public Capability? Capability { get; init; }
    public ReadmeSummary? Summary { get; init; }
    public AppCuration? Curation { get; init; }
    public InstallRecord? Install { get; init; }
    public ReadmeMeta ReadmeMeta { get; init; } = new();
    public IReadOnlyList<string> Screenshots { get; init; } = [];
}

/// <summary>Editorial recommendations and repository identity checks; not install approval or signature verification.</summary>
public sealed record AppCuration
{
    public string? DescriptionZh { get; init; }
    public IReadOnlyList<RecommendationSource> RecommendationSources { get; init; } = [];
    public required RepositoryVerification Verification { get; init; }
}

public sealed record RecommendationSource
{
    public required string Platform { get; init; }
    public required string Title { get; init; }
    public required Uri Url { get; init; }
    public DateTimeOffset DiscoveredAt { get; init; }
    public required string EvidenceLevel { get; init; }
    public string? Quote { get; init; }
}

public sealed record RepositoryVerification
{
    public DateTimeOffset VerifiedAt { get; init; }
    public required Uri RepositoryUrl { get; init; }
    public required string Scope { get; init; }
    public IReadOnlyList<Uri> EvidenceUrls { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record SearchOptions
{
    public CategoryId? Category { get; init; }
    public string? Language { get; init; }
    public int MinStars { get; init; }
    public bool IncludeRemote { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed record SearchResult
{
    public IReadOnlyList<App> Items { get; init; } = [];
    public int Total { get; init; }
    public string Source { get; init; } = "local";
    public TimeSpan Took { get; init; }
    public string? RemoteError { get; init; }
}

// Initial skeleton contracts, documented in docs/01 section 4.
// This stage supports a full in-memory refresh only.
public enum RefreshScope { All }

public sealed record RefreshProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string? Message { get; init; }
}

public sealed record ReadmeMeta
{
    public string? Sha { get; init; }
    public string? Path { get; init; }
    public string? HtmlUrl { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
}

public sealed class CatalogUpdatedEventArgs : EventArgs
{
    public required int Count { get; init; }
    public required DateTimeOffset At { get; init; }
}
