using System.Text.RegularExpressions;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Catalog;

/// <summary>
/// Recommends Windows release assets from metadata only. A recommendation does not
/// verify archive contents, safety, install switches, or portable capabilities.
/// </summary>
public static class DefaultAssetSelectionPolicy
{
    // Token boundaries keep product names such as sourcegraph and debugger eligible.
    private static readonly Regex ExcludedTokens = Tokens(
        "linux|ubuntu|debian|fedora|centos|appimage|android|mac|macos|osx|darwin|ios|" +
        "source|sources|src|debug|symbols|symbol|pdb|pdbs|dsym|dsyms|sdk|" +
        "checksum|checksums|sha1|sha256|sha512|md5|signature|signatures|sig|asc|" +
        "patch|patches|delta|update|updates|updater|updateonly");
    private static readonly Regex WindowsTokens = Tokens("windows|win|win32|win64|win7|win8|win10|win11");
    private static readonly Regex PortableTokens = Tokens("portable|standalone|noinstall");
    private static readonly Regex Amd64Tokens = Tokens("x86[_-]64|x64|amd64|win64");
    private static readonly Regex Arm64Tokens = Tokens("arm[_-]?64|aarch64");
    private static readonly Regex X86Tokens = Tokens("x86|win32|i[3-6]86");
    // An archive whose stem names an installer wraps that installer; it is not a green build.
    private static readonly Regex InstallerStemTokens = Tokens("setup|install|installer|webinstall|online");

    /// <summary>
    /// Selects an exact architecture match before an unknown architecture, then
    /// portable ZIP/7z, portable EXE, EXE, MSI. Explicit mismatches are never used
    /// as emulation fallbacks. Returns null when no supported candidate exists.
    /// </summary>
    public static Asset? Select(IEnumerable<Asset> assets, Arch architecture, string? repositoryKey = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var materialized = assets as IReadOnlyCollection<Asset> ?? assets.ToArray();
        // 同一发行版内与 EXE/MSI 同名的压缩包属于该产品的绿色分发件，优先于安装器。
        var pairedArchives = PairedArchiveNames(materialized);
        return materialized.Select(asset => (Asset: asset, Rank: Rank(asset, architecture, repositoryKey, pairedArchives)))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Asset.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Asset.InstallerId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Asset.DownloadUrl, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Asset.ReleaseKey, StringComparer.Ordinal)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault();
    }

    /// <summary>
    /// Names of ZIP/7z assets that are the product's own green distribution of an
    /// installer shipped in the same release. Matching uses an identity that drops
    /// installer words, so "App-1.0-x64.zip" pairs with "App-Setup-1.0-x64.exe".
    /// An archive whose own name marks it as an installer is excluded: that archive
    /// wraps the installer rather than providing a runnable green build.
    /// </summary>
    private static HashSet<string> PairedArchiveNames(IReadOnlyCollection<Asset> assets)
    {
        var installerIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (Extension(asset) is ".exe" or ".msi") installerIdentities.Add(ProductIdentity(Stem(asset.Name)));
        }
        var paired = new HashSet<string>(StringComparer.Ordinal);
        if (installerIdentities.Count == 0) return paired;
        foreach (var asset in assets)
        {
            if (Extension(asset) is not (".zip" or ".7z")) continue;
            var stem = Stem(asset.Name);
            if (InstallerStemTokens.IsMatch(stem)) continue;
            if (installerIdentities.Contains(ProductIdentity(stem))) paired.Add(asset.Name);
        }
        return paired;
    }

    private static string Stem(string name) => Path.GetFileNameWithoutExtension(name);

    /// <summary>Normalized product identity: installer words removed, separators collapsed.</summary>
    private static string ProductIdentity(string stem)
    {
        var withoutInstallerWords = InstallerStemTokens.Replace(stem, "-");
        return Regex.Replace(withoutInstallerWords, "[^a-z0-9]+", "-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Trim('-').ToLowerInvariant();
    }

    /// <summary>
    /// Filename evidence takes precedence over metadata (which may have classified
    /// x86_64 as x86). Conflicting filename tokens yield Unknown; selection rejects
    /// such ambiguous files. This method makes no claim about runnable contents.
    /// </summary>
    public static Arch InferArchitecture(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var detected = FilenameArchitectures(asset.Name);
        return detected.Count == 1 ? detected[0] : detected.Count > 1 ? Arch.Unknown : asset.Arch;
    }

    /// <summary>
    /// Indicates a supported Windows portable naming convention, not a verified
    /// install capability. Generic compressed files are never portable by format alone.
    /// </summary>
    public static bool IsRecognizedPortable(Asset asset, string? repositoryKey = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (IsExcluded(asset)) return false;
        var extension = Extension(asset);
        if (extension == ".exe")
            return asset.Kind == AssetKind.PortableExe || asset.Installer == InstallerKind.Portable
                || PortableTokens.IsMatch(asset.Name) || IsCuratedRufusPortable(asset.Name, repositoryKey);
        if (extension is not (".zip" or ".7z")) return false;
        return IsWindowsArchive(asset.Name, repositoryKey)
            && (PortableTokens.IsMatch(asset.Name) || asset.Installer == InstallerKind.Portable
                || IsCuratedArchive(asset.Name, repositoryKey));
    }

    private static int Rank(Asset asset, Arch architecture, string? repositoryKey, HashSet<string> pairedArchives)
    {
        if (IsExcluded(asset)) return -1;
        var detected = FilenameArchitectures(asset.Name);
        // A multi-architecture name is ambiguous, not evidence of a universal binary.
        if (detected.Count > 1) return -1;
        var inferred = InferArchitecture(asset);
        if (architecture != Arch.Unknown && inferred != Arch.Unknown && inferred != architecture) return -1;
        var format = Extension(asset) switch
        {
            ".zip" or ".7z" when IsRecognizedPortable(asset, repositoryKey) => 0,
            // 与同一发行版 EXE/MSI 同名的压缩包是该产品的绿色分发件，优先于安装类文件。
            ".zip" or ".7z" when pairedArchives.Contains(asset.Name) => 0,
            ".exe" when IsRecognizedPortable(asset, repositoryKey) => 1,
            ".exe" => 2,
            ".msi" => 3,
            _ => -1,
        };
        if (format < 0) return -1;
        return (architecture != Arch.Unknown && inferred == Arch.Unknown ? 10 : 0) + format;
    }

    private static bool IsExcluded(Asset asset)
        => asset.Kind is AssetKind.Checksum or AssetKind.Signature or AssetKind.SourceArchive
            || ExcludedTokens.IsMatch(asset.Name);

    private static List<Arch> FilenameArchitectures(string name)
    {
        var result = new List<Arch>(3);
        if (Amd64Tokens.IsMatch(name)) result.Add(Arch.Amd64);
        if (Arm64Tokens.IsMatch(name)) result.Add(Arch.Arm64);
        // Consume the complete x86_64/x86-64 token before looking for x86.
        if (X86Tokens.IsMatch(Amd64Tokens.Replace(name, " "))) result.Add(Arch.X86);
        return result;
    }

    private static bool IsWindowsArchive(string name, string? repositoryKey)
        => WindowsTokens.IsMatch(name) || IsCuratedArchive(name, repositoryKey)
            || IsRepository(repositoryKey, "nickemanarin/screentogif")
                && Regex.IsMatch(name, @"^ScreenToGif\.\d+(?:\.\d+)+\.(?:Light\.)?Portable\.(?:Arm64|x64|x86)\.zip$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsCuratedArchive(string name, string? repositoryKey)
    {
        // Exact product/platform/version shapes from the offline catalog. These
        // rules deliberately do not bless every archive in a known repository.
        if (IsRepository(repositoryKey, "obsproject/obs-studio"))
            return Regex.IsMatch(name, @"^OBS-Studio-\d+(?:\.\d+)+-Windows-(?:x64|arm64)\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "localsend/localsend"))
            return Regex.IsMatch(name, @"^LocalSend-\d+(?:\.\d+)+-windows-(?:x86-64|arm-64)\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return false;
    }

    private static bool IsCuratedRufusPortable(string name, string? repositoryKey)
        => IsRepository(repositoryKey, "pbatard/rufus")
            && Regex.IsMatch(name, @"^rufus-\d+(?:\.\d+)+p\.exe$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsRepository(string? actual, string expected)
        => string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string Extension(Asset asset) => Path.GetExtension(asset.Name).ToLowerInvariant();

    private static Regex Tokens(string alternatives)
        => new($@"(?<![a-z0-9])(?:{alternatives})(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
