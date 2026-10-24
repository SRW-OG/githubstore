using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

public static class PackagePolicyValidator
{
    public static void Validate(PackagePolicy? policy)
    {
        if (policy is null) return;
        if (!Enum.IsDefined(policy.ReleaseChannel) || !Enum.IsDefined(policy.InstallFormat)
            || policy.AssetRules is null || policy.AssetRules.Count > 4 || policy.PreservePaths is null || policy.PreservePaths.Count > 64)
            Invalid();
        if (policy.ReleaseChannel == PackageReleaseChannel.Pinned)
        {
            if (string.IsNullOrWhiteSpace(policy.PinnedTag) || policy.PinnedTag.Length > 256 || policy.PinnedTag.Any(char.IsControl)) Invalid();
        }
        else if (policy.PinnedTag is not null) Invalid();
        var architectures = new HashSet<Arch>();
        foreach (var rule in policy.AssetRules!)
        {
            if (rule is null || !Enum.IsDefined(rule.Architecture) || !architectures.Add(rule.Architecture)
                || rule.Include is null || rule.Exclude is null || rule.Include.Count > 32 || rule.Exclude.Count > 32) Invalid();
            foreach (var pattern in rule!.Include.Concat(rule.Exclude))
                if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256
                    || pattern.Any(c => char.IsControl(c) || "\\/:[]{}".Contains(c))) Invalid();
        }
        if (policy.ExecutableRelativePath is not null) ValidateRelativePath(policy.ExecutableRelativePath);
        foreach (var path in policy.PreservePaths!) ValidateRelativePath(path);
    }

    /// <summary>Checks Windows-safe package-relative paths on every host OS.</summary>
    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c))) Invalid();
        var segments = path.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')) Invalid();
            var name = segment.Split('.')[0];
            if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && name[3] is >= '0' and <= '9') Invalid();
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new RepositoryCatalogException("invalid_package_policy");
}
