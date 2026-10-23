using System.Text.RegularExpressions;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>Strict public GitHub repository address; never accepts a caller-controlled API host.</summary>
public sealed record RepositoryAddress(string Owner, string Repo)
{
    public string FullName => $"{Owner}/{Repo}";
    public string Key => FullName.ToLowerInvariant();
    public Uri Url => new($"https://github.com/{Owner}/{Repo}");

    public static RepositoryAddress Parse(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) throw Invalid();
        var value = repository.Trim();
        if (value.Any(char.IsWhiteSpace) || value.Contains('%') || value.Contains('\\')) throw Invalid();
        string path;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw Invalid();
            // Use the original path: System.Uri normalizes dot segments before validation.
            var authorityEnd = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
            if (authorityEnd < 0) throw Invalid();
            path = value[(authorityEnd + 1)..];
            if (path.EndsWith('/')) path = path[..^1];
        }
        else path = value;
        var segments = path.Split('/');
        if (segments.Length != 2) throw Invalid();
        var owner = segments[0];
        var repo = segments[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (!Regex.IsMatch(owner, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")
            || !Regex.IsMatch(repo, "^[A-Za-z0-9_.-]{1,100}$") || repo is "." or "..") throw Invalid();
        return new RepositoryAddress(owner, repo);
    }

    private static RepositoryCatalogException Invalid() => new("invalid_repository");
}
