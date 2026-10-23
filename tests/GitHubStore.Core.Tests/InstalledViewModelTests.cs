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
