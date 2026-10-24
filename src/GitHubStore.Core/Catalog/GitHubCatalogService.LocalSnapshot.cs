using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

public sealed partial class GitHubCatalogService : ILocalCatalogSnapshotService
{
    public async Task<LocalCatalogSnapshot> ReadLocalSnapshotAsync(CancellationToken ct = default)
    {
        // Initialization reads bundled snapshots and the local cache only.
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // A corrupt cache must not turn a request for the complete library into
            // a successful export of fallback seed entries.
            if (LastCacheError == "cache_read_failed") throw new RepositoryCatalogException("cache_read_failed");
            // Detach nested collections as well as the top-level array. Callers can
            // edit their snapshot without modifying live catalog state behind the gate.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_entries, CacheJson);
            var entries = JsonSerializer.Deserialize<AppDetail[]>(bytes, CacheJson)
                ?? throw new RepositoryCatalogException("invalid_response");
            ct.ThrowIfCancellationRequested();
            return new LocalCatalogSnapshot
            {
                Entries = Array.AsReadOnly(entries),
                Categories = Array.AsReadOnly(Categories.Select(category => category with { }).ToArray()),
            };
        }
        finally { _gate.Release(); }
    }
}
