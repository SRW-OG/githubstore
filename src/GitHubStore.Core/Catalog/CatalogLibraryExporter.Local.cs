using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

public static partial class CatalogLibraryExporter
{
    public const string LocalFormat = "githubstore.local-catalog";

    /// <summary>
    /// Exports complete local catalog metadata, including manual repositories and every release.
    /// This format is a local metadata backup, not a public curated source or install approval.
    /// Runtime capabilities, installation receipts and AI summaries are intentionally excluded.
    /// </summary>
    public static async Task<byte[]> ExportLocalAsync(IReadOnlyList<AppDetail> entries,
        IReadOnlyList<Category> categories, DateTimeOffset exportedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(categories);
        ct.ThrowIfCancellationRequested();
        if (exportedAt == default) throw new RepositoryCatalogException("invalid_response");
        ValidateLocalMetadata(entries, categories);
        var metadata = entries.OrderBy(entry => $"{entry.App.Owner}/{entry.App.Repo}", StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.App.Id.ToString(), StringComparer.Ordinal)
            .Select(entry => entry with { Capability = null, Install = null, Summary = null }).ToArray();
        var orderedCategories = categories.OrderBy(category => category.Order)
            .ThenBy(category => category.Id.Value, StringComparer.Ordinal).ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new LocalLibrary(LocalFormat, 1, exportedAt, metadata, orderedCategories), Json);
        if (payload.Length > MaximumBytes) throw new RepositoryCatalogException("cache_write_failed");
        ct.ThrowIfCancellationRequested();
        // Exercise the exact explicit local reader; a successful public-sync import is
        // deliberately not a validation criterion for a local metadata backup.
        using var stream = new MemoryStream(payload);
        await ImportLocalAsync(stream, ct).ConfigureAwait(false);
        return payload;
    }

    /// <summary>
    /// Reads and validates a local export without HTTP, catalog mutation or automatic import.
    /// Returned metadata carries no new evidence, signature verification or installation authority.
    /// Public curated sync deliberately uses a different loader and rejects this format.
    /// </summary>
    public static async Task<LocalCatalogSnapshot> ImportLocalAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaximumBytes) throw new JsonException("Local catalog too large.");
                buffer.Write(chunk, 0, count);
            }
            ct.ThrowIfCancellationRequested();
            var library = JsonSerializer.Deserialize<LocalLibrary>(buffer.ToArray(), Json);
            if (library is null || library.Format != LocalFormat || library.SchemaVersion != 1 || library.ExportedAt == default
                || library.Entries is null || library.Categories is null) throw new JsonException("Invalid local catalog schema.");
            ValidateLocalMetadata(library.Entries, library.Categories);
            if (library.Entries.Any(entry => entry.Install is not null || entry.Capability is not null || entry.Summary is not null))
                throw new JsonException("Runtime state is not local catalog metadata.");
            var entries = library.Entries.Select(entry => entry with
            {
                App = entry.App with { Description = entry.DescriptionOverride ?? entry.App.Description },
            }).ToArray();
            return new LocalCatalogSnapshot { Entries = Array.AsReadOnly(entries), Categories = Array.AsReadOnly(library.Categories) };
        }
        catch (RepositoryCatalogException ex) when (ex.Code != "invalid_response")
        { throw new RepositoryCatalogException("invalid_response", ex); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw new RepositoryCatalogException("invalid_response", ex); }
    }

    private static void ValidateLocalMetadata(IReadOnlyList<AppDetail> entries, IReadOnlyList<Category> categories)
    {
        var known = new HashSet<CategoryId>();
        foreach (var category in categories)
        {
            if (category is null || !GitHubCatalogService.IsKnownCategory(category.Id) || !known.Add(category.Id)
                || string.IsNullOrWhiteSpace(category.Name) || string.IsNullOrWhiteSpace(category.NameEn)) InvalidLocalMetadata();
        }
        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry?.App is null) InvalidLocalMetadata();
            var app = entry.App;
            var repository = RepositoryAddress.Parse($"{app.Owner}/{app.Repo}");
            if (app.Id.SourceId != GitHubCatalogService.ManualSourceId || app.Id.PackageId.Value != repository.Key
                || !repositories.Add(repository.Key) || app.FetchedAt == default || !known.Contains(app.PrimaryCategory)
                || app.Categories is null || app.Categories.Any(category => !known.Contains(category))
                || app.Topics is null || app.Topics.Any(topic => topic is null)
                || entry.Releases is null || entry.ReadmeMeta is null || entry.Screenshots is null || entry.Screenshots.Any(url => url is null))
                InvalidLocalMetadata();
            ValidateLocalDescription(entry.DescriptionOverride);
            PackagePolicyValidator.Validate(entry.PackagePolicy);
            if (entry.Curation is { } curation) CuratedCatalogLoader.ValidateCuration(curation, app);
            var releaseKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var release in entry.Releases)
            {
                if (release is null || release.AppId != app.Id || string.IsNullOrWhiteSpace(release.Key)
                    || string.IsNullOrWhiteSpace(release.TagName) || !releaseKeys.Add(release.Key) || release.Assets is null)
                    InvalidLocalMetadata();
                foreach (var asset in release.Assets)
                {
                    if (asset is null || asset.ReleaseKey != release.Key || string.IsNullOrWhiteSpace(asset.Name)
                        || string.IsNullOrWhiteSpace(asset.InstallerId) || asset.Size < 0 || asset.DownloadCount < 0
                        || !Enum.IsDefined(asset.Arch) || !Enum.IsDefined(asset.Kind) || !Enum.IsDefined(asset.Installer)
                        || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                        || !string.IsNullOrEmpty(uri.UserInfo)) InvalidLocalMetadata();
                }
            }
        }
    }

    private static void ValidateLocalDescription(string? description)
    {
        if (description is not null && (description.Length > 4000
            || description.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))) InvalidLocalMetadata();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void InvalidLocalMetadata() => throw new RepositoryCatalogException("invalid_response");

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record LocalLibrary(string Format, int SchemaVersion, DateTimeOffset ExportedAt,
        AppDetail[] Entries, Category[] Categories);
}
