using System.Runtime.InteropServices;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

public sealed partial class GitHubCatalogService
{
    public async Task<AppDetail> PreviewAsync(string repository, PackagePolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PackagePolicyValidator.Validate(policy);
        var address = RepositoryAddress.Parse(repository);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await FetchAsync(address, new CategoryId("system"), ct, policy, false).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task<AppDetail> AddAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
        string confirmedAssetName, string confirmedReleaseTag, CancellationToken ct = default)
        => AddConfiguredAsync(repository, category, policy, description, confirmedAssetName, confirmedReleaseTag, false, ct);

    public Task<AppDetail> AddCatalogOnlyAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
        string? confirmedReleaseTag, CancellationToken ct = default)
        => AddConfiguredAsync(repository, category, policy, description, null, confirmedReleaseTag, true, ct);

    private async Task<AppDetail> AddConfiguredAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
        string? confirmedAssetName, string? confirmedReleaseTag, bool catalogOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PackagePolicyValidator.Validate(policy);
        ValidateDescriptionOverride(description);
        if (!catalogOnly) ValidateConfirmedAssetName(confirmedAssetName!);
        var address = RepositoryAddress.Parse(repository);
        var selectedCategory = category ?? new CategoryId("system");
        if (!IsKnownCategory(selectedCategory)) throw new RepositoryCatalogException("invalid_category");
        await InitializeAsync(ct).ConfigureAwait(false);
        AppDetail detail;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.Any(e => RepositoryKey(e.App) == address.Key)) throw new RepositoryCatalogException("duplicate_repository");
            detail = await FetchAsync(address, selectedCategory, ct, policy, false).ConfigureAwait(false);
            if (_entries.Any(e => RepositoryKey(e.App) == RepositoryKey(detail.App))) throw new RepositoryCatalogException("duplicate_repository");
            var architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => Arch.Amd64,
                Architecture.Arm64 => Arch.Arm64,
                Architecture.X86 => Arch.X86,
                _ => Arch.Unknown,
            };
            var release = DefaultAssetSelectionPolicy.SelectRelease(detail);
            if (!string.Equals(release?.TagName, confirmedReleaseTag, StringComparison.Ordinal))
                throw new RepositoryCatalogException("selected_asset_unavailable");
            if (catalogOnly)
            {
                // Ignore restrictive configured rules when deciding whether asset confirmation
                // can be omitted. A rule that matches nothing must not hide an available package.
                var broadPolicy = new PackagePolicy { AssetRules = [new PackageAssetRule { Include = ["*"] }] };
                if (release is not null && DefaultAssetSelectionPolicy.GetEligibleAssets(release.Assets, architecture,
                    RepositoryKey(detail.App), broadPolicy).Any(a => IsSafeConfirmedAssetName(a.Name)))
                    throw new RepositoryCatalogException("selected_asset_unavailable");
            }
            else if (architecture == Arch.Unknown || release is null
                || !DefaultAssetSelectionPolicy.GetEligibleAssets(release.Assets, architecture, RepositoryKey(detail.App), policy)
                    .Any(a => string.Equals(a.Name, confirmedAssetName, StringComparison.Ordinal)))
                throw new RepositoryCatalogException("selected_asset_unavailable");
            detail = ApplyDescriptionOverride(detail, description?.Trim());
            var next = _entries.Append(detail).ToArray();
            var suppressed = new HashSet<string>(_suppressedRepositoryKeys, StringComparer.OrdinalIgnoreCase);
            suppressed.Remove(address.Key);
            suppressed.Remove(RepositoryKey(detail.App));
            var manualKeys = new HashSet<string>(_manualRepositoryKeys, StringComparer.OrdinalIgnoreCase) { RepositoryKey(detail.App) };
            await PersistAsync(next, suppressed, ct, manualKeys: manualKeys).ConfigureAwait(false);
            _manualRepositoryKeys = manualKeys;
            _suppressedRepositoryKeys = suppressed;
            Volatile.Write(ref _entries, next);
        }
        finally { _gate.Release(); }
        NotifyUpdated();
        return detail;
    }

    private static AppDetail ApplyDescriptionOverride(AppDetail detail, string? description)
        => detail with { DescriptionOverride = description, App = detail.App with { Description = description ?? detail.App.Description } };

    private static void ValidateDescriptionOverride(string? description)
    {
        if (description is not null && (description.Length > 4000 || description.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
            throw new RepositoryCatalogException("invalid_description");
    }

    private static bool IsSafeConfirmedAssetName(string name)
    {
        try { ValidateConfirmedAssetName(name); return true; }
        catch (RepositoryCatalogException) { return false; }
    }

    private static void ValidateConfirmedAssetName(string name)
    {
        try
        {
            PackagePolicyValidator.ValidateRelativePath(name);
            if (name.Length > 255 || name.Contains('/') || name.Contains('\\')) throw new RepositoryCatalogException("invalid_asset_name");
        }
        catch (RepositoryCatalogException ex) when (ex.Code == "invalid_package_policy")
        { throw new RepositoryCatalogException("invalid_asset_name", ex); }
    }
}
