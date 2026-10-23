using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Ai;

public sealed record ReadmeDocument(string Markdown, string Sha, Uri SourceUrl, DateTimeOffset FetchedAt);
public sealed record AiReadmeSummary(string Markdown, string Model, string Language, string ReadmeSha,
    DateTimeOffset CreatedAt, bool FromCache = false, bool InputTruncated = false);

public interface IReadmeSummaryService
{
    bool IsConfigured { get; }
    Task<ReadmeDocument> LoadReadmeAsync(GitHubStore.Core.Abstractions.App app, CancellationToken ct = default);
    Task<AiReadmeSummary?> GetCachedAsync(GitHubStore.Core.Abstractions.App app, string language, CancellationToken ct = default);
    Task<AiReadmeSummary> GenerateAsync(GitHubStore.Core.Abstractions.App app, ReadmeDocument readme,
        string language, bool force = false, CancellationToken ct = default);
}

/// <summary>Stable, non-sensitive error codes. Remote response bodies and credentials never reach UI errors.</summary>
public sealed class AiSummaryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
