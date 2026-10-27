using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

public sealed partial class GitHubCatalogService
{
    public async Task<CatalogLibrarySyncResult> SyncLibraryAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsLibraryUri(uri))
            throw new RepositoryCatalogException("invalid_library_url");
        ct.ThrowIfCancellationRequested();
        await InitializeAsync(ct).ConfigureAwait(false);
        var library = await DownloadLibraryAsync(uri, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        CatalogLibrarySyncResult result;
        try
        {
            var libraryKeys = library.Select(e => RepositoryKey(e.App)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var libraryEntries = library
                .Where(e => !_suppressedRepositoryKeys.Contains(RepositoryKey(e.App)))
                .ToDictionary(e => RepositoryKey(e.App), StringComparer.OrdinalIgnoreCase);
            var manual = new List<AppDetail>();
            var manualKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = 0;
            foreach (var entry in _entries.Where(e => _manualRepositoryKeys.Contains(RepositoryKey(e.App))))
            {
                var key = RepositoryKey(entry.App);
                if (!libraryEntries.TryGetValue(key, out var shared))
                {
                    manual.Add(entry);
                    manualKeys.Add(key);
                    continue;
                }
                // 远端目录已有同一仓库：以库条目为准，合并到那一条上，不再保留手动记录。
                manual.Add(MergeSharedEntry(shared, entry));
                merged++;
            }
            var next = manual.Concat(libraryEntries
                .Where(pair => !_manualRepositoryKeys.Contains(pair.Key))
                .Select(pair => pair.Value)).ToArray();
            ValidateEntries(next);
            result = new CatalogLibrarySyncResult(library.Length, DateTimeOffset.UtcNow, merged);
            await PersistAsync(next, _suppressedRepositoryKeys, ct, libraryKeys, manualKeys).ConfigureAwait(false);
            _libraryRepositoryKeys = libraryKeys;
            _manualRepositoryKeys = manualKeys;
            _seedEntries = library;
            Volatile.Write(ref _entries, next);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
        return result;
    }

    /// <summary>
    /// 用户的手动记录让位给远端库条目，但只保留用户自己明确设置过的东西：多分类选择与描述覆盖。
    /// 策展说明、核验信息、分类归属与选包策略都跟随远端目录；清空本地策略只是“恢复默认”，
    /// 不能把旧的本地策略当作手动设置再写回来。
    /// </summary>
    private static AppDetail MergeSharedEntry(AppDetail shared, AppDetail manual)
        => shared with
        {
            App = shared.App with
            {
                Categories = manual.App.Categories.Count > 1 ? manual.App.Categories : shared.App.Categories,
            },
            DescriptionOverride = manual.DescriptionOverride,
        };

    private async Task<AppDetail[]> DownloadLibraryAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("GitHubStore/0.1");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsLibraryUri(finalUri))
                throw new RepositoryCatalogException("invalid_response");
            if (!response.IsSuccessStatusCode) throw new RepositoryCatalogException("network_error");
            if (response.Content.Headers.ContentLength > MaximumJsonBytes)
                throw new RepositoryCatalogException("invalid_response");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaximumJsonBytes) throw new RepositoryCatalogException("invalid_response");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            IReadOnlyList<AppDetail> entries;
            try { entries = await CuratedCatalogLoader.LoadAsync(buffer, timeout.Token).ConfigureAwait(false); }
            catch (RepositoryCatalogException ex) when (ex.Code != "invalid_response")
            { throw new RepositoryCatalogException("invalid_response", ex); }
            timeout.Token.ThrowIfCancellationRequested();
            return entries.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new RepositoryCatalogException("request_timeout"); }
        catch (HttpRequestException ex) { throw new RepositoryCatalogException("network_error", ex); }
        catch (IOException ex) { throw new RepositoryCatalogException("network_error", ex); }
    }

    private static bool IsLibraryUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && !((uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.Split('/').Contains("blob", StringComparer.OrdinalIgnoreCase));

    private static HashSet<string> ValidateMembershipKeys(string[] keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
            if (key is null || RepositoryAddress.Parse(key).Key != key.ToLowerInvariant() || !result.Add(key))
                throw new JsonException("Invalid repository membership key.");
        return result;
    }
}
