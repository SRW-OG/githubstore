namespace GitHubStore.Core.Abstractions;

/// <summary>Repository management for locally subscribed, public GitHub projects.</summary>
public interface IRepositoryCatalogService
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<AppDetail> PreviewAsync(string repository, CancellationToken ct = default);
    Task<AppDetail> AddAsync(string repository, CategoryId? category = null, CancellationToken ct = default);
    Task RemoveAsync(AppId id, CancellationToken ct = default);
}

/// <summary>Stable reason code; the presentation layer supplies localized messages.</summary>
public sealed class RepositoryCatalogException : Exception
{
    public RepositoryCatalogException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    public string Code { get; }
}
