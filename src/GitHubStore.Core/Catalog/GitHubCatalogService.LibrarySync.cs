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
            var manual = _entries.Where(e => _manualRepositoryKeys.Contains(RepositoryKey(e.App))).ToArray();
            var next = manual.Concat(library.Where(e => !_suppressedRepositoryKeys.Contains(RepositoryKey(e.App))
                && !_manualRepositoryKeys.Contains(RepositoryKey(e.App)))).ToArray();
            ValidateEntries(next);
            result = new CatalogLibrarySyncResult(library.Length, DateTimeOffset.UtcNow);
            await PersistAsync(next, _suppressedRepositoryKeys, ct, libraryKeys).ConfigureAwait(false);
            _libraryRepositoryKeys = libraryKeys;
            _seedEntries = library;
            Volatile.Write(ref _entries, next);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
        return result;
    }

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
