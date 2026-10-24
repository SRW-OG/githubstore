namespace GitHubStore.Core.Abstractions;

/// <summary>Updates optional package metadata for both library and manually added repositories.</summary>
public interface IPackagePolicyCatalogService
{
    Task<AppDetail> PreviewAsync(string repository, PackagePolicy policy, CancellationToken ct = default);
    Task<AppDetail> AddAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
        string confirmedAssetName, string confirmedReleaseTag, CancellationToken ct = default);
    Task<AppDetail> AddCatalogOnlyAsync(string repository, CategoryId? category, PackagePolicy policy, string? description,
        string? confirmedReleaseTag, CancellationToken ct = default);
    Task<AppDetail> UpdatePackagePolicyAsync(AppId id, PackagePolicy? policy, CancellationToken ct = default);
}
