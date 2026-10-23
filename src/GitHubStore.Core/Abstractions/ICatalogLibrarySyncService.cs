namespace GitHubStore.Core.Abstractions;

public interface ICatalogLibrarySyncService
{
    Task<CatalogLibrarySyncResult> SyncLibraryAsync(string url, CancellationToken ct = default);
}

/// <param name="AppCount">Number of entries in the validated remote library, including suppressed entries.</param>
public sealed record CatalogLibrarySyncResult(int AppCount, DateTimeOffset SyncedAt);
