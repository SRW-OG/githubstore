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
    // Platform words carry no product identity: "Recordly-x64.zip" and
    // "Recordly-windows-x64.exe" are the same product for one release.
    private static readonly Regex PlatformTokens = Tokens("windows|win|win32|win64|win7|win8|win10|win11");
    // Architecture synonyms, canonicalized so "…-x86_64.zip" pairs with "…-windows-x64.exe".
    private static readonly Regex Amd64Canonical = Tokens("x86[_-]64|x64|amd64|win64");
    private static readonly Regex Arm64Canonical = Tokens("arm[_-]?64|aarch64");
    private static readonly Regex X86Canonical = Tokens("x86|win32|i[3-6]86");

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

    /// <summary>
    /// Normalized product identity for pairing. Installer, portable and platform words are
    /// removed and architecture synonyms are canonicalized, so a product's green archive is
    /// recognized as such even when only one of the two names carries those words
    /// (for example "DBX_0.6.51_x64-portable.zip" and "DBX_0.6.51_x64-setup.exe").
    /// </summary>
    private static string ProductIdentity(string stem)
    {
        var canonical = Amd64Canonical.Replace(stem, "x64");
        canonical = Arm64Canonical.Replace(canonical, "arm64");
        canonical = X86Canonical.Replace(canonical, "x86");
        canonical = InstallerStemTokens.Replace(canonical, "-");
        canonical = PortableTokens.Replace(canonical, "-");
        canonical = PlatformTokens.Replace(canonical, "-");
        return Regex.Replace(canonical, "[^a-z0-9]+", "-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
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
        // 同一格式内，用户级安装器排在机器级之前：两者都能装，但前者不请求管理员权限。
        // 最大 format*2+sub 为 7，仍小于下方“架构未知”的 10，不改变既有的架构优先关系。
        var scope = IsUserScopeInstaller(asset.Name) ? 0 : 1;
        return (architecture != Arch.Unknown && inferred == Arch.Unknown ? 10 : 0) + format * 2 + scope;
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
        // 以下仓库的绿色分包名与安装器名差异较大，无法由配对规则识别，按精确产品形状登记。
        if (IsRepository(repositoryKey, "dail8859/notepadnext"))
            return Regex.IsMatch(name, @"^NotepadNext-v[\d.]+-win64\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "winsiderss/systeminformer"))
            // 发行包名有 …-release-bin.zip 与 …-bin.zip 两种写法，都是同一绿色分发包。
            return Regex.IsMatch(name, @"^systeminformer-[\d.]+(?:-release)?-bin\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "qtgolang/sunnynettools"))
            return Regex.IsMatch(name, @"^Windows\.SunnyNetTools\.7z$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "oop7/ytsage"))
            return Regex.IsMatch(name, @"^YTSage-v[\d.]+(?:-ffmpeg)?-portable\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // 绿色包名不带 portable/平台词，且发行版里没有可配对的安装器，按精确形状登记。
        if (IsRepository(repositoryKey, "hrydgard/ppsspp"))
            return Regex.IsMatch(name, @"^PPSSPP-v[\d.]+-Windows-x64\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "esengine/deepseek-reasonix"))
            return Regex.IsMatch(name, @"^reasonix-windows-amd64\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "naibowang/easyspider"))
            return Regex.IsMatch(name, @"^EasySpider_[\d.]+_Windows_x64\.7z$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "barry-ran/qtscrcpy"))
            return Regex.IsMatch(name, @"^QtScrcpy-win-x64-v[\d.]+\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "duan602728596/48tools"))
            return Regex.IsMatch(name, @"^48tools-[\d.]+-win64\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "perlh/wavely"))
            // 发行版另有 …-installer-amd64.zip，属于安装器打包，不在绿色包形状内。
            return Regex.IsMatch(name, @"^Wavely-[\d.]+-windows-amd64\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "curiouslearnerdev/online_tools"))
            return Regex.IsMatch(name, @"^windows_x64_[\d.]+\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (IsRepository(repositoryKey, "jar-analyzer/jar-analyzer"))
            // windows-full 内含运行环境，开箱可用；-25 与 -system 需要额外的 Java 环境。
            return Regex.IsMatch(name, @"^jar-analyzer-[\d.]+-windows-full\.zip$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return false;
    }

    /// <summary>
    /// 按用户的推荐策略挑选“用户级”安装器：个别发行版同时提供机器级与用户级安装器
    /// （PowerToys 的 PowerToysSetup 需要管理员，PowerToysUserSetup 不需要），
    /// 默认应选按当前用户安装的那个。只识别明确的用户级命名，不做模糊猜测。
    /// </summary>
    /// 命名可能是驼峰拼接（PowerToysUserSetup），中间没有分隔符，因此这里不使用词边界，
    /// 只按“user + 安装词”这一实际出现的写法匹配。
    private static readonly Regex UserScopeMarker = new(
        @"user[-_. ]?(?:setup|install|scope)|per[-_. ]?user",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsUserScopeInstaller(string name) => UserScopeMarker.IsMatch(name);

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
