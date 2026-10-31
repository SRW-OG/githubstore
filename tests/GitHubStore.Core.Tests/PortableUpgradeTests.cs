using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using GitHubStore.App.Services.Installation;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;
using Version = GitHubStore.Core.Abstractions.Version;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 版本比较纯逻辑。这里只断言"能不能判定"和方向，不断言任何 UI 文案。
/// 贯穿始终的取向：无法比较一律 <see cref="VersionRelation.Different"/>，绝不 Newer。
/// </summary>
[Trait("Category", "Unit")]
public sealed class VersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("V1.0.0", "v1.0.0")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("1.0.0-RC1", "1.0.0-rc1")]
    public void EqualVersionsNormalizeCasePrefixAndMissingSegments(string installed, string candidate) =>
        Assert.Equal(VersionRelation.Same, VersionComparer.RelateText(installed, candidate));

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2")]
    [InlineData("20240115", "2024.01.16")]
    [InlineData("v2.3.4", "V2.3.5")]
    public void NewerCandidatesAreNewer(string installed, string candidate) =>
        Assert.Equal(VersionRelation.Newer, VersionComparer.RelateText(installed, candidate));

    [Theory]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.0.0", "1.0.0-alpha")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("20240115", "2023.12.31")]
    public void OlderCandidatesAreOlderAndNeverNewer(string installed, string candidate)
    {
        Assert.Equal(VersionRelation.Older, VersionComparer.RelateText(installed, candidate));
        Assert.NotEqual(VersionRelation.Newer, VersionComparer.RelateText(installed, candidate));
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1")]
    [InlineData("20240115", "20240115")]
    public void PrereleaseAndDateFormsAreComparable(string installed, string candidate) =>
        Assert.True(VersionComparer.CompareText(installed, candidate).Comparable);

    [Theory]
    // 完全不同的不可解析文本：只能得出"不同"，不能得出"更新"。
    [InlineData("nightly", "canary")]
    [InlineData("nightly-2024", "nightly-2025")]
    [InlineData("release", "release-2")]
    // 一侧可解析、另一侧不可解析：不可比较。
    [InlineData("1.0.0", "latest")]
    [InlineData("1.0.0", "2024")]
    [InlineData("1.0.0", "")]
    [InlineData("1.0.0", null)]
    // 数字方案与日期方案之间没有可靠序关系。
    [InlineData("1.0.0", "2024.01.15")]
    [InlineData("2024.01.15", "1.0.0")]
    // 歧义/越界形式：宁可判为不同，也不猜。
    [InlineData("1.0.0", "1.0.0.0.1")]
    [InlineData("1.0.0", "1.0.0-")]
    [InlineData("1.0.0", "1.0.0-a..b")]
    public void UnparsableAndMismatchedVersionsAreDifferentNeverNewer(string installed, string? candidate)
    {
        var relation = VersionComparer.RelateText(installed, candidate);
        Assert.Equal(VersionRelation.Different, relation);
        Assert.NotEqual(VersionRelation.Newer, relation);
        var comparison = VersionComparer.CompareText(installed, candidate);
        Assert.False(comparison.Comparable);
        Assert.Equal(0, comparison.Result);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("build-42")]
    [InlineData("release")]
    public void OpaqueTextIsNeverComparableEvenAgainstItself(string text)
    {
        // 不可解析文本一律"不可比较"，即使两段文本完全相同：没有任何依据说明
        // "nightly" 与 "nightly" 是同一发行版的不同版本，判定等价会让调用方误以为"已是最新"。
        // 调用方若要更新这种安装，只能由用户显式发起，并如实说明"版本无法比较"。
        var relation = VersionComparer.RelateText(text, text);
        Assert.Equal(VersionRelation.Different, relation);
        Assert.NotEqual(VersionRelation.Newer, relation);
        Assert.False(VersionComparer.CompareText("v" + text, text).Comparable);
    }

    [Fact]
    public void OpaqueInstalledReceiptUsesRawTextNotTheSchemeField()
    {
        // 本程序自己的安装流程写入 Opaque + 原始标签，因此不能把 Scheme 当成"声明不可比较"。
        var receipt = new Version { Raw = "1.4.2", Scheme = VersionScheme.Opaque };
        Assert.Equal(VersionRelation.Newer, VersionComparer.Relate(receipt, "1.4.3"));
    }

    [Fact]
    public void StructuredPartsAreOnlyUsedWhenRawTextIsUnparsable()
    {
        // Raw 可解析时以 Raw 为准：登记里过期的 Parts 不能覆盖它。
        var receipt = new Version { Raw = "2.0.0", Scheme = VersionScheme.Semver, Parts = new long[] { 1, 0, 0 } };
        Assert.Equal(VersionRelation.Newer, VersionComparer.Relate(receipt, "2.0.1"));

        // Raw 不可解析（例如被截断的标签）时，才退回登记里可信的结构化字段。
        var fallback = new Version { Raw = "v", Scheme = VersionScheme.Semver, Parts = new long[] { 1, 2, 0 } };
        Assert.Equal(VersionRelation.Newer, VersionComparer.Relate(fallback, "1.3.0"));
        Assert.Equal(VersionRelation.Older, VersionComparer.Relate(fallback, "1.1.0"));
        Assert.Equal(VersionRelation.Same, VersionComparer.Relate(fallback, "1.2.0.0"));
    }

    [Fact]
    public void CompareResultSignMeansCandidateMinusInstalled()
    {
        Assert.True(VersionComparer.CompareText("1.0.0", "1.0.1").Result > 0);
        Assert.True(VersionComparer.CompareText("1.0.1", "1.0.0").Result < 0);
        Assert.Equal(0, VersionComparer.CompareText("1.0.1", "1.0.1").Result);
    }

    [Fact]
    public void EmptyBuildMetadataIsNotVersions()
    {
        Assert.Equal(VersionRelation.Different, VersionComparer.RelateText("", "1.0.0"));
        Assert.Equal(VersionRelation.Different, VersionComparer.RelateText("   ", "1.0.0"));
        Assert.Equal(VersionRelation.Different, VersionComparer.RelateText("1.0.0", "+"));
        // 按 SemVer 规则，+build 元数据不参与比较：只有 "+" 没有核心版本仍然不是版本。
        Assert.Equal(VersionRelation.Same, VersionComparer.RelateText("1.0.0", "1.0.0+build.7"));
    }

    [Fact]
    public void ExtremelyLongVersionTextIsNotGuessed()
    {
        var huge = new string('9', 300);
        Assert.Equal(VersionRelation.Different, VersionComparer.RelateText("1.0.0", huge));
        // 单段超长数字不解析，避免溢出与臆测。
        Assert.Equal(VersionRelation.Different, VersionComparer.RelateText("1.0.0", "1.0." + new string('9', 20)));
    }

    [Fact]
    public void NumericPrereleaseIdentifiersSortNumericallyNotLexically()
    {
        // 字符串比较会把 alpha.10 排在 alpha.9 之前；数值比较才是对的。
        Assert.Equal(VersionRelation.Newer, VersionComparer.RelateText("1.0.0-alpha.9", "1.0.0-alpha.10"));
        Assert.Equal(VersionRelation.Older, VersionComparer.RelateText("1.0.0-alpha.10", "1.0.0-alpha.9"));
    }

    [Fact]
    public void NullInstalledVersionIsNotComparable()
    {
        var comparison = VersionComparer.Compare(null!, "1.0.0");
        Assert.False(comparison.Comparable);
        Assert.Equal(VersionRelation.Different, VersionComparer.Relate(null!, "1.0.0"));
    }
}

/// <summary>
/// 便携更新/回滚。全部使用真实临时目录与假下载器/假登记器，
/// 不联网、不启动任何进程、不接触真实用户安装。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PortableUpgradeTests
{
    [Fact]
    public async Task SuccessfulUpdateKeepsInstallIdAndPointsAtNewVersion()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "old");
        f.Download.Bytes = Zip(("Tool.exe", Pe("new")));
        var progress = new List<PortableUpgradePhase>();

        var result = await f.Service().UpdateAsync(f.Request("1.1.0"), new SyncProgress<PortableUpgradeProgress>(p => progress.Add(p.Phase)));

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(install.InstallId, result.InstallId);
        Assert.Equal("1.1.0", result.DisplayVersion);
        Assert.Equal(Pe("new"), await File.ReadAllBytesAsync(result.ExecutablePath!));
        Assert.Equal(PortableUpgradePhase.Completed, progress.Last());

        // 登记必须仍是同一条：InstallId / AppId / 来源身份 / 安装时间都不变。
        var receipt = Assert.Single(f.Launcher.Receipts);
        Assert.Equal(install.InstallId, receipt.InstallId);
        Assert.Equal(install.AppId, receipt.AppId);
        Assert.Equal(install.Source.SourceId, receipt.Source.SourceId);
        Assert.Equal(install.Source.PackageId, receipt.Source.PackageId);
        Assert.Equal(install.InstalledAt, receipt.InstalledAt);
        Assert.Equal("1.1.0", receipt.DisplayVersion);
        Assert.Equal("1.1.0", receipt.Version.Raw);
        Assert.Equal(result.ExecutablePath, receipt.ExePath);
        Assert.True(receipt.UpdatedAt >= install.UpdatedAt);
        // 同一个 InstallId 只有一条登记，更新不能变成"装了两份"。
        Assert.Single(await f.Launcher.ListAsync());
    }

    [Fact]
    public async Task SuccessfulUpdateLeavesNoStagingDirectoryAndDeletesTheOldVersion()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);

        // 新机制不保留本地旧版本：成功后暂存、备份、被替换目录与回退记录都必须消失。
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-stage-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-replaced-*"));
        Assert.Empty(RollbackRecords(f));
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));
        // journal 只在更新进行中存在，成功后必须消失。
        Assert.False(f.Service().HasPendingRecovery(f.InstallId));
    }

    [Fact]
    public async Task OlderCandidateIsRefusedAndNothingChanges()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("2.0.0", "Tool.exe", "current");
        f.Download.Bytes = Zip(("Tool.exe", Pe("downgrade")));

        var result = await f.Service().UpdateAsync(f.Request("1.0.0"));

        Assert.Equal(PortableUpgradeErrorCodes.NotAnUpgrade, result.ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Equal("current", Text(f.Target, "Tool.exe"));
        // 登记没有被改动：版本与入口都还是旧的。
        Assert.Equal("2.0.0", Assert.Single(f.Launcher.Receipts).Version.Raw);
        Assert.Equal(install.ExePath, Assert.Single(f.Launcher.Receipts).ExePath);
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
    }

    [Fact]
    public async Task SameVersionIsRefusedWithoutReplacingAnything()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "current");
        f.Download.Bytes = Zip(("Tool.exe", Pe("same")));

        var result = await f.Service().UpdateAsync(f.Request("v1.0.0"));

        Assert.Equal(PortableUpgradeErrorCodes.AlreadyCurrent, result.ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Equal("current", Text(f.Target, "Tool.exe"));
    }

    [Fact]
    public async Task UnparsableVersionProceedsBecauseTheUpdateIsUserInitiated()
    {
        using var f = new Fixture();
        await f.SeedAsync("nightly", "Tool.exe", "old");
        f.Download.Bytes = Zip(("Tool.exe", Pe("new")));

        // 版本无法比较（Different）时既不谎报"有更新"，也不能反过来把用户挡在门外。
        var result = await f.Service().UpdateAsync(f.Request("canary"));

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(Pe("new"), await File.ReadAllBytesAsync(Path.Combine(f.Target, "Tool.exe")));
        Assert.Equal("canary", Assert.Single(f.Launcher.Receipts).DisplayVersion);
    }

    [Fact]
    public async Task NonPortableInstallerReceiptIsRefusedWithoutTouchingFiles()
    {
        using var f = new Fixture();
        // EXE/MSI 安装器的登记：不满足 StoreOwnedInstallation.OwnsExtractedFiles。
        var receipt = await f.SeedAsync("1.0.0", "Tool.exe", "installed", installer: InstallerKind.Inno, strategy: Strategy.Custom);
        f.Download.Bytes = Zip(("Tool.exe", Pe("new")));

        var result = await f.Service().UpdateAsync(f.Request("2.0.0"));

        Assert.Equal(PortableUpgradeErrorCodes.NotPortable, result.ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Equal("installed", Text(receipt.TargetDir!, "Tool.exe"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-stage-*"));
    }

    [Theory]
    // StoreOwnedInstallation.OwnsExtractedFiles 的判据是“Green 策略 或 Portable 安装方式”，
    // 因此这里覆盖的是两者都不满足的形状；(Portable, Custom) 其实满足条件，不属于本用例。
    [InlineData(InstallerKind.Msi, Strategy.Custom)]
    [InlineData(InstallerKind.Nsis, Strategy.Default)]
    [InlineData(InstallerKind.Inno, Strategy.Default)]
    [InlineData(InstallerKind.Squirrel, Strategy.Default)]
    [InlineData(InstallerKind.Burn, Strategy.Default)]
    [InlineData(InstallerKind.Zip, Strategy.Default)]
    [InlineData(InstallerKind.Unknown, Strategy.Default)]
    public async Task EveryNonStoreOwnedShapeIsRefused(InstallerKind installer, Strategy strategy)
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "installed", installer: installer, strategy: strategy);
        Assert.Equal(PortableUpgradeErrorCodes.NotPortable, (await f.Service().UpdateAsync(f.Request("2.0.0"))).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task UnknownInstallIdIsRefusedBeforeAnyDownload()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "installed");
        var request = f.Request("2.0.0") with { InstallId = "does-not-exist" };

        var result = await f.Service().UpdateAsync(request);

        Assert.Equal(PortableUpgradeErrorCodes.InstallationNotFound, result.ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task RequestForADifferentAppIdIsRefused()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "installed");
        var other = new CatalogApp
        {
            Id = new(new(Guid.NewGuid()), new("owner/other")), SourceRevision = 1,
            DisplayName = "Other", Owner = "owner", Repo = "other",
        };
        var asset = new Asset { InstallerId = "asset-1", ReleaseKey = "release-2", Name = "Tool.zip", DownloadUrl = "https://example.com/Tool.zip" };
        var request = new PortableUpgradeRequest
        {
            InstallId = install.InstallId, App = other,
            Release = new() { AppId = other.Id, Key = "release-2", TagName = "2.0.0", Assets = new[] { asset } },
            Asset = asset,
        };

        Assert.Equal(PortableUpgradeErrorCodes.IdentityMismatch, (await f.Service().UpdateAsync(request)).ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-stage-*"));
    }

    [Fact]
    public async Task RegistrationFailureRestoresPreviousFilesAndPreviousReceipt()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        f.Download.Bytes = Zip(("Tool.exe", Pe("replacement")));
        // 只拒绝“新版本”那次登记：还原写回旧登记必须能成功，否则测的就不是还原路径了。
        f.Launcher.OnRegister = installing =>
        {
            if (installing.Version.Raw == "1.1.0") throw new IOException("registration rejected");
        };

        var result = await f.Service().UpdateAsync(f.Request("1.1.0"));

        Assert.False(result.Succeeded);
        Assert.Equal(PortableUpgradeErrorCodes.RegistrationFailed, result.ErrorCode);
        Assert.True(result.FilesRestored);
        Assert.False(result.FilesMayRemain);
        // 文件回到旧版本。
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
        // 登记也回到旧版本：入口、版本、UpdatedAt 都与更新前完全一致。
        var receipt = Assert.Single(f.Launcher.Receipts);
        Assert.Equal(install, receipt);
        // 还原完成后不留备份与 journal。
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.False(f.Service().HasPendingRecovery(install.InstallId));
    }

    [Fact]
    public async Task FailureAfterBackupRestoresFilesAndReceiptAtEveryStage()
    {
        foreach (var phase in new[]
        {
            PortableUpgradePhase.BackingUp, PortableUpgradePhase.Swapping, PortableUpgradePhase.Registering,
        })
        {
            using var f = new Fixture();
            var install = await f.SeedAsync("1.0.0", "Tool.exe", "previous");
            f.Download.Bytes = Zip(("Tool.exe", Pe("replacement")));
            var fired = false;
            var service = f.Service(checkpoint: (current, _) =>
            {
                if (current == phase && !fired) { fired = true; throw new IOException("injected failure"); }
                return Task.CompletedTask;
            });

            var result = await service.UpdateAsync(f.Request("1.1.0"));

            Assert.False(result.Succeeded);
            Assert.True(result.FilesRestored, $"phase={phase} code={result.ErrorCode}");
            Assert.Equal("previous", Text(f.Target, "Tool.exe"));
            Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
            Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
            Assert.False(service.HasPendingRecovery(install.InstallId));
        }
    }

    [Fact]
    public async Task CancellationDuringStagingLeavesOldInstallIntact()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        f.Download.Bytes = Zip(("Tool.exe", Pe("replacement")));
        using var cts = new CancellationTokenSource();
        var service = f.Service(checkpoint: (phase, _) =>
        {
            // 解包阶段取消：此时目标目录与登记都还没被碰过。
            if (phase == PortableUpgradePhase.Extracting) cts.Cancel();
            return Task.CompletedTask;
        });

        var result = await service.UpdateAsync(f.Request("1.1.0"), ct: cts.Token);

        Assert.Equal(PortableUpgradeErrorCodes.Cancelled, result.ErrorCode);
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
        Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-stage-*"));
        Assert.False(service.HasPendingRecovery(install.InstallId));
    }

    [Fact]
    public async Task CancellationAfterBackupStillRestoresThePreviousInstall()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        f.Download.Bytes = Zip(("Tool.exe", Pe("replacement")));
        using var cts = new CancellationTokenSource();
        var service = f.Service(checkpoint: (phase, _) =>
        {
            // 备份已经开始（目标目录已被改名），此时取消必须仍然把旧版本换回来。
            if (phase == PortableUpgradePhase.Swapping) cts.Cancel();
            return Task.CompletedTask;
        });

        var result = await service.UpdateAsync(f.Request("1.1.0"), ct: cts.Token);

        Assert.Equal(PortableUpgradeErrorCodes.Cancelled, result.ErrorCode);
        Assert.True(result.FilesRestored);
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
        Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
        Assert.False(service.HasPendingRecovery(install.InstallId));
    }

    [Fact]
    public async Task CorruptOrMissingExecutableInTheCandidateNeverReplacesTheInstall()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        f.Download.Bytes = Zip(("Tool.exe", new byte[] { 1, 2, 3 }));

        var result = await f.Service().UpdateAsync(f.Request("1.1.0"));

        Assert.False(result.Succeeded);
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
        Assert.Single(f.Launcher.Receipts);
    }

    [Fact]
    public async Task FakeDownloaderIntegrityIsIndependentlyChecked()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        var request = f.Request("1.1.0");

        // 断言里的资产必须同时出现在 Release.Assets 中，否则请求本身就不合法，
        // 根本走不到校验环节——那样测的就不是完整性校验了。
        var checksum = await f.Service().UpdateAsync(f.Rebind(request, request.Asset with { Digest = new string('0', 64) }));
        Assert.Equal(PortableUpgradeErrorCodes.ChecksumMismatch, checksum.ErrorCode);

        var length = await f.Service().UpdateAsync(f.Rebind(request, request.Asset with { Size = f.Download.Bytes.Length + 1 }));
        Assert.Equal(PortableUpgradeErrorCodes.LengthMismatch, length.ErrorCode);

        var invalidDigest = await f.Service().UpdateAsync(f.Rebind(request, request.Asset with { Digest = "sha512:arbitrary" }));
        Assert.Equal(PortableUpgradeErrorCodes.InvalidDigest, invalidDigest.ErrorCode);

        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
        Assert.Single(f.Launcher.Receipts);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("dir/../../escape.exe")]
    [InlineData("C:/absolute.exe")]
    public async Task UnsafeArchiveNamesNeverReplaceTheInstall(string name)
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        f.Download.Bytes = Zip((name, Pe("evil")));

        var result = await f.Service().UpdateAsync(f.Request("1.1.0"));

        Assert.Equal(PortableUpgradeErrorCodes.UnsafeArchive, result.ErrorCode);
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
        Assert.False(File.Exists(Path.Combine(f.Root, "escape.exe")));
        Assert.False(File.Exists(Path.Combine(f.Base, "escape.exe")));
    }

    [Fact]
    public async Task ZipSymlinkEntryIsRejected()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("link.exe");
            entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var output = entry.Open();
            output.Write(Pe("link"));
        }
        f.Download.Bytes = memory.ToArray();

        Assert.Equal(PortableUpgradeErrorCodes.UnsafeArchive, (await f.Service().UpdateAsync(f.Request("1.1.0"))).ErrorCode);
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
    }

    [Theory]
    [InlineData("Tool.7z")]
    [InlineData("Tool.exe")]
    [InlineData("Tool.msi")]
    [InlineData("Tool-linux.zip")]
    [InlineData("Tool-symbols.zip")]
    public async Task CandidateAssetsThatAreNotPortableAreRefusedBeforeDownload(string name)
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "previous");

        var result = await f.Service().UpdateAsync(f.Request("1.1.0", shear: name));

        Assert.False(result.Succeeded);
        Assert.Null(f.Download.Last);
        Assert.Equal("previous", Text(f.Target, "Tool.exe"));
    }

    [Fact]
    public async Task PortableSingleFileExeCandidateIsSupportedWithoutExecution()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "previous");
        f.Download.Bytes = Pe("standalone");

        var result = await f.Service().UpdateAsync(f.Request("1.1.0", shear: "Tool-portable.exe"));

        Assert.True(result.Succeeded, result.ErrorCode);
        var receipt = Assert.Single(f.Launcher.Receipts);
        Assert.Equal(install.InstallId, receipt.InstallId);
        Assert.Equal(Pe("standalone"), await File.ReadAllBytesAsync(receipt.ExePath!));
    }

    [Fact]
    public async Task UpdateKeepsTheExistingEntryPointInsteadOfPickingAnotherProgram()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "bin/Client.exe", "previous");
        // 新版多了一个更"像产品名"的入口，但旧登记的入口仍然存在，应继续使用它。
        f.Download.Bytes = Zip(("bin/Client.exe", Pe("client2")), ("Tool.exe", Pe("other")));

        var result = await f.Service().UpdateAsync(f.Request("1.1.0"));

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.EndsWith(Path.Combine("bin", "Client.exe"), result.ExecutablePath);
        Assert.Equal(Pe("client2"), await File.ReadAllBytesAsync(result.ExecutablePath!));
    }

    [Fact]
    public async Task ConfiguredExecutableRelativePathWinsOverTheOldEntryPoint()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Old.exe", "previous");
        f.Download.Bytes = Zip(("Old.exe", Pe("old2")), ("nested/Client.exe", Pe("client2")));

        var result = await f.Service().UpdateAsync(f.Request("1.1.0") with { ExecutableRelativePath = "nested/Client.exe" });

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.EndsWith(Path.Combine("nested", "Client.exe"), result.ExecutablePath);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("bin/Client.dll")]
    public async Task InvalidConfiguredExecutablePathIsRefusedBeforeDownload(string path)
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "previous");

        var result = await f.Service().UpdateAsync(f.Request("1.1.0") with { ExecutableRelativePath = path });

        Assert.Equal(PortableUpgradeErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task RollbackReinstallsTheOlderCatalogReleaseAndRemovesTheNewer()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));

        // 新机制不读本地备份：软件源里必须有比当前(1.1.0)更旧的发行版，回退会重新下载它。
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-20)));
        f.SetDownloadMarker("v1-from-catalog");

        var rollback = await f.Service().RollbackAsync(install.InstallId);

        Assert.True(rollback.Succeeded, rollback.ErrorCode);
        Assert.Equal(install.InstallId, rollback.InstallId);
        Assert.Equal("v1-from-catalog", Text(f.Target, "Tool.exe"));
        // 登记回到旧版本，且仍然是同一条 InstallId。
        var receipt = Assert.Single(f.Launcher.Receipts);
        Assert.Equal(install.InstallId, receipt.InstallId);
        Assert.Equal("1.0.0", receipt.Version.Raw);
        Assert.Equal("1.0.0", receipt.DisplayVersion);
        Assert.Equal(install.ExePath, receipt.ExePath);
        // 新机制不保留本地目录：回退后不得留下备份/被替换目录或回退记录。
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-replaced-*"));
        Assert.Empty(RollbackRecords(f));
        // 现在已是 1.0.0，软件源里没有更旧的版本，再次回退必须明确拒绝。
        var again = await f.Service().RollbackAsync(install.InstallId);
        Assert.False(again.Succeeded);
        Assert.Equal(PortableUpgradeErrorCodes.RollbackNotAvailable, again.ErrorCode);
    }

    [Fact]
    public async Task RollbackWithoutAnyOlderReleaseIsRefused()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        // 软件源里只有当版本本身：没有更旧的可回退版本。
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));

        var result = await f.Service().RollbackAsync(install.InstallId);

        Assert.Equal(PortableUpgradeErrorCodes.RollbackNotAvailable, result.ErrorCode);
        Assert.Equal("v1", Text(f.Target, "Tool.exe"));
        Assert.Single(f.Launcher.Receipts);

        // 目录读不出来时必须与“确实没有更旧版本”区分开。
        f.Catalog.Error = new InvalidOperationException("catalog offline");
        Assert.Equal(PortableUpgradeErrorCodes.CatalogUnavailable, (await f.Service().RollbackAsync(install.InstallId)).ErrorCode);

        // 未配置目录服务同样是“读不出来”，绝不能说成“没有可回退版本”。
        Assert.Equal(PortableUpgradeErrorCodes.CatalogUnavailable,
            (await f.Service(useCatalog: false).RollbackAsync(install.InstallId)).ErrorCode);
    }

    [Fact]
    public async Task RollbackRefusesNonPortableReceipt()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1", installer: InstallerKind.Msi, strategy: Strategy.Custom);

        Assert.Equal(PortableUpgradeErrorCodes.RollbackNotPortable, (await f.Service().RollbackAsync(install.InstallId)).ErrorCode);
    }

    [Fact]
    public async Task RollbackRefusesUnknownInstallId()
    {
        using var f = new Fixture();
        Assert.Equal(PortableUpgradeErrorCodes.RollbackInstallationNotFound, (await f.Service().RollbackAsync("nope")).ErrorCode);
    }

    [Fact]
    public async Task RepeatedUpdatesLeaveNoLocalBackupAndRollbackUsesTheLatestOlderRelease()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        f.Download.Bytes = Zip(("Tool.exe", Pe("v3")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.2.0"))).Succeeded);

        // 新机制每成功更新一次就删除被替换的旧目录：磁盘上不残留备份，也没有回退记录。
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(RollbackRecords(f));

        // 回退从软件源取“比当前 1.2.0 更旧的最近一版”，即 1.1.0（而不是 1.0.0）。
        f.ConfigureRollbackReleases(
            ("1.1.0", DateTimeOffset.UtcNow.AddDays(-10)),
            ("1.0.0", DateTimeOffset.UtcNow.AddDays(-20)));
        f.SetDownloadMarker("v2-from-catalog");

        Assert.True((await f.Service().RollbackAsync(install.InstallId)).Succeeded);
        Assert.Equal("v2-from-catalog", Text(f.Target, "Tool.exe"));
        Assert.Equal("1.1.0", Assert.Single(f.Launcher.Receipts).Version.Raw);
    }

    [Fact]
    public async Task JournalRecoveryRestoresPreviousVersionAfterSimulatedCrash()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        // 崩溃注入：进程在"新目录已就位、登记还没写"的那一刻被强杀，
        // 更新流程不做任何还原，现场留给启动恢复。
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);

        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));

        // 现场：journal 还在，目标目录是"半新"的，登记仍是旧版本。
        Assert.True(crashed.HasPendingRecovery(install.InstallId));
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));
        Assert.Equal("1.0.0", Assert.Single(f.Launcher.Receipts).Version.Raw);

        var recovered = await f.Service().RecoverAsync();

        Assert.Equal(1, recovered.Examined);
        Assert.Equal(1, recovered.Restored);
        Assert.Equal(0, recovered.Failed);
        // 文件与登记一起回到崩溃前的状态。
        Assert.Equal("v1", Text(f.Target, "Tool.exe"));
        Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
        Assert.False(crashed.HasPendingRecovery(install.InstallId));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
    }

    [Fact]
    public async Task JournalRecoveryRestoresAfterCrashDuringSwapping()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Swapping
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);

        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));
        Assert.True(crashed.HasPendingRecovery(install.InstallId));

        // 这一阶段旧目录已经改名成备份、目标位置还是空的：恢复必须把它换回来。
        var recovered = await f.Service().RecoverAsync();

        Assert.Equal(1, recovered.Restored);
        Assert.Equal("v1", Text(f.Target, "Tool.exe"));
        Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
    }

    [Fact]
    public async Task RecoveryReportsUnrelatedHealthyStateAsEmpty()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        var result = await f.Service().RecoverAsync();
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RecoveryNeverRestoresAnAlreadyCommittedUpdate()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var service = f.Service();
        Assert.True((await service.UpdateAsync(f.Request("1.1.0"))).Succeeded);

        // 人为制造"已提交的 journal 没能删掉"：下次启动绝不能据此把新版本还原回去。
        var journal = service.GetJournalPath(install.InstallId);
        Assert.False(File.Exists(journal));

        var recovered = await service.RecoverAsync();
        Assert.Equal(0, recovered.Examined);
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));
    }

    [Fact]
    public async Task CorruptJournalIsReportedAndLeavesTheInstallAlone()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        var service = f.Service();
        var journal = service.GetJournalPath(install.InstallId);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllTextAsync(journal, "{ this is not valid json");

        var recovered = await service.RecoverAsync();

        Assert.Equal(1, recovered.Examined);
        Assert.Equal(1, recovered.Failed);
        Assert.Contains(PortableUpgradeErrorCodes.RecoveryJournalInvalid, recovered.ErrorCodes);
        // 损坏的 journal 指向不了任何可信路径：一个文件都不动，也不删这个文件。
        Assert.Equal("v1", Text(f.Target, "Tool.exe"));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task RollbackWithPendingJournalRecoversFirstThenInstallsTheOlderRelease()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));
        Assert.True(crashed.HasPendingRecovery(install.InstallId));

        // 回退会先恢复上次未完成的更新（回到 1.0.0）；随后再从软件源取更旧的一版（0.9.0）重装。
        f.ConfigureRollbackReleases(("0.9.0", DateTimeOffset.UtcNow.AddDays(-30)));
        f.SetDownloadMarker("v0.9");

        var rollback = await f.Service().RollbackAsync(install.InstallId);

        Assert.True(rollback.Succeeded, rollback.ErrorCode);
        // 上次更新的 journal 已被恢复消费掉，不会再挂在磁盘上。
        Assert.False(crashed.HasPendingRecovery(install.InstallId));
        Assert.Equal("v0.9", Text(f.Target, "Tool.exe"));
        Assert.Equal("0.9.0", Assert.Single(f.Launcher.Receipts).Version.Raw);
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-replaced-*"));
    }

    [Fact]
    public async Task FailedReceiptRestoreKeepsTheJournalAndARetryFinishesTheJob()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));
        var backupsBefore = Directory.GetDirectories(f.Root, ".githubstore-backup-*");
        Assert.Single(backupsBefore);

        // 恢复时文件能换回旧版本，但登记写入失败（索引只读）：必须如实报告失败，
        // <em>不</em>删除 journal——否则这条安装的登记就会永远停留在“新版本”，再无自愈机会。
        f.Launcher.OnRegister = _ => throw new IOException("manifest is read-only");
        var recovered = await f.Service().RecoverAsync();

        Assert.Equal(1, recovered.Failed);
        Assert.Contains(PortableUpgradeErrorCodes.ReceiptRestoreFailed, recovered.ErrorCodes);
        Assert.True(crashed.HasPendingRecovery(install.InstallId));
        // 崩溃点在“写登记之前”，所以登记本来就没被改成新版本；换回文件后磁盘上是
        // “旧文件 + 旧登记”，本身已自洽子。但登记写入这一步确实报了错，本程序无法证明
        // 它到底有没有落盘，因此必须保守地保留 journal，让下一次启动重试后再确认。
        Assert.Equal("v1", Text(f.Target, "Tool.exe"));
        Assert.Equal("1.0.0", Assert.Single(f.Launcher.Receipts).Version.Raw);

        // 索引恢复可写后重试：这一次必须成功（重试本身没有副作用），并且收敛到“无 journal”。
        f.Launcher.OnRegister = null;
        var retry = await f.Service().RecoverAsync();

        Assert.Equal(1, retry.Examined);
        Assert.Equal(0, retry.Failed);
        Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
        Assert.False(crashed.HasPendingRecovery(install.InstallId));
    }

    [Fact]
    public async Task UnmanagedSiblingDirectoriesAreNeverDeleted()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        // 安装根目录下的邻居：不属于本次更新，任何路径下都不许被删。
        var neighbour = Directory.CreateDirectory(Path.Combine(f.Root, "Neighbour")).FullName;
        await File.WriteAllTextAsync(Path.Combine(neighbour, "keep.txt"), "keep");
        var unrelated = Directory.CreateDirectory(Path.Combine(f.Root, ".githubstore-stage-not-ours")).FullName;
        await File.WriteAllTextAsync(Path.Combine(unrelated, "keep.txt"), "keep");
        var outside = Directory.CreateDirectory(Path.Combine(f.Base, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "keep");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));

        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        // 回退从软件源重装更旧版本，而不是动本地邻居目录。
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));
        f.SetDownloadMarker("v1-from-catalog");
        Assert.True((await f.Service().RollbackAsync(f.InstallId)).Succeeded);

        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(neighbour, "keep.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task TargetOutsideTheInstallRootIsRefused()
    {
        using var f = new Fixture();
        // 登记指向安装根目录之外（且是根目录的兄弟）：这类登记绝不能成为被替换的对象。
        var outside = Directory.CreateDirectory(Path.Combine(f.Base, "elsewhere")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(outside, "Tool.exe"), Pe("outside"));
        f.Launcher.Receipts.Add(f.Receipt("1.0.0", outside, Path.Combine(outside, "Tool.exe")));

        var result = await f.Service().UpdateAsync(f.Request("1.1.0"));

        Assert.Equal(PortableUpgradeErrorCodes.UnsafePath, result.ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Equal(Pe("outside"), await File.ReadAllBytesAsync(Path.Combine(outside, "Tool.exe")));
    }

    [Fact]
    public async Task RootLevelTargetOrReservedDirectoryNameIsRefused()
    {
        using var f = new Fixture();
        var reserved = Directory.CreateDirectory(Path.Combine(f.Root, ".githubstore-backup-forged")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(reserved, "Tool.exe"), Pe("forged"));
        f.Launcher.Receipts.Add(f.Receipt("1.0.0", reserved, Path.Combine(reserved, "Tool.exe")));

        Assert.Equal(PortableUpgradeErrorCodes.UnsafePath, (await f.Service().UpdateAsync(f.Request("1.1.0"))).ErrorCode);
        Assert.Equal(Pe("forged"), await File.ReadAllBytesAsync(Path.Combine(reserved, "Tool.exe")));
    }

    [Fact]
    public async Task MissingTargetDirectoryIsReportedInsteadOfGuessing()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        File.Delete(Path.Combine(f.Target, "Tool.exe"));

        Assert.Equal(PortableUpgradeErrorCodes.TargetMissing, (await f.Service().UpdateAsync(f.Request("1.1.0"))).ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Equal(install, Assert.Single(f.Launcher.Receipts));
    }

    [Fact]
    public async Task EmptyInstallRootRefusesEveryOperation()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        // 提供更旧发行版，确保回退真正走到需要安装根目录的替换步骤，而不是先因“无候选”早退。
        f.ConfigureRollbackReleases(("0.9.0", DateTimeOffset.UtcNow.AddDays(-30)));
        // 空字符串表示该启动器不管理安装根目录：无法证明任何目录归本程序所有。
        var service = f.Service(root: () => string.Empty);

        Assert.Equal(PortableUpgradeErrorCodes.UnsafePath, (await service.UpdateAsync(f.Request("1.1.0"))).ErrorCode);
        Assert.Equal(PortableUpgradeErrorCodes.UnsafePath, (await service.RollbackAsync(f.InstallId)).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Theory]
    [InlineData(@"\\server\share\apps")]
    [InlineData(@"C:\apps\..\escape")]
    public async Task UnsafeInstallRootsAreRefusedBeforeDownload(string root)
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        Assert.Equal(PortableUpgradeErrorCodes.UnsafePath, (await f.Service(root: () => root).UpdateAsync(f.Request("1.1.0"))).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task ConcurrentUpdateReturnsBusyWithoutTouchingAnything()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var first = f.Service(checkpoint: async (phase, _) =>
        {
            if (phase != PortableUpgradePhase.Extracting) return;
            entered.TrySetResult();
            await release.Task;
        }).UpdateAsync(f.Request("1.1.0"));
        await entered.Task;
        try
        {
            // 互斥按数据目录分键：同一数据目录的第二个服务实例必须立刻返回忙。
            // 这里只断言错误码，不断言 Download.Last——它记录的是仍在进行的那次成功更新。
            var other = f.Service();
            Assert.Equal(PortableUpgradeErrorCodes.Busy, (await other.UpdateAsync(f.Request("1.2.0"))).ErrorCode);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first).Succeeded);
    }

    [Fact]
    public async Task SeparateDataDirectoriesDoNotShareTheUpgradeGate()
    {
        using var first = new Fixture();
        await first.SeedAsync("1.0.0", "Tool.exe", "v1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var running = first.Service(checkpoint: async (phase, _) =>
        {
            if (phase != PortableUpgradePhase.Extracting) return;
            entered.TrySetResult();
            await release.Task;
        }).UpdateAsync(first.Request("1.1.0"));
        await entered.Task;
        try
        {
            // 另一个数据目录是另一套受管状态：它自己的更新不应被别人的门挡住。
            using var other = new Fixture();
            await other.SeedAsync("1.0.0", "Tool.exe", "v1");
            other.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
            var result = await other.Service().UpdateAsync(other.Request("1.1.0"));
            Assert.True(result.Succeeded, result.ErrorCode);
            Assert.Equal("v2", Text(other.Target, "Tool.exe"));
        }
        finally { release.TrySetResult(); }
        Assert.True((await running).Succeeded);
    }

    [Fact]
    public async Task CancelledTokenBeforeStartReturnsCancelledWithoutSideEffects()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(PortableUpgradeErrorCodes.Cancelled, (await f.Service().UpdateAsync(f.Request("1.1.0"), ct: cts.Token)).ErrorCode);
        Assert.Null(f.Download.Last);
    }

    [Fact]
    public async Task JournalLivesUnderTheAppDataDirectoryAndIsNamedByInstallIdHash()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        var path = f.Service().GetJournalPath(install.InstallId);

        Assert.StartsWith(Path.Combine(f.Data, "upgrade-journal") + Path.DirectorySeparatorChar, path);
        Assert.Equal(".json", Path.GetExtension(path));
        // 安装 ID 本身不能出现在路径里：否则它可以借 ID 越界。
        Assert.DoesNotContain(install.InstallId, Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Equal(64, Path.GetFileNameWithoutExtension(path).Length);
    }

    [Fact]
    public void JournalPathRejectsUnusableInstallId()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => f.Service().GetJournalPath("  "));
        Assert.Throws<ArgumentException>(() => f.Service().GetJournalPath(new string('a', 257)));
    }

    [Fact]
    public async Task RealLauncherAcceptsBothUpdatedAndRestoredReceipts()
    {
        using var f = new Fixture();
        // 用真实的 ManagedAppLauncher 走一遍：更新与回滚写出的登记都必须被它接受。
        var launcher = new GitHubStore.App.Services.Launcher.ManagedAppLauncher(f.Data, f.Root, (_, _) => true);
        var installId = Guid.NewGuid().ToString("N");
        var target = Directory.CreateDirectory(Path.Combine(f.Root, "Tool")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(target, "Tool.exe"), Pe("v1"));
        await launcher.RegisterCompletedInstallAsync(new InstallRecord
        {
            InstallId = installId, AppId = f.App.Id,
            Source = new SourceBinding
            {
                SourceId = f.App.Id.SourceId, PackageId = f.App.Id.PackageId, SourceRevision = 1,
                SourceEntryUrl = new Uri("https://github.com/owner/tool"), InstallerId = "asset-1",
                ArtifactUrl = new Uri("https://example.com/Tool.zip"),
            },
            ReleaseKey = "release-1", Origin = Origin.Store, Channel = Channel.Http,
            Strategy = Strategy.Green, Installer = InstallerKind.Portable,
            Version = new Version { Raw = "1.0.0", Scheme = VersionScheme.Opaque }, DisplayVersion = "1.0.0",
            TargetDir = target, ExePath = Path.Combine(target, "Tool.exe"), State = InstallState.Done,
            InstalledAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, "Tool");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var service = new PortableUpgradeService(f.Download, launcher, f.Preferences, f.Data,
            installRootProvider: () => f.Root, catalog: f.Catalog);

        var update = await service.UpdateAsync(f.Request("1.1.0", installId));
        Assert.True(update.Succeeded, update.ErrorCode);
        Assert.Equal("1.1.0", Assert.Single(await launcher.ListAsync()).DisplayVersion);

        // 回退从软件源重新获取上一版本：为这条真实启动器提供更旧的发行版与安装包。
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));
        f.SetDownloadMarker("v1-restored");
        var rollback = await service.RollbackAsync(installId);
        Assert.True(rollback.Succeeded, rollback.ErrorCode);
        var restored = await launcher.GetAsync(installId);
        Assert.Equal("1.0.0", restored!.DisplayVersion);
        Assert.True(File.Exists(restored.ExePath));
    }

    /// <summary>
    /// 读回 <see cref="Pe"/> 写入的标记文本。标记刻意放在 PE 头部之后（偏移 128），
    /// 因为可执行文件校验只读文件头，标记不能干扰它。
    /// </summary>
    // ── task-16：事务保证、路径权威与升级入口 ──────────────────────────────────

    [Fact]
    public async Task UpdateIsRefusedWhileAnIncompleteJournalStillExists()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        // 崩溃后现场：journal 仍在，恢复还没做。
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));
        Assert.True(crashed.HasPendingRecovery(install.InstallId));

        // 此时再开新更新会覆盖上一次的恢复依据，必须被拒绝。
        var second = await f.Service().UpdateAsync(f.Request("1.2.0"));
        Assert.Equal(PortableUpgradeErrorCodes.PendingJournal, second.ErrorCode);
        // 原 journal 没被覆盖，恢复依据完好。
        Assert.True(crashed.HasPendingRecovery(install.InstallId));
    }

    /// <summary>
    /// 已提交（Completed）的残留 journal 只是没删掉的元数据：文件与登记都已是新版本。
    /// 它不能阻止用户继续更新，否则一次“删除元数据失败”就会把用户永久卡在旧版本上。
    /// </summary>
    [Fact]
    public async Task AlreadyCommittedLeftoverJournalDoesNotBlockTheNextUpdate()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        Assert.False(f.Service().HasPendingRecovery(install.InstallId));

        // 重建一条“已提交但没删掉”的残留 journal：用真实更新产生的 journal 再置上 completed，
        // 这样字段、命名与嵌套结构都来自服务本身，不会因为手写 JSON 不匹配而误判。
        f.Download.Bytes = Zip(("Tool.exe", Pe("v3")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(
            () => crashed.UpdateAsync(f.Request("1.2.0", shear: "Tool-1.2.0.zip")));
        await TamperAsync(crashed.GetJournalPath(install.InstallId), root => root["completed"] = true);
        Assert.True(crashed.HasPendingRecovery(install.InstallId));

        // 下一次更新必须能正常进行，而不是被这条残留挡住。
        f.Download.Bytes = Zip(("Tool.exe", Pe("v4")));
        var result = await f.Service().UpdateAsync(f.Request("1.3.0", shear: "Tool-1.3.0.zip"));
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal("v4", Text(f.Target, "Tool.exe"));
    }

    [Fact]
    public async Task JournalPointingAtANeighbouringDirectoryIsRefused()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));

        // 邻居：一个与本次安装无关的受管目录。被篡改的 journal 会把它当成 target。
        var neighbour = Directory.CreateDirectory(Path.Combine(f.Root, "Neighbour")).FullName;
        await File.WriteAllTextAsync(Path.Combine(neighbour, "keep.txt"), "keep");
        var journal = crashed.GetJournalPath(install.InstallId);
        // 直接改写 JSON 字段（而不是替换原始文本），确保生成的是一份完全合法的 journal——
        // 这样被拒的原因只会是“身份不符”，而不是“文件损坏”。
        await TamperAsync(journal, root => root["targetDir"] = neighbour);

        var recovered = await crashed.RecoverAsync();

        Assert.Equal(1, recovered.Failed);
        // 邻居目录既没被移动也没被删除。
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(neighbour, "keep.txt")));
    }

    [Fact]
    public async Task JournalWhosePreviousReceiptTargetsAnotherDirectoryIsRefused()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));

        // 把 journal 里旧登记的 TargetDir 换成另一个目录：两者不一致说明这条记录是被拼凑出来的。
        var journal = crashed.GetJournalPath(install.InstallId);
        await TamperAsync(journal, root =>
            root["previousReceipt"]!["targetDir"] = Path.Combine(f.Root, "Elsewhere"));

        Assert.Equal(1, (await crashed.RecoverAsync()).Failed);
        // 目标目录仍是我们自己的那份，没有被搬动。
        Assert.True(Directory.Exists(f.Target));
    }

    /// <summary>
    /// 把 journal 的 backupDir 改成<em>另一条安装</em>的备份目录。它仍然是根目录的直接子目录、
    /// 仍然带备份前缀，因此只校验前缀的实现会去搬动别人的备份。必须被拒绝。
    /// </summary>
    [Fact]
    public async Task JournalPointingAtAnotherInstallsBackupIsRefused()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));

        // 另一条安装的备份目录：命名规范完全正确，只是代际标记属于别的安装。
        var foreignBackup = Directory.CreateDirectory(
            Path.Combine(f.Root, OwnedNameFor(".githubstore-backup-", Guid.NewGuid().ToString("N"), "b"))).FullName;
        await File.WriteAllTextAsync(Path.Combine(foreignBackup, "keep.txt"), "keep");

        await TamperAsync(crashed.GetJournalPath(f.InstallId), root => root["backupDir"] = foreignBackup);

        Assert.Equal(1, (await crashed.RecoverAsync()).Failed);
        // 别人的备份一个文件都没动。
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(foreignBackup, "keep.txt")));
    }

    /// <summary>
    /// 把 journal 的 stageDir 改成另一条安装的暂存目录。暂存目录会被“清理”，
    /// 因此只按前缀识别的实现会删掉别人的目录。必须被拒绝。
    /// </summary>
    [Fact]
    public async Task JournalPointingAtAnotherInstallsStageDirectoryIsRefused()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));

        var foreignStage = Directory.CreateDirectory(
            Path.Combine(f.Root, OwnedNameFor(".githubstore-stage-", Guid.NewGuid().ToString("N"), "a"))).FullName;
        await File.WriteAllTextAsync(Path.Combine(foreignStage, "keep.txt"), "keep");

        await TamperAsync(crashed.GetJournalPath(f.InstallId), root => root["stageDir"] = foreignStage);

        await crashed.RecoverAsync();
        // 别人的暂存目录必须原样保留。
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(foreignStage, "keep.txt")));
    }

    /// <summary>
    /// 崩溃在“当前版本已挪到一边、登记还没写回”的那一刻。journal 里必须已有 replacedDir，
    /// 重启恢复才能收敛到旧版本并清掉那一份，而不是留下无人认领的残留。
    /// </summary>
    [Fact]
    public async Task CrashDuringRollbackAfterSwapRestoresThePreRollbackVersionOnRestart()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));

        // 回退=重新安装更旧版本，因此必须先有可回退的旧发行版。
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));
        f.SetDownloadMarker("v1-from-catalog");

        var service = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => service.RollbackAsync(install.InstallId));

        // 崩溃点在“旧版本已换入、登记还没写回”：现场是“目标=旧版本、备份=新版本、journal 仍在”。
        Assert.True(service.HasPendingRecovery(install.InstallId));
        Assert.Equal("v1-from-catalog", Text(f.Target, "Tool.exe"));
        var backups = Directory.GetDirectories(f.Root, ".githubstore-backup-*");
        Assert.Single(backups);
        Assert.Equal("v2", Text(backups[0], "Tool.exe"));
        var journalText = await File.ReadAllTextAsync(service.GetJournalPath(install.InstallId));
        Assert.Contains("backupDir", journalText, StringComparison.OrdinalIgnoreCase);

        // 重启恢复：必须收敛到“回退前的当前版本 + 原登记 + 无 journal + 无残留”。
        var recovered = await f.Service().RecoverAsync();
        Assert.Equal(0, recovered.Failed);
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));
        Assert.Equal("1.1.0", Assert.Single(f.Launcher.Receipts).Version.Raw);
        Assert.False(f.Service().HasPendingRecovery(install.InstallId));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-replaced-*"));
    }

    /// <summary>
    /// 旧版（schema 1）journal：它没有把暂存/备份目录与安装绑定，因此无法证明归属。
    /// 本服务必须 fail closed——保留 journal 与备份原样、如实报告，绝不自动处理旧数据。
    /// </summary>
    [Fact]
    public async Task LegacyJournalIsReportedButNotActedOn()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        var crashed = f.Service(checkpoint: (phase, _) => phase == PortableUpgradePhase.Registering
            ? throw new PortableUpgradeSimulatedCrashException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<PortableUpgradeSimulatedCrashException>(() => crashed.UpdateAsync(f.Request("1.1.0")));

        // 把 schema 降级成 1，模拟旧版本留下的 journal。
        await TamperAsync(crashed.GetJournalPath(install.InstallId), root => root["schemaVersion"] = 1);
        var backupsBefore = Directory.GetDirectories(f.Root, ".githubstore-backup-*");
        Assert.Single(backupsBefore);

        var recovered = await crashed.RecoverAsync();

        Assert.Equal(1, recovered.Failed);
        Assert.Contains(PortableUpgradeErrorCodes.RecoveryLegacyJournal, recovered.ErrorCodes);
        // 一个文件都没动：journal 与备份原样保留，交给人工核对。
        Assert.True(crashed.HasPendingRecovery(install.InstallId));
        Assert.Single(Directory.GetDirectories(f.Root, ".githubstore-backup-*"));
    }

    /// <summary>按本服务的命名规范拼一个自建目录名：前缀 + 安装 ID 哈希前 16 位 + "-" + 32 位后缀。</summary>
    private static string OwnedNameFor(string prefix, string installId, string marker)
    {
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(installId))).ToLowerInvariant();
        return prefix + key[..16] + "-" + string.Concat(Enumerable.Repeat(marker, 32));
    }

    [Fact]
    public async Task UpdateRefusesDeclaredPreservePathsInsteadOfDiscardingUserConfig()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));

        var result = await f.Service().UpdateAsync(f.Request("1.1.0") with { PreservePaths = new[] { "config/settings.json" } });

        // 整目录替换必然覆盖这些文件，因此明确拒绝，而不是默默丢掉用户配置。
        Assert.Equal(PortableUpgradeErrorCodes.PreservePathsUnsupported, result.ErrorCode);
        Assert.Null(f.Download.Last);
        Assert.Equal("v1", Text(f.Target, "Tool.exe"));
        Assert.Single(f.Launcher.Receipts);
    }

    [Fact]
    public async Task FailedRollbackKeepsJournalAndRestartRecoversThePreRollbackVersion()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));
        f.SetDownloadMarker("v1-from-catalog");

        // 回退过程中登记写不进去：文件可能已换回旧版但登记没写回，必须保留 journal 作为恢复依据。
        f.Launcher.OnRegister = _ => throw new IOException("manifest is read-only");
        var rollback = await f.Service().RollbackAsync(install.InstallId);

        Assert.False(rollback.Succeeded);
        Assert.Equal(PortableUpgradeErrorCodes.RollbackFailed, rollback.ErrorCode);
        // 关键：journal 必须留在磁盘上，作为下次启动的恢复依据。
        Assert.True(f.Service().HasPendingRecovery(install.InstallId));

        // 模拟重启：登记恢复可写后，启动恢复必须把这次失败的回退收敛回“回退前的当前版本”。
        f.Launcher.OnRegister = null;
        var recovered = await f.Service().RecoverAsync();

        Assert.Equal(0, recovered.Failed);
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));
        Assert.Equal("1.1.0", Assert.Single(f.Launcher.Receipts).Version.Raw);
        Assert.False(f.Service().HasPendingRecovery(install.InstallId));
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-replaced-*"));
    }

    [Fact]
    public async Task FailedRollbackKeepsTheDisplacedOlderCopyUntilTheReceiptIsCommitted()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));
        f.SetDownloadMarker("v1-from-catalog");

        f.Launcher.OnRegister = _ => throw new IOException("manifest is read-only");
        await f.Service().RollbackAsync(install.InstallId);

        // 登记写回失败时，被换下来的那一份（回退时下载的旧版本）不能删：删了就两个版本都没了。
        var replaced = Directory.GetDirectories(f.Root, ".githubstore-replaced-*");
        Assert.Single(replaced);
        Assert.Equal("v1-from-catalog", Text(replaced[0], "Tool.exe"));

        // 重试恢复：登记写回成功后，被换下来的目录才允许清理，并收敛回回退前的当前版本。
        f.Launcher.OnRegister = null;
        await f.Service().RecoverAsync();
        Assert.Empty(Directory.GetDirectories(f.Root, ".githubstore-replaced-*"));
        Assert.Equal("v2", Text(f.Target, "Tool.exe"));
    }

    [Fact]
    public async Task FailedRollbackJournalRecordsTheDisplacedDirectoryBeforeMovingAnything()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        f.Download.Bytes = Zip(("Tool.exe", Pe("v2")));
        Assert.True((await f.Service().UpdateAsync(f.Request("1.1.0"))).Succeeded);
        f.ConfigureRollbackReleases(("1.0.0", DateTimeOffset.UtcNow.AddDays(-10)));
        f.SetDownloadMarker("v1-from-catalog");
        f.Launcher.OnRegister = _ => throw new IOException("manifest is read-only");
        await f.Service().RollbackAsync(install.InstallId);

        // journal 必须如实记录“把当时被换下来的目录挪到了哪里”，否则下次恢复无从清理。
        var text = await File.ReadAllTextAsync(f.Service().GetJournalPath(install.InstallId));
        Assert.Contains(".githubstore-replaced-", text, StringComparison.Ordinal);
        Assert.Contains("replacedDir", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindCandidateAsyncUsesRealCatalogDataAndNeverGuessesAVersion()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        var asset = new Asset
        {
            InstallerId = "asset-2", ReleaseKey = "release-1", Name = "Tool-1.1.0-windows-portable.zip",
            DownloadUrl = "https://example.com/Tool-1.1.0-windows-portable.zip",
        };
        f.Catalog.Detail = new AppDetail
        {
            App = f.App,
            Releases = new[]
            {
                new Release { AppId = f.App.Id, Key = "release-1", TagName = "1.1.0", Assets = new[] { asset } },
                // 预发布与草稿都不能变成候选。
                new Release { AppId = f.App.Id, Key = "release-2", TagName = "2.0.0", Prerelease = true, Assets = Array.Empty<Asset>() },
                new Release { AppId = f.App.Id, Key = "release-3", TagName = "3.0.0", Draft = true, Assets = Array.Empty<Asset>() },
            },
        };

        var result = await f.Service().FindCandidateAsync(install.InstallId);

        Assert.NotNull(result.Candidate);
        Assert.Equal("1.1.0", result.Candidate!.CandidateVersion);
        Assert.Equal(asset.Name, result.Candidate.Asset.Name);
        Assert.Equal(VersionRelation.Newer, result.Candidate.Relation);
        Assert.Equal("1.0.0", result.Candidate.InstalledVersion);
    }

    [Fact]
    public async Task FindCandidateAsyncReportsNoCandidateSeparatelyFromAnUnavailableCatalog()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        // 目录里只有更旧的版本：确实没有候选。
        var asset = new Asset
        {
            InstallerId = "asset-2", ReleaseKey = "release-1", Name = "Tool-0.9.0-windows-portable.zip",
            DownloadUrl = "https://example.com/Tool-0.9.0-windows-portable.zip",
        };
        f.Catalog.Detail = new AppDetail
        {
            App = f.App,
            Releases = new[] { new Release { AppId = f.App.Id, Key = "release-1", TagName = "0.9.0", Assets = new[] { asset } } },
        };
        Assert.Equal(PortableUpgradeErrorCodes.CandidateNotFound, (await f.Service().FindCandidateAsync(install.InstallId)).ErrorCode);

        // 目录读不出来：“没打听到”和“确实没有”必须区分开，后者才代表已经是最新。
        f.Catalog.Error = new InvalidOperationException("catalog offline");
        Assert.Equal(PortableUpgradeErrorCodes.CatalogUnavailable, (await f.Service().FindCandidateAsync(install.InstallId)).ErrorCode);

        // 没有配置目录服务时同样是“不可用”，绝不能说成“没有新版”。
        Assert.Equal(PortableUpgradeErrorCodes.CatalogUnavailable,
            (await f.Service(useCatalog: false).FindCandidateAsync(install.InstallId)).ErrorCode);
    }

    [Fact]
    public async Task FindCandidateAsyncRefusesNonPortableAndUnknownInstalls()
    {
        using var f = new Fixture();
        await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        Assert.Equal(PortableUpgradeErrorCodes.InstallationNotFound,
            (await f.Service().FindCandidateAsync("unknown-id")).ErrorCode);

        using var g = new Fixture();
        var installer = await g.SeedAsync("1.0.0", "Tool.exe", "v1", installer: InstallerKind.Inno, strategy: Strategy.Custom);
        Assert.Equal(PortableUpgradeErrorCodes.NotPortable, (await g.Service().FindCandidateAsync(installer.InstallId)).ErrorCode);
    }

    [Fact]
    public async Task FindCandidateAsyncIgnoresNonPortableAssetsInARelease()
    {
        using var f = new Fixture();
        var install = await f.SeedAsync("1.0.0", "Tool.exe", "v1");
        // 该发行版只有安装器：不能拿它做整目录替换。
        var setup = new Asset
        {
            InstallerId = "asset-2", ReleaseKey = "release-1", Name = "Tool-Setup-1.1.0-x64.exe",
            DownloadUrl = "https://example.com/Tool-Setup-1.1.0-x64.exe",
        };
        f.Catalog.Detail = new AppDetail
        {
            App = f.App,
            Releases = new[] { new Release { AppId = f.App.Id, Key = "release-1", TagName = "1.1.0", Assets = new[] { setup } } },
        };

        Assert.Equal(PortableUpgradeErrorCodes.CandidateNotFound, (await f.Service().FindCandidateAsync(install.InstallId)).ErrorCode);
    }

    /// <summary>用一次合法的 JSON 反序列化/序列化改写 journal 字段，保证改完仍是结构合法的文件。</summary>
    private static async Task TamperAsync(string path, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        mutate(node);
        await File.WriteAllTextAsync(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>列出旧回退机制遗留的保留记录；新机制成功后这些记录必须不存在。</summary>
    private static string[] RollbackRecords(Fixture fixture)
    {
        var directory = Path.Combine(fixture.Data, "upgrade-rollback");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : Array.Empty<string>();
    }

    private static string Text(string directory, string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, name));
        return System.Text.Encoding.UTF8.GetString(bytes, 128, bytes.Length - 128);
    }

    private static byte[] Pe(string marker)
    {
        var bytes = new byte[128 + marker.Length];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 2);
        System.Text.Encoding.UTF8.GetBytes(marker).CopyTo(bytes, 128);
        return bytes;
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(file.Bytes);
            }
        return memory.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "GitHubStore-upgrade-tests-" + Guid.NewGuid().ToString("N"));
        public string Base => _base;
        public string Root => Path.Combine(_base, "apps");
        public string Data => Path.Combine(_base, "data");
        public string Target => Path.Combine(Root, "Tool");
        public string InstallId { get; private set; } = string.Empty;
        public FakeDownload Download { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public PreferencesService Preferences { get; }
        public CatalogApp App { get; } = new()
        {
            Id = new(new(Guid.NewGuid()), new("owner/tool")), SourceRevision = 3,
            DisplayName = "Tool", Owner = "owner", Repo = "tool",
        };

        public Fixture()
        {
            Directory.CreateDirectory(_base);
            Preferences = new(Path.Combine(Data, "preferences.json"));
        }

        public InstallRecord Receipt(string version, string target, string executable,
            InstallerKind installer = InstallerKind.Portable, Strategy strategy = Strategy.Green)
        {
            // 未经过 SeedAsync 的用例也要有一条合法的安装 ID：请求用它来定位登记。
            if (string.IsNullOrEmpty(InstallId)) InstallId = Guid.NewGuid().ToString("N");
            return new InstallRecord
            {
            InstallId = InstallId, AppId = App.Id,
            Source = new SourceBinding
            {
                SourceId = App.Id.SourceId, PackageId = App.Id.PackageId, SourceRevision = 1,
                SourceEntryUrl = new Uri("https://github.com/owner/tool"), InstallerId = "asset-0",
                ArtifactUrl = new Uri("https://example.com/Tool.zip"), ArtifactSha256 = new string('a', 64),
            },
            ReleaseKey = "release-0", Origin = Origin.Store, Channel = Channel.Http,
            Strategy = strategy, Installer = installer,
            Version = new Version { Raw = version, Scheme = VersionScheme.Opaque }, DisplayVersion = version,
            TargetDir = target, ExePath = executable, State = InstallState.Done,
            InstalledAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        /// <summary>写入一条真实的便携安装目录与登记，返回登记原文供断言比对。</summary>
        public async Task<InstallRecord> SeedAsync(string version, string relativeExecutable, string marker,
            InstallerKind installer = InstallerKind.Portable, Strategy strategy = Strategy.Green)
        {
            InstallId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Root);
            var target = Directory.CreateDirectory(Target).FullName;
            var executable = Path.Combine(target, relativeExecutable);
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(executable, Pe(marker));
            var receipt = Receipt(version, target, executable, installer, strategy);
            Launcher.Receipts.Add(receipt);
            return receipt;
        }

        public FakeCatalog Catalog { get; } = new();

        public PortableUpgradeService Service(Func<string>? root = null,
            Func<PortableUpgradePhase, CancellationToken, Task>? checkpoint = null, bool useCatalog = true) =>
            new(Download, Launcher, Preferences, Data, root ?? (() => Root), checkpoint: checkpoint,
                catalog: useCatalog ? Catalog : null);

        /// <summary>替换资产时必须同时更新 Release.Assets，否则请求不合法（构建如实的输入）。</summary>
        public PortableUpgradeRequest Rebind(PortableUpgradeRequest request, Asset asset) =>
            request with { Asset = asset, Release = request.Release with { Assets = new[] { asset } } };

        public PortableUpgradeRequest Request(string tag, string? installId = null, string shear = "Tool.zip")
        {
            var asset = new Asset
            {
                InstallerId = "asset-1", ReleaseKey = "release-1", Name = shear,
                DownloadUrl = "https://example.com/" + shear,
            };
            return new()
            {
                InstallId = installId ?? InstallId, App = App,
                Release = new() { AppId = App.Id, Key = "release-1", TagName = tag, Assets = new[] { asset } },
                Asset = asset,
            };
        }

        /// <summary>
        /// 配置软件源里的发行版。新回退机制不再读取本地备份，而是从目录里挑“比当前更旧的最近一版”
        /// 重新下载安装，因此回退用例必须先在这里提供真实的旧发行版与便携资产。
        /// </summary>
        public void ConfigureRollbackReleases(params (string Tag, DateTimeOffset Published)[] releases)
        {
            Catalog.Detail = new AppDetail
            {
                App = App,
                Releases = releases.Select(release =>
                {
                    var name = "Tool-" + release.Tag + "-windows-portable.zip";
                    var asset = new Asset
                    {
                        InstallerId = "asset-" + release.Tag, ReleaseKey = "release-" + release.Tag, Name = name,
                        DownloadUrl = "https://example.com/" + name,
                    };
                    return new Release
                    {
                        AppId = App.Id, Key = "release-" + release.Tag, TagName = release.Tag,
                        PublishedAt = release.Published, Assets = new[] { asset },
                    };
                }).ToArray(),
            };
        }

        /// <summary>设置下一次下载得到的 zip 内容（标记写在 PE 头之后，供断言读出）。</summary>
        public void SetDownloadMarker(string marker) => Download.Bytes = Zip(("Tool.exe", Pe(marker)));

        public void Dispose()
        {
            try { if (Directory.Exists(_base)) Directory.Delete(_base, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 只提供“按 AppId 取目录详情”的假目录：本服务只会用它找候选，
    /// 不会用它做刷新、搜索或任何写操作。
    /// </summary>
    private sealed class FakeCatalog : ICatalogService
    {
        public AppDetail? Detail { get; set; }
        public Exception? Error { get; set; }

        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Category>>(Array.Empty<Category>());
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) =>
            Task.FromResult(new CatalogPage());
        public Task<IReadOnlyList<CatalogApp>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogApp>>(Array.Empty<CatalogApp>());
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) =>
            Error is not null ? Task.FromException<AppDetail>(Error)
            : Task.FromResult(Detail ?? throw new KeyNotFoundException("No catalog detail configured."));
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDownload : IDownloadService
    {
        public byte[] Bytes { get; set; } = Zip(("Tool.exe", Pe("default")));
        public string? Error { get; set; }
        public int Calls { get; private set; }
        public DownloadRequest? Last { get; private set; }
        public IReadOnlyList<DownloadEngine> AvailableEngines => new[] { DownloadEngine.Http };
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            Last = request;
            Calls++;
            if (Error is not null) return DownloadResult.Failure(Error);
            await File.WriteAllBytesAsync(request.TargetPath, Bytes, ct);
            return DownloadResult.Success(request.Source, request.TargetPath, Bytes.Length, request.Engine);
        }
    }

    /// <summary>
    /// 只保存登记与列表的假实现，语义与 ManagedAppLauncher 的按 InstallId 替换一致：
    /// 同一条 InstallId 不会变成两条记录，因此"更新"不能伪装成"多装了一份"。
    /// </summary>
    private sealed class FakeLauncher : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public List<InstallRecord> Receipts { get; } = new();
        public Action<InstallRecord>? OnRegister { get; set; }
        public string InstallRoot { get; set; } = string.Empty;

        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LauncherApp>>(Receipts.Select(receipt => new LauncherApp
            {
                InstallId = receipt.InstallId, AppId = receipt.AppId, DisplayName = "Tool",
                InstallDirectory = receipt.TargetDir!, ExecutablePath = receipt.ExePath!,
                DisplayVersion = receipt.DisplayVersion, InstalledAt = receipt.InstalledAt,
            }).ToArray());

        public Task<InstallRecord?> GetAsync(string installId, CancellationToken ct = default) =>
            Task.FromResult(Receipts.FirstOrDefault(receipt => receipt.InstallId == installId));

        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnRegister?.Invoke(installation);
            var index = Receipts.FindIndex(receipt => receipt.InstallId == installation.InstallId);
            if (index >= 0) Receipts[index] = installation; else Receipts.Add(installation);
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
