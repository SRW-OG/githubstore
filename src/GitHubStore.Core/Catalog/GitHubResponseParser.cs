using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>Maps public GitHub API metadata, never a verified install manifest.</summary>
public static class GitHubResponseParser
{
    public static AppDetail Parse(string repositoryJson, string? releaseJson, DateTimeOffset fetchedAt,
        CategoryId? category = null, bool stale = false)
    {
        try
        {
            using var repositoryDocument = JsonDocument.Parse(repositoryJson);
            using var releaseDocument = releaseJson is null ? null : JsonDocument.Parse(releaseJson);
            var repository = repositoryDocument.RootElement;
            var owner = RequiredString(repository.GetProperty("owner"), "login");
            var name = RequiredString(repository, "name");
            var address = RepositoryAddress.Parse($"{owner}/{name}");
            if (Bool(repository, "private")) throw new RepositoryCatalogException("repository_not_public");
            var appId = new AppId(GitHubCatalogService.ManualSourceId, new PackageId(address.Key));
            var primary = category ?? new CategoryId("system");
            var releases = releaseDocument is null ? Array.Empty<Release>() :
                new[] { ParseRelease(releaseDocument.RootElement, appId) };
            var license = repository.TryGetProperty("license", out var licenseObject)
                && licenseObject.ValueKind == JsonValueKind.Object ? Text(licenseObject, "spdx_id") : null;
            var app = new App
            {
                Id = appId,
                SourceRevision = 1,
                DisplayName = name,
                Owner = owner,
                Repo = name,
                Description = Text(repository, "description"),
                Homepage = SafeHttps(Text(repository, "homepage")) ?? address.Url.AbsoluteUri,
                License = string.IsNullOrWhiteSpace(license) ? "NOASSERTION" : license,
                Language = Text(repository, "language"),
                Topics = Array.AsReadOnly(repository.TryGetProperty("topics", out var topics)
                    && topics.ValueKind == JsonValueKind.Array ? topics.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray() : []),
                Stars = Number(repository, "stargazers_count"),
                Forks = Number(repository, "forks_count"),
                OpenIssues = Number(repository, "open_issues_count"),
                Archived = Bool(repository, "archived"),
                IsTemplate = Bool(repository, "is_template"),
                DefaultBranch = Text(repository, "default_branch") ?? "main",
                PushedAt = Date(repository, "pushed_at"),
                CreatedAt = Date(repository, "created_at"),
                PrimaryCategory = primary,
                Categories = Array.AsReadOnly(new[] { primary }),
                WindowsReady = releases.SelectMany(r => r.Assets).Any(IsWindowsCandidate),
                Excluded = Bool(repository, "archived") ? ExcludeReason.Archived : ExcludeReason.None,
                FetchedAt = fetchedAt,
                Stale = stale,
            };
            return new AppDetail
            {
                App = app,
                Releases = Array.AsReadOnly(releases),
                ReadmeMeta = new ReadmeMeta
                {
                    HtmlUrl = $"{address.Url.AbsoluteUri}/blob/{Uri.EscapeDataString(app.DefaultBranch)}/README.md",
                    FetchedAt = null, // README itself has not been fetched.
                },
            };
        }
        catch (RepositoryCatalogException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new RepositoryCatalogException("invalid_response", ex);
        }
    }

    private static Release ParseRelease(JsonElement release, AppId appId)
    {
        var tag = RequiredString(release, "tag_name");
        var key = $"{(Bool(release, "prerelease") ? "prerelease" : "stable")}:{tag}";
        var assets = release.TryGetProperty("assets", out var rawAssets)
            && rawAssets.ValueKind == JsonValueKind.Array
            ? rawAssets.EnumerateArray().Select(a => ParseAsset(a, key)).Where(a => a is not null)
                .Cast<Asset>().ToArray() : [];
        return new Release
        {
            AppId = appId,
            Key = key,
            TagName = tag,
            Name = Text(release, "name"),
            Body = Truncate(Text(release, "body"), 12000),
            Prerelease = Bool(release, "prerelease"),
            Draft = Bool(release, "draft"),
            PublishedAt = Date(release, "published_at"),
            Assets = Array.AsReadOnly(assets),
        };
    }

    private static Asset? ParseAsset(JsonElement asset, string releaseKey)
    {
        var name = RequiredString(asset, "name");
        var lower = name.ToLowerInvariant();
        var kind = Classify(lower);
        if (kind is AssetKind.Checksum or AssetKind.Signature or AssetKind.SourceArchive) return null;
        var downloadUrl = SafeHttps(Text(asset, "browser_download_url"));
        if (downloadUrl is null) return null;
        var arch = lower.Contains("arm64", StringComparison.Ordinal) || lower.Contains("aarch64", StringComparison.Ordinal)
            ? Arch.Arm64 : lower.Contains("x64", StringComparison.Ordinal) || lower.Contains("amd64", StringComparison.Ordinal)
            ? Arch.Amd64 : lower.Contains("x86", StringComparison.Ordinal) || lower.Contains("win32", StringComparison.Ordinal)
            ? Arch.X86 : Arch.Unknown;
        var installer = kind switch
        {
            AssetKind.InstallerMsi => InstallerKind.Msi,
            AssetKind.ArchiveZip => InstallerKind.Zip,
            _ when lower.EndsWith(".msix", StringComparison.Ordinal) || lower.EndsWith(".msixbundle", StringComparison.Ordinal)
                => InstallerKind.Msix,
            _ => InstallerKind.Unknown, // EXE format does not establish its installer family.
        };
        return new Asset
        {
            InstallerId = asset.TryGetProperty("id", out var id) ? id.ToString() : name,
            ReleaseKey = releaseKey,
            Name = name,
            Size = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? Math.Max(0, bytes) : 0,
            ContentType = Text(asset, "content_type"),
            DownloadUrl = downloadUrl,
            ApiUrl = SafeHttps(Text(asset, "url")),
            Digest = Text(asset, "digest"),
            DownloadCount = Number(asset, "download_count"),
            Kind = kind,
            Arch = arch,
            Installer = installer,
        };
    }

    private static AssetKind Classify(string name)
    {
        if (name.Contains("checksum", StringComparison.Ordinal) || name.StartsWith("sha256", StringComparison.Ordinal)
            || name.EndsWith(".sha256", StringComparison.Ordinal) || name.EndsWith(".sha512", StringComparison.Ordinal)
            || name.EndsWith(".md5", StringComparison.Ordinal)) return AssetKind.Checksum;
        if (name.EndsWith(".sig", StringComparison.Ordinal) || name.EndsWith(".asc", StringComparison.Ordinal)) return AssetKind.Signature;
        if (name.StartsWith("source", StringComparison.Ordinal) || name.Contains("-source.", StringComparison.Ordinal)
            || name.Contains("-sources.", StringComparison.Ordinal)) return AssetKind.SourceArchive;
        if (name.EndsWith(".msi", StringComparison.Ordinal)) return AssetKind.InstallerMsi;
        if (name.EndsWith(".exe", StringComparison.Ordinal)) return AssetKind.InstallerExe;
        if (name.EndsWith(".zip", StringComparison.Ordinal)) return AssetKind.ArchiveZip;
        if (name.EndsWith(".7z", StringComparison.Ordinal)) return AssetKind.Archive7z;
        return AssetKind.Unknown;
    }

    private static bool IsWindowsCandidate(Asset asset)
    {
        if (asset.Kind is AssetKind.InstallerExe or AssetKind.InstallerMsi || asset.Installer == InstallerKind.Msix) return true;
        return asset.Kind is AssetKind.ArchiveZip or AssetKind.Archive7z
            && (asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || asset.Name.Contains("win-", StringComparison.OrdinalIgnoreCase)
                || asset.Name.Contains("win_", StringComparison.OrdinalIgnoreCase));
    }

    private static string RequiredString(JsonElement value, string name)
        => Text(value, name) is { Length: > 0 } text ? text : throw new RepositoryCatalogException("invalid_response");
    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int Number(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? Math.Max(0, number) : 0;
    private static bool Bool(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static DateTimeOffset Date(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && property.TryGetDateTimeOffset(out var date) ? date : default;
    private static string? SafeHttps(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    private static string? Truncate(string? value, int limit) => value?.Length > limit ? value[..limit] : value;
}
