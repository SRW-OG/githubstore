namespace GitHubStore.Core.Abstractions;

public interface ICatalogLibrarySyncService
{
    Task<CatalogLibrarySyncResult> SyncLibraryAsync(string url, CancellationToken ct = default);
}

/// <param name="AppCount">Number of entries in the validated remote library, including suppressed entries.</param>
/// <param name="MergedManualCount">
/// 手动添加、且远端库现在已收录的仓库数量。这些记录已让位给远端条目，远端同步不会再覆盖远端的更新。
/// </param>
public sealed record CatalogLibrarySyncResult(int AppCount, DateTimeOffset SyncedAt, int MergedManualCount = 0);
