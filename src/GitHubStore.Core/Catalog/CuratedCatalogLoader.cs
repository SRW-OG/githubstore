using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>Loads editorial metadata with public GitHub API snapshots, without network access.</summary>
public static class CuratedCatalogLoader
{
    public const string ResourceName = "GitHubStore.Core.CuratedCatalog.seed.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<IReadOnlyList<AppDetail>> LoadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || root.GetProperty("entries").ValueKind != JsonValueKind.Array)
                throw new JsonException("Invalid curated catalog schema.");
            var entries = new List<AppDetail>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in root.GetProperty("entries").EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var category = new CategoryId(value.GetProperty("categoryId").GetString()!);
                if (!GitHubCatalogService.IsKnownCategory(category)) throw new JsonException("Invalid category.");
                var fetchedAt = value.GetProperty("fetchedAt").GetDateTimeOffset();
                if (fetchedAt == default) throw new JsonException("Missing snapshot time.");
                var release = value.GetProperty("release");
                // JSON null is absence of a release, not a release payload containing the word null.
                var detail = GitHubResponseParser.Parse(value.GetProperty("repository").GetRawText(),
                    release.ValueKind == JsonValueKind.Null ? null : release.GetRawText(), fetchedAt, category, stale: true);
                var key = $"{detail.App.Owner}/{detail.App.Repo}";
                if (!keys.Add(key)) throw new JsonException("Duplicate repository.");
                var curation = value.GetProperty("curation").Deserialize<AppCuration>(Json)
                    ?? throw new JsonException("Missing curation.");
                ValidateCuration(curation, detail.App);
                PackagePolicy? policy = null;
                if (value.TryGetProperty("packagePolicy", out var packagePolicy) && packagePolicy.ValueKind != JsonValueKind.Null)
                {
                    policy = packagePolicy.Deserialize<PackagePolicy>(Json) ?? throw new JsonException("Missing package policy.");
                    try { PackagePolicyValidator.Validate(policy); }
                    catch (RepositoryCatalogException ex) { throw new JsonException("Invalid package policy.", ex); }
                }
                entries.Add(detail with { Curation = curation, PackagePolicy = policy });
            }
            return Array.AsReadOnly(entries.ToArray());
        }
        catch (RepositoryCatalogException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException or ArgumentException)
        {
            throw new RepositoryCatalogException("invalid_response", ex);
        }
    }

    internal static void ValidateCuration(AppCuration curation, App app)
    {
        if (curation.RecommendationSources is not { Count: > 0 } || curation.Verification is not { } verification
            || verification.VerifiedAt == default || string.IsNullOrWhiteSpace(verification.Scope)
            || verification.EvidenceUrls is not { Count: > 0 } || verification.Notes is null
            || verification.Notes.Any(string.IsNullOrWhiteSpace))
            throw new RepositoryCatalogException("invalid_response");
        RequireHttps(verification.RepositoryUrl);
        var repository = RepositoryAddress.Parse(verification.RepositoryUrl.AbsoluteUri);
        if (!string.Equals(repository.Key, $"{app.Owner}/{app.Repo}", StringComparison.OrdinalIgnoreCase))
            throw new RepositoryCatalogException("invalid_response");
        foreach (var url in verification.EvidenceUrls) RequireHttps(url);
        foreach (var source in curation.RecommendationSources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Platform) || string.IsNullOrWhiteSpace(source.Title)
                || source.DiscoveredAt == default || string.IsNullOrWhiteSpace(source.EvidenceLevel))
                throw new RepositoryCatalogException("invalid_response");
            RequireHttps(source.Url);
        }
    }

    private static void RequireHttps(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new RepositoryCatalogException("invalid_response");
    }
}
