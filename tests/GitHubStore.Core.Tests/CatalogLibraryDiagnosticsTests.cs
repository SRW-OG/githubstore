using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 库自检只依据库里已有的发行快照，不联网、不安装；它报告的是配置问题，不是安全结论。
/// </summary>
public sealed class CatalogLibraryDiagnosticsTests
{
    [Fact]
    public void PolicyThatMatchesNothingIsReportedWithItsRule()
    {
        var entry = Detail("https://example.com/Tool.zip", digest: "sha256:" + new string('a', 64)) with
        {
            PackagePolicy = new PackagePolicy
            {
                InstallFormat = PackageInstallFormat.PortableArchive,
                AssetRules = [new PackageAssetRule { Architecture = Arch.Amd64, Include = ["Nope-*.zip"] }],
            },
        };
        var diagnostic = Assert.Single(CatalogLibraryDiagnostics.Inspect([entry], Arch.Amd64));
        Assert.Equal(LibraryDiagnosticKind.NoMatchingAsset, diagnostic.Kind);
        Assert.Equal("Nope-*.zip", diagnostic.Detail);
        Assert.Equal("owner/tool", diagnostic.RepositoryKey);
    }

    [Fact]
    public void EntryWithoutAnyReleaseIsReportedAsBrowseOnly()
    {
        var entry = new AppDetail { App = App() };
        var diagnostic = Assert.Single(CatalogLibraryDiagnostics.Inspect([entry], Arch.Amd64));
        Assert.Equal(LibraryDiagnosticKind.NoRelease, diagnostic.Kind);
    }

    [Fact]
    public void PinnedTagThatIsMissingIsReportedSeparatelyFromNoRelease()
    {
        var entry = Detail("https://example.com/Tool.zip", digest: "sha256:x") with
        {
            PackagePolicy = new PackagePolicy { ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "v99" },
        };
        var diagnostic = Assert.Single(CatalogLibraryDiagnostics.Inspect([entry], Arch.Amd64));
        Assert.Equal(LibraryDiagnosticKind.PinnedTagMissing, diagnostic.Kind);
        Assert.Equal("v99", diagnostic.Detail);
    }

    [Fact]
    public void MissingDigestAndUnsafeUrlAreBothReported()
    {
        var entry = Detail("https://user:pass@example.com/Tool.zip", digest: null) with
        {
            PackagePolicy = new PackagePolicy { InstallFormat = PackageInstallFormat.PortableArchive },
        };
        var kinds = CatalogLibraryDiagnostics.Inspect([entry], Arch.Amd64).Select(d => d.Kind).ToArray();
        Assert.Contains(LibraryDiagnosticKind.MissingDigest, kinds);
        Assert.Contains(LibraryDiagnosticKind.UnsafeDownloadUrl, kinds);
    }

    [Fact]
    public void WellConfiguredEntryProducesNoDiagnostics()
    {
        var entry = Detail("https://example.com/Tool.zip", digest: "sha256:" + new string('b', 64)) with
        {
            PackagePolicy = new PackagePolicy
            {
                InstallFormat = PackageInstallFormat.PortableArchive,
                AssetRules = [new PackageAssetRule { Architecture = Arch.Amd64, Include = ["Tool-*.zip"] }],
            },
        };
        Assert.Empty(CatalogLibraryDiagnostics.Inspect([entry], Arch.Amd64));
    }

    private static AppDetail Detail(string url, string? digest)
    {
        var app = App();
        var asset = new Asset
        {
            InstallerId = "asset", ReleaseKey = "stable:v1", Name = "Tool-1.0-windows-x64-portable.zip",
            DownloadUrl = url, Digest = digest, Size = 10, Arch = Arch.Amd64,
            Kind = AssetKind.ArchiveZip, Installer = InstallerKind.Portable,
        };
        var release = new Release { AppId = app.Id, Key = "stable:v1", TagName = "v1", Assets = [asset] };
        return new AppDetail { App = app, Releases = [release] };
    }

    private static GitHubStore.Core.Abstractions.App App() => new()
    {
        Id = new(new(Guid.NewGuid()), new("owner/tool")), SourceRevision = 1, DisplayName = "Tool",
        Owner = "owner", Repo = "tool",
    };
}
