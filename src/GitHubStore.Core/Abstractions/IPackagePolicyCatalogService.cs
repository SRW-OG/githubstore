namespace GitHubStore.Core.Abstractions;

/// <summary>Updates optional package metadata for both library and manually added repositories.</summary>
public interface IPackagePolicyCatalogService
{
    Task<AppDetail> UpdatePackagePolicyAsync(AppId id, PackagePolicy? policy, CancellationToken ct = default);
}
