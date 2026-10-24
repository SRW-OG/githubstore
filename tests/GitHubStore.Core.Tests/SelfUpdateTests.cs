using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Updates;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 自更新（GitHubStore 更新自身）的离线回归。
/// <para>
/// 所有 HTTP 响应都由内存 handler 产生，绝不访问真实网络；安装程序一律通过记录委托“启动”，
/// 因此测试<em>永远不会</em>执行任何安装包。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SelfUpdateTests
{
    private const string OfficialTag = "v0.1.3";
    private const string InstallerName = "GitHubStore-Setup-0.1.3-win-x64.exe";
    private const string ChecksumName = InstallerName + ".sha256";
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("synthetic installer payload");

    // ── 版本判定：同 / 更旧 / 更新 / 无法解析 ────────────────────────────

    [Fact]
    public async Task SameVersion_IsReportedAsSameAndNeverOffered()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag));
        var service = Service(directory, client, currentVersion: "0.1.3");

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Same, result.Candidate!.Relation);
        // 同版本不提供更新：没有资产地址，界面只能说“已是最新”。
        Assert.False(result.Candidate.IsUpdateOffered);
        Assert.Null(result.Candidate.InstallerUri);
        Assert.Equal(SelfUpdateErrorCodes.NotOffered, (await service.DownloadAsync(result.Candidate)).ErrorCode);
    }

    [Fact]
    public async Task OlderCandidate_IsReportedAsOlderAndNeverOffered()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: "v0.0.9"));
        var service = Service(directory, client, currentVersion: "0.1.3");

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Older, result.Candidate!.Relation);
        Assert.False(result.Candidate.IsUpdateOffered);
    }

    [Fact]
    public async Task NewerCandidate_IsOfferedWithOfficialInstallerAndChecksum()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Newer, result.Candidate!.Relation);
        Assert.True(result.Candidate.IsUpdateOffered);
        Assert.Equal(InstallerName, result.Candidate.InstallerName);
        Assert.Equal(ChecksumName, result.Candidate.ChecksumName);
        Assert.Equal("https://github.com/SRW-OG/githubstore/releases/tag/v0.1.3", result.Candidate.ReleaseNotesUrl);
    }

    // 注意：Core 的 VersionComparer 能解析 1–4 段数字（0.1 与 0.1.3.4 是“更旧”而不是“无法判定”），
    // 也能解析带预发布/元数据的 SemVer（1.0.0-beta+meta 与 0.1.3 同族）。
    // 这里只放真正不可解析的文本，用来固定“不可解析一律 Different、绝不 Newer”的口径。
    [Theory]
    [InlineData("nightly")]
    [InlineData("release-candidate")]
    [InlineData("v2ray")]     // 产品名不会被切掉首字母当成数字
    [InlineData("latest")]    // 纯文字标签
    public async Task UnparsableVersions_AreDifferentNeverNewer(string installed)
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag));
        var service = Service(directory, client, currentVersion: installed);

        var result = await service.CheckAsync();

        // 无法判定一律 Different；绝不谎称“有更新”，也绝不谎称“已是最新”。
        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Different, result.Candidate!.Relation);
        Assert.False(result.Candidate.IsUpdateOffered);
    }

    [Fact]
    public async Task UnparsableTagWithParseableInstalled_IsDifferentAndNeverNewer()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: "nightly"));
        var service = Service(directory, client, currentVersion: "0.1.3");

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Different, result.Candidate!.Relation);
        Assert.False(result.Candidate.IsUpdateOffered);
    }

    [Fact]
    public async Task UnknownCurrentVersion_IsDifferentAndNeverClaimsUpToDate()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag));
        var service = Service(directory, client, currentVersion: null);

        var result = await service.CheckAsync();

        // 没有版本号就没有依据说“已是最新”，只能说无法比较。
        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Different, result.Candidate!.Relation);
        Assert.False(result.Candidate.IsUpdateOffered);
    }

    [Fact]
    public void CompareTextFixture_MatchesCoreVersionComparer()
    {
        // 自更新不另写比较：这里直接固定它对 Core 现有实现的依赖方向。
        Assert.Equal(VersionRelation.Newer, VersionComparer.RelateText("0.1.2", "v0.1.3"));
        Assert.Equal(VersionRelation.Same, VersionComparer.RelateText("0.1.3", "v0.1.3"));
        Assert.Equal(VersionRelation.Older, VersionComparer.RelateText("0.1.3", "v0.0.9"));
        Assert.Equal(VersionRelation.Different, VersionComparer.RelateText("nightly", "v0.1.3"));
    }

    // ── 草稿与预发布版 ────────────────────────────────────────────────

    [Fact]
    public async Task PrereleaseIsIgnored()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag, prerelease: true));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.NotStable, result.ErrorCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task DraftIsIgnored()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: "v9.9.9", draft: true));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.NotStable, result.ErrorCode);
    }

    // ── 来源固定：非官方主机 / 非 HTTPS ────────────────────────────────

    [Fact]
    public async Task NonOfficialHostResponse_IsRejected()
    {
        using var directory = new TempDirectory();
        // 上游返回了一个来自非官方主机的成功响应（模拟被劫持/被重定向的客户端）。
        var response = Json(Release(tag: OfficialTag));
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/releases/latest");
        using var client = new HttpClient(new StubHttpHandler(_ => response));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.UnsupportedSource, result.ErrorCode);
    }

    [Fact]
    public async Task NonHttpsResponse_IsRejected()
    {
        using var directory = new TempDirectory();
        var response = Json(Release(tag: OfficialTag));
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://api.github.com/repos/SRW-OG/githubstore/releases/latest");
        using var client = new HttpClient(new StubHttpHandler(_ => response));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.UnsupportedSource, result.ErrorCode);
    }

    [Fact]
    public async Task RequestAlwaysTargetsTheHardcodedOfficialEndpoint()
    {
        using var directory = new TempDirectory();
        var captured = new List<Uri>();
        using var client = new HttpClient(new StubHttpHandler(request =>
        {
            captured.Add(request.RequestUri!);
            return Json(Release(tag: OfficialTag));
        }));
        var service = Service(directory, client, currentVersion: "0.1.2");

        await service.CheckAsync();

        // 来源是编译期常量：调用方没有任何参数可以把它指向别处。
        Assert.Equal(new Uri("https://api.github.com/repos/SRW-OG/githubstore/releases/latest"), Assert.Single(captured));
    }

    [Fact]
    public async Task AssetFromAnotherHost_IsRejected()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag,
            installerUrl: "https://evil.example/SRW-OG/githubstore/releases/download/v0.1.3/" + InstallerName));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.AssetRejected, result.ErrorCode);
    }

    [Fact]
    public async Task AssetFromAnotherRepository_IsRejected()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag,
            installerUrl: "https://github.com/attacker/githubstore/releases/download/v0.1.3/" + InstallerName));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.AssetRejected, result.ErrorCode);
    }

    // ── 资产名与架构 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("GitHubStore-Setup-0.1.3-win-arm64.exe")]      // 架构不符
    [InlineData("GitHubStore-Setup-0.1.3-win-x86.exe")]
    [InlineData("GitHubStore-Setup-0.1.3.exe")]                // 缺少架构后缀
    [InlineData("GitHubStore-Setup-0.1.4-win-x64.exe")]        // 版本与标签不符
    [InlineData("githubstore-setup-0.1.3-win-x64.exe")]        // 大小写不符
    [InlineData("GitHubStore-Setup-0.1.3-win-x64.msi")]
    public async Task AssetThatIsNotTheExpectedInstaller_IsRejected(string wrongName)
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag, installerName: wrongName));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.InstallerMissing, result.ErrorCode);
    }

    [Fact]
    public async Task DuplicateInstallerAssets_AreAmbiguousAndRejected()
    {
        using var directory = new TempDirectory();
        var json = JsonNode.Parse(Release(tag: OfficialTag))!;
        var installer = json["assets"]!.AsArray()[0]!.DeepClone();
        json["assets"]!.AsArray().Add(installer);
        using var client = Client(json.ToJsonString());
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        // 同名资产出现两次时不猜哪一个，直接拒绝。
        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.InstallerMissing, result.ErrorCode);
    }

    [Fact]
    public async Task AmbiguousReleaseTag_IsRejectedRatherThanGuessingAnAssetName()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: "nightly-build"));
        var service = Service(directory, client, currentVersion: "0.0.1");

        var result = await service.CheckAsync();

        // "nightly-build" 无法与 0.0.1 比较（Different），因此先报告“不提供更新”，
        // 绝不会去推导一个臆想的安装包名称。这里用可比较的已安装版本证明该顺序。
        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Different, result.Candidate!.Relation);
        Assert.False(result.Candidate.IsUpdateOffered);
    }

    [Fact]
    public async Task NewerButUnparsableTag_IsRejectedRatherThanGuessingAnAssetName()
    {
        using var directory = new TempDirectory();
        // v0.1.3-beta 相对 0.1.2 是可判定的“更新”，但它不是 Major.Minor.Patch，
        // 因此无法确定官方安装包名（官方资产名不含预发布后缀）：必须拒绝，而不是猜一个名字。
        using var client = Client(Release(tag: "v0.1.3-beta"));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task UnparsableTagAgainstUnparsableInstalled_IsDifferent()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: "nightly"));
        var service = Service(directory, client, currentVersion: "nightly");

        var result = await service.CheckAsync();

        // 两段完全相同的不可解析文本仍然不可比较：没有依据说它们是同一发行版的两个版本。
        Assert.True(result.Succeeded);
        Assert.Equal(VersionRelation.Different, result.Candidate!.Relation);
    }

    // ── 校验依据：缺少 / 非法 / 不符 ───────────────────────────────────

    [Fact]
    public async Task MissingChecksumAsset_RefusesToDownload()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag, includeChecksum: false));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        // 没有官方摘要就必须拒绝：把“自己算的哈希与自己对上”当成安全结论是不成立的。
        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.ChecksumAssetMissing, result.ErrorCode);
    }

    [Fact]
    public async Task MissingChecksumAsset_NeverStartsDownloadOrExecutes()
    {
        using var directory = new TempDirectory();
        using var client = Client(Release(tag: OfficialTag, includeChecksum: false));
        var downloads = new RecordingDownload();
        var launches = new List<string>();
        var service = Service(directory, client, currentVersion: "0.1.2", downloads: downloads, launches: launches);

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(0, downloads.Calls);
        Assert.Empty(launches);
        Assert.False(File.Exists(Path.Combine(service.StagingDirectory, InstallerName)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789abcdef0123456789abcdef")]                                    // 32 字符
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]     // 非十六进制
    public async Task InvalidChecksumAsset_RefusesToDownload(string checksumBody)
    {
        using var directory = new TempDirectory();
        var size = Encoding.UTF8.GetByteCount(checksumBody);
        var downloads = new VirtualDownload(checksumBody, InstallerBytes);
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag, checksumSize: size)),
            currentVersion: "0.1.2", downloads: downloads, launches: launches);

        // 空校验文件在检查阶段就被拒绝（声明 0 字节不像一行摘要）；其余在读取时被拒绝。
        var check = await service.CheckAsync();
        if (!check.Succeeded)
        {
            Assert.Equal(SelfUpdateErrorCodes.ChecksumAssetInvalid, check.ErrorCode);
        }
        else
        {
            var result = await service.DownloadAsync(check.Candidate!);
            Assert.Equal(SelfUpdateErrorCodes.ChecksumAssetInvalid, result.ErrorCode);
        }
        // 无论在哪一阶段被拒绝，安装包都不会被请求，也不会有任何执行。
        Assert.True(downloads.Calls <= 1, $"安装包不应被下载，实际调用 {downloads.Calls} 次。");
        Assert.Empty(launches);
        Assert.False(service.IsInstallerPrepared);
    }

    [Fact]
    public async Task ChecksumNamingAnotherFile_IsRejected()
    {
        using var directory = new TempDirectory();
        var body = $"{Convert.ToHexString(SHA256.HashData(InstallerBytes))}  some-other-file.exe\n";
        var downloads = new VirtualDownload(body, InstallerBytes);
        var service = Service(directory, Client(Release(tag: OfficialTag, checksumSize: Encoding.UTF8.GetByteCount(body))),
            currentVersion: "0.1.2", downloads: downloads);

        var result = await service.DownloadAsync((await service.CheckAsync()).Candidate!);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.ChecksumAssetInvalid, result.ErrorCode);
        Assert.False(service.IsInstallerPrepared);
    }

    [Fact]
    public async Task ChecksumMismatch_RefusesAndNeverExecutes()
    {
        using var directory = new TempDirectory();
        // 校验文件声明的摘要与安装包字节不符。
        var body = $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("a different payload")))}  {InstallerName}\n";
        var downloads = new VirtualDownload(body, InstallerBytes);
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag, checksumSize: Encoding.UTF8.GetByteCount(body))),
            currentVersion: "0.1.2", downloads: downloads, launches: launches);

        var result = await service.DownloadAsync((await service.CheckAsync()).Candidate!);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.ChecksumMismatch, result.ErrorCode);
        // 下载层错误码被保留用于诊断，且本次失败没有被降级成“下载失败”。
        Assert.Equal(DownloadErrorCodes.ChecksumMismatch, result.DownloadErrorCode);
        Assert.False(service.IsInstallerPrepared);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task RealDownloadPath_ChecksumMismatch_LeavesNoPublishedInstaller()
    {
        using var directory = new TempDirectory();
        var wrong = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("not the installer")));
        var checksumBody = Encoding.UTF8.GetBytes($"{wrong}  {InstallerName}\n");
        // 同一个 handler 同时服务接口与资产请求：发行接口返回 JSON，资产返回字节。
        var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? Binary(checksumBody)
            : Binary(InstallerBytes));
        using var client = new HttpClient(handler);
        // 用真实下载服务：确认经它校验失败时不会发布产物。
        var service = Service(directory, client, currentVersion: "0.1.2",
            downloads: new HttpDownloadService(client), launches: []);
        var check = await service.CheckAsync();
        // 接口请求也会命中这个 handler，因此单独用固定候选驱动下载路径。
        Assert.False(check.Succeeded);
        var candidate = Candidate(checksumSize: checksumBody.Length);

        var result = await service.DownloadAsync(candidate);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.ChecksumMismatch, result.ErrorCode);
        var staging = service.StagingDirectory;
        // 校验失败时下载层<em>不发布</em>产物：最终文件名不会出现，也就没有任何可执行的安装包。
        Assert.False(File.Exists(Path.Combine(staging, InstallerName)));
        // .part 会被保留：这是下载层既有的“可重试断点”行为，不是本服务发布的产物；
        // 它永远不会被登记为可启动，因此不构成“校验失败却仍能执行”的漏洞。
        Assert.True(File.Exists(Path.Combine(staging, InstallerName + ".part")));
        Assert.False(service.IsInstallerPrepared);
    }

    [Fact]
    public async Task RealDownloadPath_VerifiedSuccess_PublishesTheInstallerAndReportsTheBasis()
    {
        using var directory = new TempDirectory();
        var checksumBody = Encoding.UTF8.GetBytes(ChecksumBody());
        var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? Binary(checksumBody)
            : Binary(InstallerBytes));
        using var client = new HttpClient(handler);
        var launches = new List<string>();
        var service = Service(directory, client, currentVersion: "0.1.2",
            downloads: new HttpDownloadService(client), launches: launches);

        var result = await service.DownloadAsync(Candidate(checksumSize: checksumBody.Length));

        Assert.True(result.Succeeded, result.ErrorCode);
        // 走真实下载层也要如实报告校验依据，并且仍然不执行任何东西。
        Assert.Equal(ChecksumName, result.VerificationBasisChecksumName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(InstallerBytes)), result.Sha256);
        Assert.True(File.Exists(result.InstallerPath!));
        Assert.True(service.IsInstallerPrepared);
        Assert.Empty(launches);
    }

    // ── 取消 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CancellationDuringCheck_LeavesNothingExecuted()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHttpHandler(_ =>
        {
            cancellation.Cancel();
            return Json(Release(tag: OfficialTag));
        }));
        var launches = new List<string>();
        var service = Service(directory, client, currentVersion: "0.1.2", launches: launches);

        var result = await service.CheckAsync(cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.Cancelled, result.ErrorCode);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task CancellationDuringDownload_LeavesNothingPreparedOrExecuted()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var launches = new List<string>();
        // 取消发生在校验文件写入之后、安装包写入之前。
        var downloads = new VirtualDownload(ChecksumBody(), InstallerBytes, cancelAfter: 1, cancellation);
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2",
            downloads: downloads, launches: launches);

        var candidate = (await service.CheckAsync()).Candidate!;
        var result = await service.DownloadAsync(candidate, ct: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(service.IsInstallerPrepared);
        Assert.Empty(launches);
        Assert.False(File.Exists(Path.Combine(service.StagingDirectory, InstallerName)));
    }

    [Fact]
    public async Task CancellationBeforeLaunch_PreventsExecution()
    {
        using var directory = new TempDirectory();
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2",
            downloads: new VirtualDownload(ChecksumBody(), InstallerBytes), launches: launches);
        var candidate = (await service.CheckAsync()).Candidate!;
        var download = await service.DownloadAsync(candidate);
        Assert.True(download.Succeeded, download.ErrorCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.LaunchVerifiedInstallerAsync(download.InstallerPath!, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.Cancelled, result.ErrorCode);
        Assert.Empty(launches);
    }

    // ── 成功路径 ──────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessPath_ReportsTheActualVerificationBasisAndStagesOutsideInstallRoot()
    {
        using var directory = new TempDirectory();
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2",
            downloads: new VirtualDownload(ChecksumBody(), InstallerBytes), launches: launches);
        var candidate = (await service.CheckAsync()).Candidate!;

        var result = await service.DownloadAsync(candidate);

        Assert.True(result.Succeeded, result.ErrorCode);
        // 校验依据必须是官方发行版里的那个资产名，而不是本程序自造的标签。
        Assert.Equal(ChecksumName, result.VerificationBasisChecksumName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(InstallerBytes)), result.Sha256);
        Assert.Equal(InstallerBytes.Length, result.Bytes);
        // 暂存位置在应用数据目录下，且此时还没有执行任何东西。
        Assert.StartsWith(service.StagingDirectory, result.InstallerPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.IsInstallerPrepared);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task Launch_ExecutesExactlyTheVerifiedInstallerAfterConfirmation()
    {
        using var directory = new TempDirectory();
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2",
            downloads: new VirtualDownload(ChecksumBody(), InstallerBytes), launches: launches);
        var candidate = (await service.CheckAsync()).Candidate!;
        var download = await service.DownloadAsync(candidate);

        var launched = await service.LaunchVerifiedInstallerAsync(download.InstallerPath!);

        Assert.True(launched.Succeeded, launched.ErrorCode);
        Assert.Equal(download.InstallerPath, Assert.Single(launches));
    }

    [Fact]
    public async Task Launch_WithoutAPreparedInstaller_IsRefused()
    {
        using var directory = new TempDirectory();
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2",
            downloads: new VirtualDownload(ChecksumBody(), InstallerBytes), launches: launches);

        var result = await service.LaunchVerifiedInstallerAsync(directory.Combine("anything.exe"));

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.NotPrepared, result.ErrorCode);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task Launch_WithTamperedBytesAfterVerification_IsRefused()
    {
        using var directory = new TempDirectory();
        var launches = new List<string>();
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2",
            downloads: new VirtualDownload(ChecksumBody(), InstallerBytes), launches: launches);
        var candidate = (await service.CheckAsync()).Candidate!;
        var download = await service.DownloadAsync(candidate);
        Assert.True(download.Succeeded, download.ErrorCode);
        // 校验通过之后文件被替换：启动前必须重新核对，而不是相信先前的登记。
        await File.WriteAllBytesAsync(download.InstallerPath!, Encoding.UTF8.GetBytes("tampered payload!!!"));

        var result = await service.LaunchVerifiedInstallerAsync(download.InstallerPath!);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.ChecksumMismatch, result.ErrorCode);
        Assert.Empty(launches);
        Assert.False(service.IsInstallerPrepared);
    }

    [Fact]
    public async Task Launch_WithoutAnInjectedLauncher_ReportsUnavailableRatherThanGuessing()
    {
        using var directory = new TempDirectory();
        var downloads = new VirtualDownload(ChecksumBody(), InstallerBytes);
        var service = new SelfUpdateService(downloads, directory.DirectoryPath,
            Client(Release(tag: OfficialTag)), () => "0.1.2", installerLauncher: null);
        var candidate = (await service.CheckAsync()).Candidate!;
        var download = await service.DownloadAsync(candidate);

        var result = await service.LaunchVerifiedInstallerAsync(download.InstallerPath!);

        // 没有注入启动委托时不自行猜测如何启动：如实报告未接入。
        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.Unavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Launch_WhenShellStartFails_ReportsLaunchFailedWithoutRetrying()
    {
        using var directory = new TempDirectory();
        var attempts = 0;
        var service = new SelfUpdateService(new VirtualDownload(ChecksumBody(), InstallerBytes),
            directory.DirectoryPath, Client(Release(tag: OfficialTag)), () => "0.1.2",
            _ => { attempts++; throw new InvalidOperationException("shell refused"); });
        var candidate = (await service.CheckAsync()).Candidate!;
        var download = await service.DownloadAsync(candidate);

        var result = await service.LaunchVerifiedInstallerAsync(download.InstallerPath!);

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.LaunchFailed, result.ErrorCode);
        Assert.Equal(1, attempts);
    }

    // ── 版本读取与暂存位置 ────────────────────────────────────────────

    [Fact]
    public void ReadStampedVersion_StripsBuildMetadataAndNeverInventsAVersion()
    {
        // 当前测试程序集没有注入版本，因此只断言“读得到或读不到”而不编造期望值。
        var reported = SelfUpdateService.ReadStampedVersion(typeof(SelfUpdateService).Assembly);
        Assert.DoesNotContain("+", reported ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingDirectory_IsUnderTheDataDirectoryAndIsNotTheInstallRoot()
    {
        using var directory = new TempDirectory();
        var service = Service(directory, Client(Release(tag: OfficialTag)), currentVersion: "0.1.2");

        Assert.Equal(Path.Combine(directory.DirectoryPath, "updates"), service.StagingDirectory);
        Assert.StartsWith(directory.DirectoryPath, service.StagingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutHttpClient_ReportsUnavailableInsteadOfPretendingToCheck()
    {
        using var directory = new TempDirectory();
        var service = new SelfUpdateService(new RecordingDownload(), directory.DirectoryPath);

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.Unavailable, result.ErrorCode);
    }

    // ── 接口层错误 ────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, SelfUpdateErrorCodes.AccessDenied)]
    [InlineData(HttpStatusCode.Unauthorized, SelfUpdateErrorCodes.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, SelfUpdateErrorCodes.NetworkError)]
    [InlineData(HttpStatusCode.InternalServerError, SelfUpdateErrorCodes.NetworkError)]
    public async Task HttpFailures_ReportStableErrorCodes(HttpStatusCode status, string expected)
    {
        using var directory = new TempDirectory();
        using var client = Client(status: status);
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Fact]
    public async Task RateLimitedResponse_IsReportedSeparately()
    {
        using var directory = new TempDirectory();
        using var client = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.RateLimited()));
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.RateLimited, result.ErrorCode);
    }

    [Fact]
    public async Task MalformedReleasePayload_ReportsInvalidResponse()
    {
        using var directory = new TempDirectory();
        using var client = Client("{not json");
        var service = Service(directory, client, currentVersion: "0.1.2");

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SelfUpdateErrorCodes.InvalidResponse, result.ErrorCode);
    }

    // ── 夹具 ──────────────────────────────────────────────────────────

    private static SelfUpdateService Service(TempDirectory directory, HttpClient client, string? currentVersion,
        IDownloadService? downloads = null, List<string>? launches = null) =>
        new(downloads ?? new RecordingDownload(), directory.DirectoryPath, client, () => currentVersion,
            path => (launches ?? []).Add(path));

    private static HttpClient Client(string body) =>
        new(new StubHttpHandler(_ => Json(body)));

    private static HttpClient Client(HttpStatusCode status) =>
        new(new StubHttpHandler(_ => StubHttpHandler.Status(status)));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Binary(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>与构建脚本写出的格式一致：sha256sum 风格的两空格加文件名。</summary>
    private static string ChecksumBodyFor(string installerName) =>
        $"{Convert.ToHexString(SHA256.HashData(InstallerBytes))}  {installerName}\n";

    private static string ChecksumBody() => ChecksumBodyFor(InstallerName);

    private static string InstallerUrl() =>
        "https://github.com/SRW-OG/githubstore/releases/download/" + OfficialTag + "/" + InstallerName;

    private static string ChecksumUrl() =>
        "https://github.com/SRW-OG/githubstore/releases/download/" + OfficialTag + "/" + ChecksumName;

    /// <summary>
    /// 直接构造一个官方更新候选，用于绕过接口夹具、单独驱动下载路径。
    /// 字段口径与 <see cref="Release"/> 保持一致，避免两处漂移。
    /// </summary>
    private static SelfUpdateCandidate Candidate(long? checksumSize = null)
    {
        return new SelfUpdateCandidate
        {
            Tag = OfficialTag,
            Version = "0.1.3",
            Relation = VersionRelation.Newer,
            ReleaseNotesUrl = SelfUpdatePolicy.ReleaseNotesUrl(OfficialTag),
            InstallerName = InstallerName,
            InstallerUri = new Uri(InstallerUrl()),
            InstallerSize = InstallerBytes.Length,
            ChecksumName = ChecksumName,
            ChecksumUri = new Uri(ChecksumUrl()),
            ChecksumSize = checksumSize ?? Encoding.UTF8.GetByteCount(ChecksumBody()),
        };
    }

    /// <summary>
    /// 合成一个官方仓库的最新发行版响应。默认同时提供安装包与同名 .sha256 资产，
    /// 以便针对单个拒绝点做最小改动。
    /// </summary>
    private static string Release(string tag, bool draft = false, bool prerelease = false,
        bool includeChecksum = true, string? installerName = null, string? installerUrl = null,
        long? checksumSize = null)
    {
        var name = installerName ?? InstallerName;
        var checksumName = name + ".sha256";
        var assets = new JsonArray
        {
            Asset(name, InstallerBytes.Length, installerUrl ?? InstallerUrl()),
        };
        if (includeChecksum)
            // 发行接口声明的字节数必须与真实资产一致：客户端会据此拒绝大小可疑的响应。
            assets.Add(Asset(checksumName, checksumSize ?? Encoding.UTF8.GetByteCount(ChecksumBodyFor(name)), ChecksumUrl()));
        var root = new JsonObject
        {
            ["tag_name"] = tag,
            ["name"] = "GitHubStore " + tag,
            ["body"] = "Synthetic release notes for offline tests.",
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["published_at"] = "2026-10-20T00:00:00Z",
            ["assets"] = assets,
        };
        return root.ToJsonString();
    }

    private static JsonObject Asset(string name, long size, string url) => new()
    {
        ["id"] = Math.Abs(name.GetHashCode(StringComparison.Ordinal)),
        ["name"] = name,
        ["size"] = size,
        ["content_type"] = "application/octet-stream",
        ["browser_download_url"] = url,
        ["download_count"] = 1,
    };

    /// <summary>只记录请求、从不写盘的下载替身；用于断言“根本没有发起下载”。</summary>
    private sealed class RecordingDownload : IDownloadService
    {
        public int Calls { get; private set; }
        public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);
        public Task<DownloadResult> DownloadAsync(DownloadRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(DownloadResult.Failure(DownloadErrorCodes.NetworkError, request.Source));
        }
    }

    /// <summary>
    /// 按请求地址写出合成的校验文件与安装包字节，可在第 N 次写入后触发取消，
    /// 让“取消发生在安装包落盘之前”这一情形可被确定地复现。
    /// </summary>
    private sealed class VirtualDownload(byte[] checksum, byte[] installer, int? cancelAfter = null,
        CancellationTokenSource? cancellation = null) : IDownloadService
    {
        private byte[] _checksum = checksum;
        private byte[] _installer = installer;

        public VirtualDownload(string checksumBody, byte[] installerBytes, int? cancelAfter = null,
            CancellationTokenSource? cancellation = null)
            : this(Encoding.UTF8.GetBytes(checksumBody), installerBytes, cancelAfter, cancellation) { }

        public int Calls { get; private set; }
        public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            var body = request.Source.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? _checksum : _installer;
            // 长度与请求声明不一致时如实报告，绝不写出大小可疑的产物。
            if (request.ExpectedSize is { } expected && expected != body.Length)
                return DownloadResult.Failure(DownloadErrorCodes.LengthMismatch, request.Source);
            // 与真实下载层一致：声明了 ExpectedSha256 就必须真正核对，不符则<em>不</em>写产物。
            if (request.ExpectedSha256 is { } digest)
            {
                var actual = Convert.ToHexString(SHA256.HashData(body));
                if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase))
                    return DownloadResult.Failure(DownloadErrorCodes.ChecksumMismatch, request.Source);
            }
            await File.WriteAllBytesAsync(request.TargetPath, body, ct);
            progress?.Report(new DownloadProgress
            { Source = request.Source, Done = body.Length, Total = body.Length, Message = "completed" });
            if (cancelAfter is { } limit && Calls >= limit) cancellation?.Cancel();
            return DownloadResult.Success(request.Source, request.TargetPath, body.Length, request.Engine);
        }
    }
}
