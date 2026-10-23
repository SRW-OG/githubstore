using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class DefaultAssetSelectionPolicyTests
{
    [Fact]
    public void Select_OrdersPortableArchivesThenPortableExeThenExeThenMsi()
    {
        var msi = Asset("app-win-x64.msi");
        var exe = Asset("app-win-x64.exe");
        var portableExe = Asset("app-win-x64-single.exe") with { Kind = AssetKind.PortableExe };
        var zip = Asset("app-win-x64-portable.zip");
        var sevenZip = Asset("app-win-x64-portable.7z");
        Assert.Same(msi, Select(msi));
        Assert.Same(exe, Select(msi, exe));
        Assert.Same(portableExe, Select(msi, exe, portableExe));
        Assert.Same(zip, Select(msi, exe, portableExe, zip));
        Assert.Same(sevenZip, Select(msi, exe, portableExe, sevenZip));
    }

    [Fact]
    public void Select_ArchitectureConfidencePrecedesFormatAndRejectsIncompatibleAssets()
    {
        var incompatible = Asset("app-win-arm64-portable.zip");
        var unknown = Asset("app-win-portable.zip");
        var exact = Asset("app-win-amd64.msi");
        Assert.Same(exact, Select(incompatible, unknown, exact));
        Assert.Same(unknown, Select(incompatible, unknown));
        Assert.Null(Select(incompatible));
        Assert.Null(DefaultAssetSelectionPolicy.Select([Asset("app-win-x86.exe")], Arch.Arm64));
    }

    [Theory]
    [InlineData("x86_64", Arch.Amd64)]
    [InlineData("X86-64", Arch.Amd64)]
    [InlineData("AMD64", Arch.Amd64)]
    [InlineData("x64", Arch.Amd64)]
    [InlineData("win64", Arch.Amd64)]
    [InlineData("ARM64", Arch.Arm64)]
    [InlineData("aarch64", Arch.Arm64)]
    [InlineData("arm-64", Arch.Arm64)]
    [InlineData("x86", Arch.X86)]
    [InlineData("win32", Arch.X86)]
    [InlineData("i386", Arch.X86)]
    [InlineData("i686", Arch.X86)]
    public void Select_InfersDelimitedArchitectureFromUnknownMetadata(string token, Arch architecture)
    {
        var asset = Asset($"app-{token}.exe");
        Assert.Equal(Arch.Unknown, asset.Arch);
        Assert.Equal(architecture, DefaultAssetSelectionPolicy.InferArchitecture(asset));
        Assert.Same(asset, DefaultAssetSelectionPolicy.Select([asset], architecture));
        foreach (var other in new[] { Arch.Amd64, Arch.Arm64, Arch.X86 }.Where(a => a != architecture))
            Assert.Null(DefaultAssetSelectionPolicy.Select([asset], other));
    }

    [Fact]
    public void Select_UsesFilenameToCorrectLegacyX86Underscore64Metadata()
    {
        var asset = Asset("rustdesk-1.5.0-x86_64.exe") with { Arch = Arch.X86 };
        Assert.Equal(Arch.Amd64, DefaultAssetSelectionPolicy.InferArchitecture(asset));
        Assert.Same(asset, Select(asset));
        Assert.Null(DefaultAssetSelectionPolicy.Select([asset], Arch.X86));
    }

    [Fact]
    public void Select_UsesMetadataWhenFilenameHasNoArchitecture()
    {
        var asset = Asset("app.exe") with { Arch = Arch.Arm64 };
        Assert.Equal(Arch.Arm64, DefaultAssetSelectionPolicy.InferArchitecture(asset));
        Assert.Null(Select(asset));
        Assert.Same(asset, DefaultAssetSelectionPolicy.Select([asset], Arch.Arm64));
    }

    [Theory]
    [InlineData("myamd64tool.exe")]
    [InlineData("tool-arm64ish.exe")]
    [InlineData("prefixx86.exe")]
    public void InferArchitecture_DoesNotMatchArchitectureInsideWords(string name)
        => Assert.Equal(Arch.Unknown, DefaultAssetSelectionPolicy.InferArchitecture(Asset(name)));

    [Theory]
    [InlineData("linux")]
    [InlineData("macOS")]
    [InlineData("darwin")]
    [InlineData("source")]
    [InlineData("sources")]
    [InlineData("debug")]
    [InlineData("symbols")]
    [InlineData("PDBs")]
    [InlineData("SDK")]
    [InlineData("checksum")]
    [InlineData("sha256")]
    [InlineData("signature")]
    [InlineData("patch")]
    [InlineData("delta")]
    [InlineData("update-only")]
    [InlineData("updater")]
    public void Select_ExcludesNonWindowsAndAuxiliaryArtifactsEvenWhenMarkedPortable(string token)
    {
        var asset = Asset($"app-win-x64-{token}-portable.zip");
        Assert.Null(Select(asset));
        Assert.False(DefaultAssetSelectionPolicy.IsRecognizedPortable(asset));
    }

    [Theory]
    [InlineData(AssetKind.Checksum)]
    [InlineData(AssetKind.Signature)]
    [InlineData(AssetKind.SourceArchive)]
    public void Select_RespectsExcludedMetadataEvenWithOrdinaryFilename(AssetKind kind)
        => Assert.Null(Select(Asset("app-win-x64.exe") with { Kind = kind }));

    [Theory]
    [InlineData("sourcegraph")]
    [InlineData("debugger")]
    [InlineData("macaroni")]
    [InlineData("updaterunner")]
    public void Select_DoesNotExcludeProductNameSubstrings(string product)
    {
        var asset = Asset($"{product}-win-x64.exe");
        Assert.Same(asset, Select(asset));
    }

    [Theory]
    [InlineData("app-win-x64.zip")]
    [InlineData("app-win-x64.7z")]
    [InlineData("app-linux-x64-portable.zip")]
    [InlineData("app-x64-portable.zip")]
    [InlineData("app-win-x64-portability.zip")]
    [InlineData("app-win-x64.zip.sig")]
    [InlineData("app-win-x64-portable.tar.gz")]
    public void Select_DoesNotTreatGenericOrUnsupportedArchivesAsWindowsPortable(string name)
    {
        var asset = Asset(name);
        Assert.False(DefaultAssetSelectionPolicy.IsRecognizedPortable(asset));
        Assert.Null(Select(asset));
    }

    [Theory]
    [InlineData("portable")]
    [InlineData("STANDALONE")]
    [InlineData("noinstall")]
    public void Select_RecognizesPortableMarkerTokens(string marker)
    {
        var asset = Asset($"app-win-x64-{marker}.zip");
        Assert.True(DefaultAssetSelectionPolicy.IsRecognizedPortable(asset));
        Assert.Same(asset, Select(asset));
    }

    [Theory]
    [InlineData("obsproject/obs-studio", "OBS-Studio-32.2.2-Windows-x64.zip")]
    [InlineData("LOCALSEND/LOCALSEND", "LocalSend-1.18.2-windows-x86-64.zip")]
    [InlineData("NickeManarin/ScreenToGif", "ScreenToGif.2.43.2.Light.Portable.x64.zip")]
    [InlineData("cxasm/notepad--", "Notepad--v3.9.1-win10-portable.zip")]
    [InlineData("pbatard/rufus", "rufus-4.15p.exe")]
    public void Select_RecognizesNarrowOfflineCatalogConventions(string repository, string filename)
    {
        var asset = Asset(filename);
        Assert.True(DefaultAssetSelectionPolicy.IsRecognizedPortable(asset, repository));
        Assert.Same(asset, DefaultAssetSelectionPolicy.Select([asset], Arch.Amd64, repository));
    }

    [Fact]
    public void Select_CuratedRulesRequireMatchingRepositoryAndProductShape()
    {
        var obs = Asset("OBS-Studio-32.2.2-Windows-x64.zip");
        Assert.Null(Select(obs));
        Assert.Null(DefaultAssetSelectionPolicy.Select([obs], Arch.Amd64, "someone/obs-studio"));
        Assert.Null(DefaultAssetSelectionPolicy.Select([Asset("unrelated-win-x64.zip")], Arch.Amd64, "obsproject/obs-studio"));
        Assert.Null(DefaultAssetSelectionPolicy.Select([Asset("GroupPolicyObjectFiles-0.101.2362.0.zip")], Arch.Amd64, "microsoft/powertoys"));
        Assert.False(DefaultAssetSelectionPolicy.IsRecognizedPortable(Asset("rufus-4.15.exe"), "pbatard/rufus"));
    }

    [Fact]
    public void Select_RejectsAmbiguousArchitectureAndHandlesUnknownTarget()
    {
        Assert.Null(Select(Asset("app-win-x64-arm64.exe")));
        var asset = Asset("app-win-arm64.exe");
        Assert.Same(asset, DefaultAssetSelectionPolicy.Select([asset], Arch.Unknown));
    }

    [Fact]
    public void Select_IsDeterministicAcrossInputOrderAndPreservesOriginalObjectsAndList()
    {
        var a = Asset("app-win-x64.exe") with { InstallerId = "a" };
        var b = a with { InstallerId = "b" };
        var assets = new List<Asset> { b, a, Asset("z-win-x64.exe") };
        var original = assets.ToArray();
        Assert.Same(a, DefaultAssetSelectionPolicy.Select(assets, Arch.Amd64));
        Assert.Same(a, DefaultAssetSelectionPolicy.Select(assets.AsEnumerable().Reverse(), Arch.Amd64));
        Assert.Equal(original, assets);
        Assert.All(assets, asset => Assert.Contains(original, candidate => ReferenceEquals(asset, candidate)));
    }

    [Fact]
    public void Select_HandlesEmptyAndRejectsNullInput()
    {
        Assert.Null(Select());
        Assert.Throws<ArgumentNullException>(() => DefaultAssetSelectionPolicy.Select(null!, Arch.Amd64));
    }

    [Fact]
    public void Select_PrefersGreenArchivePairedWithSameStemInstaller()
    {
        // 真实案例：KeePassXC 同时发行同名 zip 与 msi，zip 无 windows/portable 标记。
        var zip = Asset("KeePassXC-2.8.0-x64.zip");
        var msi = Asset("KeePassXC-2.8.0-x64.msi");
        Assert.Same(zip, Select(zip, msi));
        Assert.Same(zip, Select(msi, zip));
    }

    [Fact]
    public void Select_PairedArchiveBeatsPlainExeButNotExplicitPortableMarkers()
    {
        var zip = Asset("TriliumNotes-v0.106.0-windows-x64.zip");
        var exe = Asset("TriliumNotes-v0.106.0-windows-x64.exe");
        Assert.Same(zip, Select(exe, zip));
        var marked = Asset("App-1.0-win-x64-portable.zip");
        Assert.Same(marked, Select(zip, exe, marked));
    }

    [Fact]
    public void Select_PairsGreenArchiveWithInstallerNamedSetupInSameRelease()
    {
        // 真实案例：Paseo 的绿色包是 Paseo-0.12.0-x64.zip，安装器多了 Setup 字样。
        var zip = Asset("Paseo-0.12.0-x64.zip");
        var setupExe = Asset("Paseo-Setup-0.12.0-x64.exe");
        Assert.Same(zip, Select(setupExe, zip));
        Assert.Same(zip, Select(zip, setupExe));
    }

    [Fact]
    public void Select_DoesNotTreatZippedInstallerAsGreenBuild()
    {
        // “Setup” 压缩包是安装器打包，不是绿色版，仍应让位于安装器本身。
        var zippedSetup = Asset("Paseo-Setup-0.12.0-x64.zip");
        var setupExe = Asset("Paseo-Setup-0.12.0-x64.exe");
        Assert.Same(setupExe, Select(zippedSetup, setupExe));
    }

    [Fact]
    public void Select_UnpairedArchiveDoesNotBecomeTheRecommendation()
    {
        // 发行版里没有对应安装器的压缩包仍缺少绿色分发证据，不据此推荐。
        var unpaired = Asset("App-1.0-win-x64.zip");
        var otherProduct = Asset("Other-1.0-win-x64.exe");
        Assert.Same(otherProduct, Select(unpaired, otherProduct));
    }

    [Fact]
    public void Select_RequiresMatchingArchitectureForPairing()
    {
        var armZip = Asset("App-1.0-win-arm64.zip");
        var x64Exe = Asset("App-1.0-win-x64.exe");
        Assert.Same(x64Exe, Select(armZip, x64Exe));
        var matchingZip = Asset("App-1.0-win-x64.zip");
        Assert.Same(matchingZip, Select(armZip, x64Exe, matchingZip));
    }

    private static Asset? Select(params Asset[] assets)
        => DefaultAssetSelectionPolicy.Select(assets, Arch.Amd64);

    private static Asset Asset(string name) => new()
    {
        InstallerId = name,
        ReleaseKey = "stable:1.0",
        Name = name,
        DownloadUrl = $"https://example.invalid/{name}",
    };
}
