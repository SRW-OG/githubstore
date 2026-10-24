using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

public sealed partial class GitHubCatalogService
{
    public async Task<AppDetail> UpdatePackagePolicyAsync(AppId id, PackagePolicy? policy, CancellationToken ct = default)
    {
        PackagePolicyValidator.Validate(policy);
        await InitializeAsync(ct).ConfigureAwait(false);
        AppDetail detail;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _entries.FirstOrDefault(e => e.App.Id == id)
                ?? throw new RepositoryCatalogException("repository_not_found");
            // Fetch first: an unavailable channel or failed request must not partially change the cache.
            detail = await FetchAsync(RepositoryAddress.Parse($"{previous.App.Owner}/{previous.App.Repo}"), previous.App.PrimaryCategory, ct, policy, false).ConfigureAwait(false);
            detail = detail with { Curation = previous.Curation, PackagePolicy = policy };
            var next = _entries.Select(e => e.App.Id == id ? detail : e).ToArray();
            // A local policy edit makes this entry local user data; subsequent library
            // syncs must not silently replace either its policy or repository membership.
            var manualKeys = new HashSet<string>(_manualRepositoryKeys, StringComparer.OrdinalIgnoreCase)
                { RepositoryKey(detail.App) };
            await PersistAsync(next, _suppressedRepositoryKeys, ct, manualKeys: manualKeys).ConfigureAwait(false);
            _manualRepositoryKeys = manualKeys;
            Volatile.Write(ref _entries, next);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
        return detail;
    }
}
