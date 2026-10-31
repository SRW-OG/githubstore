using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 已安装程序页面。卸载结果到用户可见提示的映射在这里，
/// 全部用假服务驱动，不读注册表、不启动任何进程。
/// </summary>
[Trait("Category", "Unit")]
public sealed class InstalledViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsProgramsAndReportsTheCount()
    {
        var service = new FakeService
        {
            Programs =
            [
                MakeProgram("Alpha", "uninstall alpha.exe"),
                MakeProgram("Beta", "uninstall beta.exe"),
            ],
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Programs.Count);
        Assert.Contains("2", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Initialize_RunsOnlyOnce()
    {
        var service = new FakeService { Programs = [MakeProgram("Alpha", "a.exe")] };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));

        await viewModel.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(1, service.ScanCount);
    }

    [Fact]
    public async Task ScanFailure_IsReportedWithoutThrowing()
    {
        var service = new FakeService { ScanError = new InvalidOperationException("registry unavailable") };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));

        await viewModel.InitializeAsync();

        Assert.Contains("registry unavailable", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData("alpha", 1)]
    [InlineData("BETA", 1)]
    [InlineData("Publisher", 2)]
    [InlineData("nothing-matches", 0)]
    public async Task Filter_MatchesNamePublisherAndLocation(string query, int expected)
    {
        var service = new FakeService
        {
            Programs =
            [
                MakeProgram("Alpha", "a.exe", publisher: "Fixture Publisher"),
                MakeProgram("Beta", "b.exe", publisher: "Fixture Publisher"),
            ],
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();

        viewModel.Filter = query;

        Assert.Equal(expected, viewModel.Programs.Count);
    }

    [Fact]
    public async Task CanUninstall_IsFalseWhenNoUninstallCommandExists()
    {
        var service = new FakeService { Programs = [MakeProgram("Alpha", uninstall: null, quiet: "   ")] };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();

        viewModel.SelectedProgram = viewModel.Programs[0];

        Assert.True(viewModel.HasSelection);
        Assert.False(viewModel.CanUninstall);
    }

    [Fact]
    public async Task CanUninstall_IsTrueForAProgramWithACommand()
    {
        var service = new FakeService { Programs = [MakeProgram("Alpha", "\"C:\\Alpha\\u.exe\"")] };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();

        viewModel.SelectedProgram = viewModel.Programs[0];

        Assert.True(viewModel.CanUninstall);
    }

    [Fact]
    public async Task Uninstall_OnSuccess_ReportsStartedAndPassesTheSelectedProgram()
    {
        var service = new FakeService
        {
            Programs = [MakeProgram("Alpha", "\"C:\\Alpha\\u.exe\"")],
            LaunchResult = UninstallLaunchResult.Success("\"C:\\Alpha\\u.exe\""),
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Same(viewModel.Programs[0], service.LastLaunched);
        Assert.Contains("Alpha", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Uninstall_WithNoRegisteredCommand_ExplainsTheManualFallback()
    {
        var service = new FakeService
        {
            Programs = [MakeProgram("Alpha", null, quiet: "   ")],
            LaunchResult = UninstallLaunchResult.Failure(UninstallErrorCodes.NoUninstallString),
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        await viewModel.UninstallCommand.ExecuteAsync(null);

        // 失败走 ErrorMessage（红色），成功才走 StatusMessage。
        Assert.Contains("手动处理", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.StatusMessage);
    }

    [Fact]
    public async Task Uninstall_OnGenericFailure_NamesTheProgramWithoutLeakingAPlaceholder()
    {
        var service = new FakeService
        {
            Programs = [MakeProgram("Alpha", "\"C:\\Alpha\\u.exe\"")],
            LaunchResult = UninstallLaunchResult.Failure(UninstallErrorCodes.LaunchFailed),
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Contains("Alpha", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.StatusMessage);
    }

    [Fact]
    public async Task Uninstall_OnAccessDenied_UsesPermissionWordingNotGitHubWording()
    {
        var service = new FakeService
        {
            Programs = [MakeProgram("Alpha", "\"C:\\Alpha\\u.exe\"")],
            LaunchResult = UninstallLaunchResult.Failure(UninstallErrorCodes.AccessDenied),
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Contains("权限不足", viewModel.ErrorMessage, StringComparison.Ordinal);
        // 卸载权限失败与 GitHub API 失败是两回事，不能出现令牌措辞。
        Assert.DoesNotContain("令牌", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(UninstallErrorCodes.NoUninstallString)]
    [InlineData(UninstallErrorCodes.AccessDenied)]
    [InlineData(UninstallErrorCodes.LaunchFailed)]
    public async Task Uninstall_OnFailure_NeverSurfacesTheRawErrorCode(string errorCode)
    {
        var service = new FakeService
        {
            Programs = [MakeProgram("Alpha", "\"C:\\Alpha\\u.exe\"")],
            LaunchResult = UninstallLaunchResult.Failure(errorCode),
        };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        await viewModel.UninstallCommand.ExecuteAsync(null);

        // 稳定错误码只用于内部映射，任何情况下都不能出现在用户文案里。
        Assert.NotEmpty(viewModel.ErrorMessage);
        Assert.DoesNotContain(errorCode, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(viewModel.StatusMessage);
    }

    [Fact]
    public async Task Uninstall_WithNoSelection_DoesNothing()
    {
        var service = new FakeService { Programs = [MakeProgram("Alpha", "a.exe")] };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Null(service.LastLaunched);
    }

    [Fact]
    public async Task Dispose_CancelsScanAndStopsReporting()
    {
        var service = new FakeService { Programs = [MakeProgram("Alpha", "a.exe")] };
        var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));

        viewModel.Dispose();
        await viewModel.RescanCommand.ExecuteAsync(null);

        Assert.Equal(0, service.ScanCount);
    }

    // ── task-16：回退刷新与升级入口 ────────────────────────────────────────────

    /// <summary>
    /// 回退成功后必须真的重扫一次。RescanAsync 见到 _uninstallInProgress 会直接早退，
    /// 若在忙碌标志还没清掉时调用，界面就会继续显示已被替换掉的旧版本。
    /// </summary>
    [Fact]
    public async Task Rollback_OnSuccess_ActuallyRescansAndDoesNotEarlyReturn()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        // 先用真实服务跑一次更新到 1.1.0。
        Assert.True((await h.UpgradeAsync("1.1.0", "v2")).Succeeded);
        // 新回退机制从软件源重装更旧版本，必须提供可回退的旧发行版。
        h.ConfigureOlderRelease("1.0.0", "v1-old");
        var service = h.Service();
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"), null, h.Upgrades);
        await viewModel.InitializeAsync();
        var scansAfterInit = service.ScanCount;
        viewModel.SelectedProgram = viewModel.Programs[0];

        var applied = await viewModel.RollbackConfirmedAsync(viewModel.Programs[0]);

        Assert.True(applied, viewModel.ErrorMessage);
        // 初始化时扫过一次；回退成功后必须再扫一次（不是被 _uninstallInProgress 挡掉的空调用）。
        Assert.Equal(scansAfterInit + 1, service.ScanCount);
        Assert.False(viewModel.IsBusy);
    }

    /// <summary>回退的中间状态必须如实说明，不能套用“回退失败，当前版本已保留”这种不准确的文案。</summary>
    [Fact]
    public async Task Rollback_ReceiptRestoreFailure_ReportsIncompleteInsteadOfGenericFailure()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        Assert.True((await h.UpgradeAsync("1.1.0", "v2")).Succeeded);
        h.ConfigureOlderRelease("1.0.0", "v1-old");
        var service = h.Service();
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"), null, h.Upgrades);
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];
        // 回滚时登记写回失败：文件可能已换回旧版本，但登记还是新的——这是“中间状态”，不是普通失败。
        h.Launcher.OnRegister = _ => throw new IOException("manifest is read-only");

        var applied = await viewModel.RollbackConfirmedAsync(viewModel.Programs[0]);

        Assert.False(applied);
        var generic = viewModel.Text["InstalledRollbackFailed"];
        Assert.NotEqual(generic, viewModel.ErrorMessage);
        Assert.DoesNotContain("{0}", viewModel.ErrorMessage, StringComparison.Ordinal);
        // 中间状态必须触发重扫，让界面反映磁盘上真实的样子。
        Assert.True(h.Upgrades.HasPendingRecovery(h.InstallId));
    }

    [Fact]
    public async Task Rollback_WithoutRetainedVersion_SaysSoWithoutClaimingFailure()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        var service = h.Service();
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"), null, h.Upgrades);
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        await viewModel.RollbackConfirmedAsync(viewModel.Programs[0]);

        Assert.Equal(viewModel.Text["InstalledRollbackUnavailable"], viewModel.ErrorMessage);
    }

    /// <summary>没有配置升级服务时，升级入口必须整体不可用，而不是留下一个点了没反应的按钮。</summary>
    [Fact]
    public async Task WithoutUpgradeService_TheUpdateEntryPointsStayDisabled()
    {
        var service = new FakeService { Programs = [MakeProgram("Alpha", "uninstall alpha.exe")] };
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"));
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];

        Assert.False(viewModel.CanCheckForUpdate);
        Assert.False(viewModel.CanUpgrade);
        Assert.False(viewModel.CanRollback);
        Assert.Empty(viewModel.UpdateStatusText);
    }

    /// <summary>
    /// 候选更新只对它被查到的那条安装有效。选中项一变就必须作废，
    /// 否则用户可能把 A 的候选确认到 B 上。
    /// </summary>
    [Fact]
    public async Task UpdateCandidateIsDiscardedWhenSelectionChanges()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        h.Catalog.Detail = new AppDetail
        {
            App = h.App,
            Releases = new[]
            {
                new Release
                {
                    AppId = h.App.Id, Key = "release-1", TagName = "1.1.0",
                    Assets = new[]
                    {
                        new Asset
                        {
                            InstallerId = "asset-2", ReleaseKey = "release-1",
                            Name = "Tool-1.1.0-windows-portable.zip",
                            DownloadUrl = "https://example.com/Tool-1.1.0-windows-portable.zip",
                        },
                    },
                },
            },
        };
        var service = h.Service();
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"), null, h.Upgrades);
        await viewModel.InitializeAsync();
        viewModel.SelectedProgram = viewModel.Programs[0];
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);
        Assert.True(viewModel.CanUpgrade);

        // 换一个选中项（这里换成一条同样可检查、但没有查到候选的登记）：候选必须作废。
        service.Programs = [MakeProgram("Beta", "uninstall beta.exe")];
        await viewModel.RescanCommand.ExecuteAsync(null);
        viewModel.SelectedProgram = viewModel.Programs.First(p => p.DisplayName == "Beta");

        Assert.False(viewModel.CanUpgrade);
        Assert.Empty(viewModel.UpdateStatusText);
    }

    [Fact]
    public async Task Scan_PopulatesInstalledItemsWithAutomaticCandidateState()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        h.ConfigureCandidate("1.1.0", "v2");
        var service = h.Service();
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"), null, h.Upgrades);

        await viewModel.InitializeAsync();

        var item = Assert.Single(viewModel.InstalledItems);
        Assert.Same(Assert.Single(viewModel.Programs), item.Program);
        Assert.Equal("Tool", item.DisplayName);
        Assert.Equal("—", item.Publisher);
        Assert.Equal("1.0.0", item.CurrentVersion);
        Assert.Equal("1.1.0", item.LatestVersion);
        Assert.True(item.CanUninstall);
        Assert.True(item.CanUpdate);
        viewModel.SelectedProgram = item.Program;
        Assert.True(viewModel.CanUpgrade);
    }

    [Fact]
    public async Task Scan_CandidateNotFoundShowsCurrentVersionAsLatestSupported()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        h.Catalog.Detail = new AppDetail { App = h.App, Releases = [] };
        using var viewModel = new InstalledViewModel(h.Service(), new LocalizationService("zh-CN"), null, h.Upgrades);

        await viewModel.InitializeAsync();

        var item = Assert.Single(viewModel.InstalledItems);
        Assert.Equal(item.CurrentVersion, item.LatestVersion);
        Assert.False(item.CanUpdate);
        viewModel.SelectedProgram = item.Program;
        Assert.False(viewModel.CanUpgrade);
        Assert.Equal(viewModel.Text["InstalledUpdateCurrent"], viewModel.UpdateStatusText);
    }

    [Fact]
    public async Task Scan_CatalogUnavailableDoesNotClaimCurrentVersionIsLatest()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        using var viewModel = new InstalledViewModel(h.Service(), new LocalizationService("zh-CN"), null, h.Upgrades);

        await viewModel.InitializeAsync();

        var item = Assert.Single(viewModel.InstalledItems);
        Assert.Equal("1.0.0", item.CurrentVersion);
        Assert.Equal("—", item.LatestVersion);
        Assert.False(item.CanUpdate);
        viewModel.SelectedProgram = item.Program;
        Assert.Equal(viewModel.Text["InstalledUpdateUnavailable"], viewModel.UpdateStatusText);
    }

    [Fact]
    public async Task Scan_ReportsRollbackAvailabilityPerRow()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        Assert.True((await h.UpgradeAsync("1.1.0", "v2")).Succeeded);
        // 回退可用性来自软件源：提供更旧的发行版后，Tool 这一行才应显示可回退。
        h.ConfigureOlderRelease("1.0.0", "v1-old");
        var service = h.Service();
        service.Programs = [service.Programs[0], MakeProgram("External", "external-uninstall.exe")];
        using var viewModel = new InstalledViewModel(service, new LocalizationService("zh-CN"), null, h.Upgrades);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.InstalledItems.Single(item => item.DisplayName == "Tool").CanRollback);
        Assert.False(viewModel.InstalledItems.Single(item => item.DisplayName == "External").CanRollback);
    }

    [Fact]
    public async Task UpgradeConfirmed_UsesAutomaticallyInspectedSelectedRowCandidate()
    {
        using var h = new UpgradeHarness();
        h.Seed("1.0.0", "v1");
        h.ConfigureCandidate("1.1.0", "v2");
        using var viewModel = new InstalledViewModel(h.Service(), new LocalizationService("zh-CN"), null, h.Upgrades);
        await viewModel.InitializeAsync();
        var item = Assert.Single(viewModel.InstalledItems);
        viewModel.SelectedProgram = item.Program;

        var applied = await viewModel.UpgradeConfirmedAsync(item.Program);

        Assert.True(applied, viewModel.ErrorMessage);
        Assert.Equal("1.1.0", Assert.Single(h.Launcher.Receipts).DisplayVersion);
    }

    /// <summary>
    /// 用真实的 <c>PortableUpgradeService</c> 驱动 ViewModel：只有真实服务才能证明
    /// “回退确实换了文件、登记确实被改写”这类结论，假实现只是自证其说。
    /// 全程使用临时目录，不接触任何真实安装。
    /// </summary>
    private sealed class UpgradeHarness : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "GitHubStore-installedvm-tests-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(_base, "apps");
        public string Data => Path.Combine(_base, "data");
        public string InstallId { get; private set; } = string.Empty;
        public FakeLauncher Launcher { get; } = new();
        public FakeDownload Download { get; } = new();
        public GitHubStore.App.Services.Installation.PortableUpgradeService Upgrades { get; }
        public InstalledViewModelTests.FakeCatalog Catalog { get; } = new();
        public GitHubStore.Core.Abstractions.App App { get; } = new()
        {
            Id = new(new(Guid.NewGuid()), new("owner/tool")), SourceRevision = 3,
            DisplayName = "Tool", Owner = "owner", Repo = "tool",
        };

        public UpgradeHarness()
        {
            Directory.CreateDirectory(_base);
            var preferences = new GitHubStore.App.Services.Preferences.PreferencesService(Path.Combine(Data, "preferences.json"));
            Upgrades = new GitHubStore.App.Services.Installation.PortableUpgradeService(
                Download, Launcher, preferences, Data, installRootProvider: () => Root, catalog: Catalog);
        }

        /// <summary>写入一条真实的便携安装；随后它同时被登记器与已安装页看到。</summary>
        public void Seed(string version, string marker)
        {
            InstallId = Guid.NewGuid().ToString("N");
            var target = Directory.CreateDirectory(Path.Combine(Root, "Tool")).FullName;
            var executable = Path.Combine(target, "Tool.exe");
            File.WriteAllBytes(executable, Marker(marker));
            Launcher.Receipts.Add(new InstallRecord
            {
                InstallId = InstallId, AppId = App.Id,
                Source = new SourceBinding
                {
                    SourceId = App.Id.SourceId, PackageId = App.Id.PackageId, SourceRevision = 1,
                    SourceEntryUrl = new Uri("https://github.com/owner/tool"), InstallerId = "asset-0",
                    ArtifactUrl = new Uri("https://example.com/Tool.zip"), ArtifactSha256 = new string('a', 64),
                },
                ReleaseKey = "release-0", Origin = Origin.Store, Channel = Channel.Http,
                Strategy = Strategy.Green, Installer = InstallerKind.Portable,
                Version = new GitHubStore.Core.Abstractions.Version { Raw = version, Scheme = VersionScheme.Opaque },
                DisplayVersion = version, TargetDir = target, ExePath = executable, State = InstallState.Done,
                InstalledAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        public void ConfigureCandidate(string tag, string marker)
        {
            var name = "Tool-" + tag + "-windows-portable.zip";
            var asset = new Asset
            {
                InstallerId = "asset-2", ReleaseKey = "release-1", Name = name,
                DownloadUrl = "https://example.com/" + name,
            };
            Catalog.Detail = new AppDetail
            {
                App = App,
                Releases =
                [
                    new Release
                    {
                        AppId = App.Id, Key = "release-1", TagName = tag, Assets = [asset],
                    },
                ],
            };
            Download.Bytes = Zip(("Tool.exe", Marker(marker)));
        }

        /// <summary>
        /// 为新回退机制配置可回退的更旧发行版：回退不再读本地备份，而是从目录重装旧版本。
        /// </summary>
        public void ConfigureOlderRelease(string tag, string marker)
        {
            var name = "Tool-" + tag + "-windows-portable.zip";
            var asset = new Asset
            {
                InstallerId = "asset-old", ReleaseKey = "release-old", Name = name,
                DownloadUrl = "https://example.com/" + name,
            };
            Catalog.Detail = new AppDetail
            {
                App = App,
                Releases =
                [
                    new Release
                    {
                        AppId = App.Id, Key = "release-old", TagName = tag,
                        PublishedAt = DateTimeOffset.UtcNow.AddDays(-10), Assets = [asset],
                    },
                ],
            };
            Download.Bytes = Zip(("Tool.exe", Marker(marker)));
        }

        /// <summary>
        /// 用真实服务执行一次更新，以便产生“保留的上一版本”。
        /// 候选发行版与资产都按真实形状构造，版本比较走真实实现。
        /// </summary>
        public async Task<GitHubStore.App.Services.Installation.PortableUpgradeResult> UpgradeAsync(string tag, string marker)
        {
            var name = "Tool-" + tag + "-windows-portable.zip";
            var asset = new Asset
            {
                InstallerId = "asset-2", ReleaseKey = "release-1", Name = name,
                DownloadUrl = "https://example.com/" + name,
            };
            Download.Bytes = Zip(("Tool.exe", Marker(marker)));
            return await Upgrades.UpdateAsync(new GitHubStore.App.Services.Installation.PortableUpgradeRequest
            {
                InstallId = InstallId, App = App,
                Release = new Release { AppId = App.Id, Key = "release-1", TagName = tag, Assets = new[] { asset } },
                Asset = asset,
            });
        }

        private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
        {
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(file.Name, System.IO.Compression.CompressionLevel.Fastest);
                    using var stream = entry.Open();
                    stream.Write(file.Bytes);
                }
            return memory.ToArray();
        }

        /// <summary>已安装页看到的那条记录：Store 来源、可运行、SourceKeyPath 是安装 ID。</summary>
        public FakeService Service() => new()
        {
            Programs =
            [
                new InstalledProgram
                {
                    DisplayName = "Tool", DisplayVersion = "1.0.0", InstallLocation = Path.Combine(Root, "Tool"),
                    Origin = RegistryOrigin.StoreInstall, SourceKeyPath = InstallId,
                    Status = InstalledStatus.Installed, StoreManagedRemoval = true,
                    CanRemoveRegistration = false,
                },
            ],
        };

        private static byte[] Marker(string marker)
        {
            var bytes = new byte[128 + marker.Length];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
            bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 2);
            System.Text.Encoding.UTF8.GetBytes(marker).CopyTo(bytes, 128);
            return bytes;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_base)) Directory.Delete(_base, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>只保存登记的假启动器，语义与真实实现一致：同一 InstallId 不会变成两条记录。</summary>
    private sealed class FakeLauncher : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public List<InstallRecord> Receipts { get; } = new();
        public Action<InstallRecord>? OnRegister { get; set; }

        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LauncherApp>>(Array.Empty<LauncherApp>());

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

    /// <summary>把预设字节写进目标路径；不联网、不执行任何内容。</summary>
    private sealed class FakeDownload : IDownloadService
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public IReadOnlyList<DownloadEngine> AvailableEngines => new[] { DownloadEngine.Http };
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            await File.WriteAllBytesAsync(request.TargetPath, Bytes, ct);
            return DownloadResult.Success(request.Source, request.TargetPath, Bytes.Length, request.Engine);
        }
    }

    /// <summary>只按 AppId 提供目录详情的假目录；本用例不触发刷新或搜索。</summary>
    private sealed class FakeCatalog : ICatalogService
    {
        public AppDetail? Detail { get; set; }
        public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated { add { } remove { } }
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Category>>(Array.Empty<Category>());
        public Task<CatalogPage> BrowseAsync(CatalogQuery query, CancellationToken ct = default) => Task.FromResult(new CatalogPage());
        public Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> RankAsync(BoardKind board, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitHubStore.Core.Abstractions.App>>(Array.Empty<GitHubStore.Core.Abstractions.App>());
        public Task<AppDetail> GetAppAsync(AppId id, CancellationToken ct = default) =>
            Detail is null ? Task.FromException<AppDetail>(new KeyNotFoundException("No catalog detail configured."))
                : Task.FromResult(Detail);
        public Task RefreshAsync(RefreshScope scope, IProgress<RefreshProgress>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static InstalledProgram MakeProgram(string displayName, string? uninstall, string? quiet = null,
        string publisher = "Fixture Publisher") => new()
    {
        DisplayName = displayName,
        Publisher = publisher,
        DisplayVersion = "1.0.0",
        InstallLocation = @"C:\Apps\" + displayName,
        UninstallString = uninstall,
        QuietUninstallString = quiet,
        Origin = RegistryOrigin.Hklm64,
        SourceKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + displayName,
    };

    /// <summary>离线假实现：不读注册表、不启动进程。</summary>
    private sealed class FakeService : IInstalledProgramService
    {
        public IReadOnlyList<InstalledProgram> Programs { get; set; } = [];
        public Exception? ScanError { get; set; }
        public UninstallLaunchResult LaunchResult { get; set; } =
            UninstallLaunchResult.Failure(UninstallErrorCodes.LaunchFailed);
        public InstalledProgram? LastLaunched { get; private set; }
        public int ScanCount { get; private set; }

        public Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default)
        {
            ScanCount++;
            ct.ThrowIfCancellationRequested();
            return ScanError is not null
                ? Task.FromException<IReadOnlyList<InstalledProgram>>(ScanError)
                : Task.FromResult(Programs);
        }

        public Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default)
        {
            LastLaunched = program;
            return Task.FromResult(LaunchResult);
        }
    }
}
