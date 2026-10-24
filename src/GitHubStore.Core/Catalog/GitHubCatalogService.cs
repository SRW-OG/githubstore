using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>
/// Public GitHub repository metadata with an offline seed and an atomic per-user cache.
/// Repository membership is local user data, not a signed software source or install approval.
/// </summary>
public sealed partial class GitHubCatalogService : ICatalogService, ISearchService, IRepositoryCatalogService, ICatalogLibrarySyncService, IPackagePolicyCatalogService
{
    public static SourceInstanceId ManualSourceId { get; } = new(new Guid("bf1d349a-2696-40b5-98ac-b8f37e904d86"));
    private static readonly JsonSerializerOptions CacheJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppDetail[] _entries = [];
    private AppDetail[] _seedEntries = [];
    private HashSet<string> _suppressedRepositoryKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _manualRepositoryKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _libraryRepositoryKeys;
    private readonly bool _includeCuratedSeed;
    private bool _initialized;

    public GitHubCatalogService(HttpClient httpClient, string dataDirectory, bool includeCuratedSeed = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _http = httpClient;
        _includeCuratedSeed = includeCuratedSeed;
        _cachePath = Path.Combine(Path.GetFullPath(dataDirectory), "github-catalog.json");
    }

    public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated;
    public string? LastCacheError { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var seeds = await ReadSeedAsync(ct).ConfigureAwait(false);
            var entries = seeds;
            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string>? libraryKeys = null;
            var manualKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_cachePath))
            {
                try
                {
                    var info = new FileInfo(_cachePath);
                    if (info.Length > MaximumJsonBytes) throw new JsonException("Cache too large.");
                    var json = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
                    var cache = JsonSerializer.Deserialize<CacheEnvelope>(json, CacheJson);
                    if (cache is null || cache.SchemaVersion is not (1 or 2) || cache.Entries is null
                        || cache.SchemaVersion == 2 && cache.SuppressedRepositoryKeys is null)
                        throw new JsonException("Invalid cache schema.");
                    ValidateEntries(cache.Entries);
                    var restoredSuppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (cache.SchemaVersion == 2)
                    {
                        foreach (var key in cache.SuppressedRepositoryKeys!)
                        {
                            if (key is null || RepositoryAddress.Parse(key).Key != key.ToLowerInvariant()
                                || !restoredSuppressed.Add(key)) throw new JsonException("Invalid suppressed repository key.");
                        }
                        if (cache.Entries.Any(e => restoredSuppressed.Contains(RepositoryKey(e.App))))
                            throw new JsonException("A repository cannot be present and suppressed.");
                    }
                    else if (!cache.Entries.Any(e => RepositoryKey(e.App) == "microsoft/powertoys"))
                    {
                        // Version 1 had exactly one built-in repository. Only its absence proves a previous deletion.
                        restoredSuppressed.Add("microsoft/powertoys");
                    }
                    if (cache.LibraryRepositoryKeys is not null)
                    {
                        libraryKeys = ValidateMembershipKeys(cache.LibraryRepositoryKeys);
                        if (cache.ManualRepositoryKeys is null) throw new JsonException("Missing manual membership.");
                    }
                    // Cached curated entries follow the current bundled selection; manual repositories remain user data.
                    var restored = cache.Entries
                        .Where(entry => libraryKeys is not null || cache.ManualRepositoryKeys?.Contains(RepositoryKey(entry.App), StringComparer.OrdinalIgnoreCase) == true
                            || !_includeCuratedSeed || entry.Curation is null
                            || seeds.Any(seed => string.Equals(RepositoryKey(seed.App), RepositoryKey(entry.App), StringComparison.OrdinalIgnoreCase)))
                        .Select(entry => entry with
                    {
                        App = entry.App with { Stale = true },
                        Capability = null, Install = null, Summary = null,
                    }).ToArray();
                    entries = _includeCuratedSeed && libraryKeys is null
                        ? restored.Concat(seeds.Where(seed => !restoredSuppressed.Contains(RepositoryKey(seed.App))
                            && !restored.Any(entry => string.Equals(RepositoryKey(entry.App), RepositoryKey(seed.App), StringComparison.OrdinalIgnoreCase)))).ToArray()
                        : restored;
                    if (_includeCuratedSeed && libraryKeys is null)
                        entries = entries.Select(entry => AttachCuration(entry, seeds, false)).ToArray();
                    manualKeys = cache.ManualRepositoryKeys is not null
                        ? ValidateMembershipKeys(cache.ManualRepositoryKeys)
                        : restored.Where(e => !seeds.Any(seed => RepositoryKey(seed.App) == RepositoryKey(e.App)))
                            .Select(e => RepositoryKey(e.App)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (manualKeys.Any(key => !entries.Any(e => RepositoryKey(e.App) == key))
                        || libraryKeys is not null && entries.Any(e => !libraryKeys.Contains(RepositoryKey(e.App)) && !manualKeys.Contains(RepositoryKey(e.App))))
                        throw new JsonException("Invalid membership.");
                    suppressed = restoredSuppressed;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or RepositoryCatalogException)
                {
                    // Keep the original file intact; an unreadable cache must not block the offline seed.
                    LastCacheError = "cache_read_failed";
                    entries = seeds;
                    libraryKeys = null;
                    manualKeys.Clear();
                }
            }
            ct.ThrowIfCancellationRequested();
            _libraryRepositoryKeys = libraryKeys;
            _manualRepositoryKeys = manualKeys;
            _seedEntries = libraryKeys is null ? seeds : entries.Where(e => libraryKeys.Contains(RepositoryKey(e.App))).ToArray();
            _suppressedRepositoryKeys = suppressed;
            Volatile.Write(ref _entries, entries);
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<AppDetail> PreviewAsync(string repository, CancellationToken ct = default)
    {
        var address = RepositoryAddress.Parse(repository);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await FetchAsync(address, new CategoryId("system"), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<AppDetail> AddAsync(string repository, CategoryId? category = null, CancellationToken ct = default)
    {
        var address = RepositoryAddress.Parse(repository);
        var selectedCategory = category ?? new CategoryId("system");
        if (!Categories.Any(c => c.Id == selectedCategory)) throw new RepositoryCatalogException("invalid_category");
        await InitializeAsync(ct).ConfigureAwait(false);
        AppDetail detail;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.Any(e => RepositoryKey(e.App) == address.Key)) throw new RepositoryCatalogException("duplicate_repository");
            detail = await FetchAsync(address, selectedCategory, ct).ConfigureAwait(false);
            if (_entries.Any(e => RepositoryKey(e.App) == RepositoryKey(detail.App))) throw new RepositoryCatalogException("duplicate_repository");
            var next = _entries.Append(detail).ToArray();
            var suppressed = new HashSet<string>(_suppressedRepositoryKeys, StringComparer.OrdinalIgnoreCase);
            suppressed.Remove(address.Key);
            suppressed.Remove(RepositoryKey(detail.App));
            var manualKeys = new HashSet<string>(_manualRepositoryKeys, StringComparer.OrdinalIgnoreCase);
            manualKeys.Add(RepositoryKey(detail.App));
            await PersistAsync(next, suppressed, ct, manualKeys: manualKeys).ConfigureAwait(false);
            _manualRepositoryKeys = manualKeys;
            _suppressedRepositoryKeys = suppressed;
            Volatile.Write(ref _entries, next);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
        return detail;
    }

    public async Task RemoveAsync(AppId id, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = _entries.FirstOrDefault(e => e.App.Id == id)
                ?? throw new RepositoryCatalogException("repository_not_found");
            var next = _entries.Where(e => e.App.Id != id).ToArray();
            var suppressed = new HashSet<string>(_suppressedRepositoryKeys, StringComparer.OrdinalIgnoreCase);
            suppressed.Add(RepositoryKey(removed.App));
            var manualKeys = new HashSet<string>(_manualRepositoryKeys, StringComparer.OrdinalIgnoreCase);
            manualKeys.Remove(RepositoryKey(removed.App));
            await PersistAsync(next, suppressed, ct, manualKeys: manualKeys).ConfigureAwait(false);
            _manualRepositoryKeys = manualKeys;
            _suppressedRepositoryKeys = suppressed;
            Volatile.Write(ref _entries, next);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
    }

    public async Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        return Categories;
    }

    public async Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Limit);
        await InitializeAsync(ct).ConfigureAwait(false);
        var matches = Sort(SnapshotApps().Where(a => (query.IncludeArchived || !a.Archived)
            && (query.Category is null || a.Categories.Contains(query.Category.Value))), query.Sort).ToArray();
        ct.ThrowIfCancellationRequested();
        var items = matches.Skip(query.Offset).Take(query.Limit).ToArray();
        return new CatalogPage { Items = Array.AsReadOnly(items), Total = matches.Length, HasMore = (long)query.Offset + items.Length < matches.Length };
    }

    public async Task<IReadOnlyList<App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (!Enum.IsDefined(board)) throw new ArgumentOutOfRangeException(nameof(board));
        await InitializeAsync(ct).ConfigureAwait(false);
        var apps = SnapshotApps().Where(a => !a.Archived);
        // No measured star history exists. Trending/New return empty rather than invent growth statistics.
        if (board != BoardKind.Stars) return Array.Empty<App>();
        return Array.AsReadOnly(Sort(apps, SortKey.Stars).Take(limit).ToArray());
    }

    public async Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Volatile.Read(ref _entries).FirstOrDefault(e => e.App.Id == id)
            ?? throw new RepositoryCatalogException("repository_not_found");
    }

    public async Task<SearchResult> SearchAsync(string query, SearchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Limit);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinStars);
        await InitializeAsync(ct).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = Sort(SnapshotApps().Where(app => !app.Archived && app.Stars >= options.MinStars
            && (options.Category is null || app.Categories.Contains(options.Category.Value))
            && (string.IsNullOrWhiteSpace(options.Language) || string.Equals(app.Language, options.Language, StringComparison.OrdinalIgnoreCase))
            && terms.All(term => app.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || RepositoryKey(app).Contains(term, StringComparison.OrdinalIgnoreCase)
                || (app.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || app.Topics.Any(topic => topic.Contains(term, StringComparison.OrdinalIgnoreCase)))), SortKey.Stars).ToArray();
        ct.ThrowIfCancellationRequested();
        return new SearchResult
        {
            Items = Array.AsReadOnly(matches.Take(options.Limit).ToArray()), Total = matches.Length,
            Source = "local", Took = timer.Elapsed,
            RemoteError = options.IncludeRemote ? "remote_search_unavailable" : null,
        };
    }

    public async Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default)
    {
        if (scope != RefreshScope.All) throw new ArgumentOutOfRangeException(nameof(scope));
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _entries;
            var next = new List<AppDetail>();
            progress?.Report(new RefreshProgress { Done = 0, Total = previous.Length, Message = "refreshing" });
            foreach (var entry in previous)
            {
                var refreshed = await FetchAsync(RepositoryAddress.Parse($"{entry.App.Owner}/{entry.App.Repo}"), entry.App.PrimaryCategory, ct, entry.PackagePolicy, false).ConfigureAwait(false);
                next.Add(refreshed with { Curation = refreshed.Curation ?? entry.Curation, PackagePolicy = entry.PackagePolicy });
                progress?.Report(new RefreshProgress { Done = next.Count, Total = previous.Length, Message = "refreshing" });
            }
            var updated = next.ToArray();
            ValidateEntries(updated);
            await PersistAsync(updated, _suppressedRepositoryKeys, ct).ConfigureAwait(false);
            Volatile.Write(ref _entries, updated);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
    }

    private async Task<AppDetail> FetchAsync(RepositoryAddress address, CategoryId category, CancellationToken ct, PackagePolicy? policy = null, bool useSeedPolicy = true)
    {
        if (useSeedPolicy) policy ??= _seedEntries.FirstOrDefault(e => RepositoryKey(e.App) == address.Key)?.PackagePolicy;
        PackagePolicyValidator.Validate(policy);
        var path = $"repos/{Uri.EscapeDataString(address.Owner)}/{Uri.EscapeDataString(address.Repo)}";
        var repository = await RequestAsync(path, false, ct).ConfigureAwait(false);
        string? release;
        if (policy?.ReleaseChannel == PackageReleaseChannel.LatestPrerelease)
        {
            // GitHub lists releases by creation time, so compare published times over a bounded window.
            var candidates = new List<(JsonElement Release, DateTimeOffset PublishedAt)>();
            for (var page = 1; page <= 3; page++)
            {
                var json = await RequestAsync($"{path}/releases?per_page=100&page={page}", false, ct).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(json!);
                    if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                    foreach (var item in document.RootElement.EnumerateArray())
                        if (item.GetProperty("prerelease").GetBoolean() && !item.GetProperty("draft").GetBoolean())
                            candidates.Add((item.Clone(), item.GetProperty("published_at").GetDateTimeOffset()));
                    if (document.RootElement.GetArrayLength() < 100) break;
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                { throw new RepositoryCatalogException("invalid_response", ex); }
            }
            release = candidates.OrderByDescending(e => e.PublishedAt).Select(e => e.Release.GetRawText()).FirstOrDefault();
        }
        else
        {
            var suffix = policy?.ReleaseChannel == PackageReleaseChannel.Pinned
                ? "tags/" + Uri.EscapeDataString(policy.PinnedTag!) : "latest";
            release = await RequestAsync($"{path}/releases/{suffix}", true, ct).ConfigureAwait(false);
        }
        var detail = AttachCuration(GitHubResponseParser.Parse(repository!, release, DateTimeOffset.UtcNow, category), _seedEntries)
            with { PackagePolicy = policy };
        if (detail.Releases.Any(r => r.Draft || policy?.ReleaseChannel == PackageReleaseChannel.Pinned && r.TagName != policy.PinnedTag
            || policy?.ReleaseChannel == PackageReleaseChannel.LatestPrerelease && !r.Prerelease
            || (policy is null || policy.ReleaseChannel == PackageReleaseChannel.Stable) && r.Prerelease))
            throw new RepositoryCatalogException("invalid_response");
        return detail;
    }

    private async Task<string?> RequestAsync(string path, bool allowNotFound, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/{path}");
            request.Headers.UserAgent.ParseAdd("GitHubStore/0.1");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalUri && (finalUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(finalUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)))
                throw new RepositoryCatalogException("invalid_response");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return allowNotFound ? null : throw new RepositoryCatalogException("repository_not_found");
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0"))
                throw new RepositoryCatalogException("rate_limited");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new RepositoryCatalogException("access_denied");
            if (!response.IsSuccessStatusCode) throw new RepositoryCatalogException("network_error");
            if (response.Content.Headers.ContentLength > MaximumJsonBytes) throw new RepositoryCatalogException("invalid_response");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaximumJsonBytes) throw new RepositoryCatalogException("invalid_response");
                buffer.Write(chunk, 0, count);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RepositoryCatalogException("request_timeout"); }
        catch (HttpRequestException ex) { throw new RepositoryCatalogException("network_error", ex); }
        catch (IOException ex) { throw new RepositoryCatalogException("network_error", ex); }
    }

    private async Task PersistAsync(AppDetail[] entries, HashSet<string> suppressedRepositoryKeys, CancellationToken ct,
        HashSet<string>? libraryKeys = null, HashSet<string>? manualKeys = null)
    {
        var temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(new CacheEnvelope(2, entries,
                suppressedRepositoryKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                (libraryKeys ?? _libraryRepositoryKeys)?.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                (manualKeys ?? _manualRepositoryKeys).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray()), CacheJson);
            if (Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetPreamble().Length > MaximumJsonBytes)
                throw new RepositoryCatalogException("cache_write_failed");
            await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _cachePath, true);
            LastCacheError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RepositoryCatalogException("cache_write_failed", ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Leave recoverable orphan temp. */ }
        }
    }

    private async Task<AppDetail[]> ReadSeedAsync(CancellationToken ct)
    {
        await using var stream = typeof(GitHubCatalogService).Assembly.GetManifestResourceStream("GitHubStore.Core.PowerToys.seed.json");
        if (stream is null) throw new RepositoryCatalogException("seed_unavailable");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        var release = root.GetProperty("release");
        var original = GitHubResponseParser.Parse(root.GetProperty("repository").GetRawText(),
            release.ValueKind == JsonValueKind.Null ? null : release.GetRawText(),
            root.GetProperty("fetchedAt").GetDateTimeOffset(), new CategoryId("system"), stale: true);
        if (!_includeCuratedSeed) return [original];
        await using var curatedStream = typeof(GitHubCatalogService).Assembly.GetManifestResourceStream(CuratedCatalogLoader.ResourceName);
        if (curatedStream is null) throw new RepositoryCatalogException("seed_unavailable");
        var curated = await CuratedCatalogLoader.LoadAsync(curatedStream, ct).ConfigureAwait(false);
        var merged = new Dictionary<string, AppDetail>(StringComparer.OrdinalIgnoreCase) { [RepositoryKey(original.App)] = original };
        foreach (var entry in curated)
        {
            var key = RepositoryKey(entry.App);
            if (!merged.TryGetValue(key, out var previous) || entry.App.FetchedAt >= previous.App.FetchedAt)
                merged[key] = entry;
            else
                merged[key] = previous with { Curation = entry.Curation, PackagePolicy = entry.PackagePolicy };
        }
        return merged.Values.ToArray();
    }

    private static AppDetail AttachCuration(AppDetail entry, AppDetail[] seeds, bool attachPolicy = true)
    {
        var current = seeds.FirstOrDefault(seed => string.Equals(RepositoryKey(seed.App), RepositoryKey(entry.App), StringComparison.OrdinalIgnoreCase));
        return current is null ? entry : entry with { Curation = current.Curation, PackagePolicy = attachPolicy ? entry.PackagePolicy ?? current.PackagePolicy : entry.PackagePolicy };
    }

    internal static bool IsKnownCategory(CategoryId category) => Categories.Any(c => c.Id == category);

    private static void ValidateEntries(AppDetail[] entries)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry?.App is null) throw new RepositoryCatalogException("invalid_response");
            var address = RepositoryAddress.Parse($"{entry.App.Owner}/{entry.App.Repo}");
            if (entry.App.Id.SourceId != ManualSourceId || entry.App.Id.PackageId.Value != address.Key
                || !keys.Add(address.Key) || !IsKnownCategory(entry.App.PrimaryCategory))
                throw new RepositoryCatalogException("invalid_response");
            PackagePolicyValidator.Validate(entry.PackagePolicy);
            if (entry.Curation is { } curation) CuratedCatalogLoader.ValidateCuration(curation, entry.App);
        }
    }

    private void NotifyUpdated() => CatalogUpdated?.Invoke(this, new CatalogUpdatedEventArgs { Count = Volatile.Read(ref _entries).Length, At = DateTimeOffset.UtcNow });
    private IEnumerable<App> SnapshotApps() => Volatile.Read(ref _entries).Select(e => e.App);
    private static string RepositoryKey(App app) => $"{app.Owner}/{app.Repo}".ToLowerInvariant();
    private static IOrderedEnumerable<App> Sort(IEnumerable<App> apps, SortKey sort)
        => (sort switch
        {
            SortKey.Stars => apps.OrderByDescending(a => a.Stars),
            SortKey.Updated => apps.OrderByDescending(a => a.PushedAt),
            SortKey.Name => apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase),
            SortKey.Trending => apps.OrderByDescending(a => a.Stars), // No growth history; the UI labels this fallback.
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        }).ThenBy(a => a.Id.ToString(), StringComparer.Ordinal);

    private sealed record CacheEnvelope(int SchemaVersion, AppDetail[] Entries, string[]? SuppressedRepositoryKeys = null,
        string[]? LibraryRepositoryKeys = null, string[]? ManualRepositoryKeys = null);
    private static readonly IReadOnlyList<Category> Categories = CreateCategories();
    private static IReadOnlyList<Category> CreateCategories()
    {
        (string Id, string Name, string English)[] definitions =
        [
            ("dev-tools", "开发工具", "Developer Tools"), ("system", "系统工具", "System"),
            ("network", "网络", "Network"), ("media", "媒体影音", "Media"),
            ("graphics", "图形设计", "Graphics"), ("office", "办公效率", "Office"),
            ("security", "安全隐私", "Security"), ("games", "游戏", "Games"),
            ("ai", "AI / LLM 工具", "AI / LLM"), ("terminal", "终端与 Shell", "Terminal & Shell"),
            ("files", "文件管理", "Files"), ("science", "教育与科学", "Education & Science"),
        ];
        return Array.AsReadOnly(definitions.Select((d, index) => new Category
        { Id = new CategoryId(d.Id), Name = d.Name, NameEn = d.English, Order = index }).ToArray());
    }
}
