namespace GitHubStore.Core.Abstractions;

/// <summary>A detached, complete catalog metadata snapshot. Reading it never requests remote metadata.</summary>
public interface ILocalCatalogSnapshotService
{
    Task<LocalCatalogSnapshot> ReadLocalSnapshotAsync(CancellationToken ct = default);
}

public sealed record LocalCatalogSnapshot
{
    public IReadOnlyList<AppDetail> Entries { get; init; } = [];
    public IReadOnlyList<Category> Categories { get; init; } = [];
}
