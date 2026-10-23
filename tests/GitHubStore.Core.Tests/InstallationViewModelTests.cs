using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class InstallationViewModelTests
{
    [Fact]
    public void InitialState_AndTargetDirectoryForwarding()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var app = Request().App;

        Assert.False(vm.IsInstalling);
        Assert.False(vm.CanCancel);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Equal(0d, vm.Percent);
        Assert.Empty(vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
        Assert.Empty(vm.TargetDirectory);
        Assert.Empty(vm.ExecutablePath);
        Assert.Equal(service.Target, vm.GetTargetDirectory(app));
        Assert.Same(app, service.TargetApp);
    }

    [Fact]
    public async Task Success_ForwardsRequestAndChooserAndDisplaysVerifiedPaths()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var request = Request();
        var candidates = new[] { "tool.exe", "other.exe" };
        CancellationToken callbackToken = default;
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>> chooser = (entries, ct) =>
        {
            Assert.Same(candidates, entries);
            callbackToken = ct;
            return Task.FromResult<string?>(entries[0]);
        };
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(request, chooser));
        var call = Assert.Single(service.Calls);
        Assert.Same(request, call.Request);
        Assert.Same(chooser, call.Chooser);
        Assert.True(call.Token.CanBeCanceled);
        Assert.Equal(candidates[0], await call.Chooser!(candidates, call.Token));
        Assert.Equal(call.Token, callbackToken);
        Assert.True(vm.IsInstalling);
        Assert.True(vm.CanCancel);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.True(vm.IsIndeterminate);
        Assert.Equal(vm.Text["InstallPreparing"], vm.StatusMessage);
        Assert.Equal(service.Target, vm.TargetDirectory);

        call.Completion.SetResult(new AppInstallResult(true, TargetDirectory: "verified-target", ExecutablePath: "verified.exe"));
        context.Drain();
        await operation;

        Assert.False(vm.IsInstalling);
        Assert.False(vm.CanCancel);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.IsIndeterminate);
        Assert.Equal(100d, vm.Percent);
        Assert.Equal("verified-target", vm.TargetDirectory);
        Assert.Equal("verified.exe", vm.ExecutablePath);
        Assert.Equal(vm.Text["InstallCompleted"], vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
    }

    [Theory]
    [InlineData(AppInstallPhase.Preparing, "InstallPreparing")]
    [InlineData(AppInstallPhase.Downloading, "InstallDownloading")]
    [InlineData(AppInstallPhase.Extracting, "InstallExtracting")]
    [InlineData(AppInstallPhase.RunningInstaller, "InstallRunningInstaller")]
    [InlineData(AppInstallPhase.Verifying, "InstallVerifying")]
    [InlineData(AppInstallPhase.Registering, "InstallRegistering")]
    [InlineData(AppInstallPhase.Completed, "InstallCompleted")]
    public async Task Progress_IsPostedToCallingContext_AndLocalized(AppInstallPhase phase, string key)
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        var call = Assert.Single(service.Calls);
        Assert.IsType<Progress<AppInstallProgress>>(call.Progress);

        call.Progress!.Report(new AppInstallProgress(phase, 42.5));
        Assert.Equal(0d, vm.Percent);
        context.Drain();
        Assert.Equal(42.5, vm.Percent);
        Assert.False(vm.IsIndeterminate);
        Assert.Equal(vm.Text[key], vm.StatusMessage);
        call.Progress.Report(new AppInstallProgress(phase));
        context.Drain();
        Assert.True(vm.IsIndeterminate);
        Assert.Equal(42.5, vm.Percent);

        call.Completion.SetResult(new AppInstallResult(true));
        context.Drain();
        await operation;
    }

    [Theory]
    [InlineData(-5d, 0d, false)]
    [InlineData(150d, 100d, false)]
    [InlineData(double.NaN, 0d, true)]
    [InlineData(double.PositiveInfinity, 0d, true)]
    public async Task Progress_SanitizesPercent(double value, double expected, bool indeterminate)
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        var call = service.Calls[0];
        call.Progress!.Report(new AppInstallProgress(AppInstallPhase.Downloading, value));
        context.Drain();
        Assert.Equal(expected, vm.Percent);
        Assert.Equal(indeterminate, vm.IsIndeterminate);
        call.Completion.SetResult(new AppInstallResult(true));
        context.Drain();
        await operation;
    }

    [Theory]
    [InlineData("unsupported_7z", "InstallUnsupported7z")]
    [InlineData("install_target_exists", "InstallTargetExists")]
    [InlineData("installation-target-directory-exists", "InstallTargetExists")]
    [InlineData("InstallEntryNotFound", "InstallEntryNotFound")]
    [InlineData("app_install_invalid_archive", "InstallInvalidArchive")]
    [InlineData("archive_path_traversal", "InstallInvalidArchive")]
    [InlineData("download_failed", "InstallDownloadFailed")]
    [InlineData("ACCESS_DENIED", "InstallAccessDenied")]
    [InlineData("install_busy", "InstallBusy")]
    [InlineData("installation_in_progress", "InstallBusy")]
    [InlineData("operation_canceled", "InstallCancelled")]
    [InlineData("unrecognized sensitive C:/private/file", "InstallFailed")]
    [InlineData(null, "InstallFailed")]
    public async Task Failure_MapsOnlyKnownCodes(string? code, string expectedKey)
    {
        Assert.Equal(expectedKey, InstallationViewModel.ErrorKey(code));
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        service.Calls[0].Completion.SetResult(new AppInstallResult(false, code));
        context.Drain();
        await operation;
        Assert.False(vm.IsInstalling);
        Assert.False(vm.IsIndeterminate);
        Assert.Equal(vm.Text[expectedKey == "InstallCancelled" ? "InstallCancelled" : "InstallFailed"], vm.StatusMessage);
        Assert.Equal(expectedKey == "InstallCancelled" ? string.Empty : vm.Text[expectedKey], vm.ErrorMessage);
    }

    [Fact]
    public async Task Exception_DoesNotExposeRawDetails_AndNextAttemptCanSucceed()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var first = context.Start(() => vm.InstallAsync(Request()));
        service.Calls[0].Completion.SetException(new IOException("Secret path C:/private/token"));
        context.Drain();
        await first;
        Assert.Equal(vm.Text["InstallFailed"], vm.ErrorMessage);
        Assert.DoesNotContain("Secret", vm.ErrorMessage);

        var second = context.Start(() => vm.InstallAsync(Request()));
        Assert.Empty(vm.ErrorMessage);
        service.Calls[1].Completion.SetResult(new AppInstallResult(true));
        context.Drain();
        await second;
        Assert.Equal(vm.Text["InstallCompleted"], vm.StatusMessage);
    }

    [Fact]
    public async Task DuplicateInvocation_IsRejectedWithoutDisturbingActiveState()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var first = context.Start(() => vm.InstallAsync(Request()));
        var initialStatus = vm.StatusMessage;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => vm.InstallAsync(Request()));
        Assert.Equal(vm.Text["InstallBusy"], error.Message);
        Assert.Single(service.Calls);
        Assert.True(vm.IsInstalling);
        Assert.Equal(initialStatus, vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
        service.Calls[0].Completion.SetResult(new AppInstallResult(true));
        context.Drain();
        await first;
    }

    [Fact]
    public async Task CancelCommand_CancelsServiceToken_AndKeepsFilesMayRemainWarning()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        var call = service.Calls[0];

        await vm.CancelCommand.ExecuteAsync(null);
        Assert.True(call.Token.IsCancellationRequested);
        Assert.True(vm.IsInstalling);
        Assert.False(vm.CanCancel);
        Assert.False(vm.CancelCommand.CanExecute(null));
        call.Progress!.Report(new AppInstallProgress(AppInstallPhase.Downloading, 80));
        context.Drain();
        Assert.Equal(0d, vm.Percent);
        call.Completion.SetResult(new AppInstallResult(false, "cancelled", FilesMayRemain: true));
        context.Drain();
        await operation;

        Assert.Contains(vm.Text["InstallCancelled"], vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(vm.Text["InstallFilesMayRemain"], vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(vm.ErrorMessage);
        Assert.False(vm.IsInstalling);
        vm.Text.ApplyLanguage("zh-CN");
        Assert.Contains(vm.Text["InstallFilesMayRemain"], vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationException_EndsAsCancelled()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        var call = service.Calls[0];
        await vm.CancelCommand.ExecuteAsync(null);
        call.Completion.SetCanceled(call.Token);
        context.Drain();
        await operation;
        Assert.Equal(vm.Text["InstallCancelled"], vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task CompletedResult_AfterCancellation_IsAuthoritative_AndKeepsRestartWarning()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        await vm.CancelCommand.ExecuteAsync(null);
        service.Calls[0].Completion.SetResult(new AppInstallResult(true, RequiresRestart: true));
        context.Drain();
        await operation;
        Assert.Contains(vm.Text["InstallCompleted"], vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(vm.Text["InstallRestartRequired"], vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(100d, vm.Percent);
    }

    [Fact]
    public async Task LateProgress_CannotOverwriteCompletedOrNewInstallation()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var first = context.Start(() => vm.InstallAsync(Request()));
        var old = service.Calls[0];
        old.Completion.SetResult(new AppInstallResult(true));
        old.Progress!.Report(new AppInstallProgress(AppInstallPhase.Downloading, 7));
        context.Drain();
        await first;
        Assert.Equal(vm.Text["InstallCompleted"], vm.StatusMessage);
        Assert.Equal(100d, vm.Percent);

        var second = context.Start(() => vm.InstallAsync(Request()));
        old.Progress.Report(new AppInstallProgress(AppInstallPhase.Extracting, 9));
        context.Drain();
        Assert.Equal(vm.Text["InstallPreparing"], vm.StatusMessage);
        Assert.Equal(0d, vm.Percent);
        service.Calls[1].Completion.SetResult(new AppInstallResult(true));
        context.Drain();
        await second;
    }

    [Fact]
    public async Task Dispose_CancelsAndSuppressesQueuedProgressAndLateResult()
    {
        var service = new FakeInstallService();
        var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        var call = service.Calls[0];
        call.Progress!.Report(new AppInstallProgress(AppInstallPhase.Extracting, 65));
        vm.Dispose();
        vm.Dispose();
        Assert.True(call.Token.IsCancellationRequested);
        Assert.False(vm.CanCancel);
        Assert.False(vm.IsInstalling);
        var snapshot = (vm.StatusMessage, vm.ErrorMessage, vm.Percent, vm.TargetDirectory, vm.ExecutablePath);
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        call.Completion.SetResult(new AppInstallResult(true, TargetDirectory: "late", ExecutablePath: "late.exe"));
        context.Drain();
        await operation;
        vm.Text.ApplyLanguage("zh-CN");
        Assert.Equal(snapshot, (vm.StatusMessage, vm.ErrorMessage, vm.Percent, vm.TargetDirectory, vm.ExecutablePath));
        Assert.Equal(0, notifications);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.InstallAsync(Request()));
        Assert.Single(service.Calls);
    }

    [Fact]
    public async Task LanguageChange_PreservesActiveProgressAndRendersCurrentPhase()
    {
        var service = new FakeInstallService();
        using var vm = Create(service);
        var context = new QueueContext();
        var operation = context.Start(() => vm.InstallAsync(Request()));
        service.Calls[0].Progress!.Report(new AppInstallProgress(AppInstallPhase.Verifying, 75));
        context.Drain();
        vm.Text.ApplyLanguage("zh-CN");
        Assert.True(vm.IsInstalling);
        Assert.Equal(75d, vm.Percent);
        Assert.Equal(vm.Text["InstallVerifying"], vm.StatusMessage);
        Assert.Equal(service.Target, vm.TargetDirectory);
        service.Calls[0].Completion.SetResult(new AppInstallResult(true));
        context.Drain();
        await operation;
    }

    private static InstallationViewModel Create(FakeInstallService service)
        => new(service, new LocalizationService("en-US"));

    private static AppInstallRequest Request()
    {
        var app = new CatalogApp
        {
            Id = new AppId(new SourceInstanceId(Guid.Empty), new PackageId("fixture/tool")),
            SourceRevision = 1,
            DisplayName = "Fixture Tool",
            Owner = "fixture",
            Repo = "tool",
        };
        return new AppInstallRequest
        {
            App = app,
            Release = new Release { Key = "v1", AppId = app.Id, TagName = "v1" },
            Asset = new Asset
            {
                InstallerId = "fixture", ReleaseKey = "v1", Name = "tool.zip",
                DownloadUrl = "https://example.invalid/tool.zip", Kind = AssetKind.ArchiveZip,
            },
        };
    }

    private sealed class FakeInstallService : IAppInstallService
    {
        public string Target { get; } = "fixture-target";
        public CatalogApp? TargetApp { get; private set; }
        public List<InstallCall> Calls { get; } = [];
        public string GetTargetDirectory(CatalogApp app)
        {
            TargetApp = app;
            return Target;
        }

        public Task<AppInstallResult> InstallAsync(AppInstallRequest request,
            IProgress<AppInstallProgress>? progress = null,
            Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null,
            CancellationToken ct = default)
        {
            var call = new InstallCall(request, progress, chooseExecutable, ct);
            Calls.Add(call);
            return call.Completion.Task;
        }
    }

    private sealed record InstallCall(AppInstallRequest Request, IProgress<AppInstallProgress>? Progress,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? Chooser, CancellationToken Token)
    {
        public TaskCompletionSource<AppInstallResult> Completion { get; } = new();
    }

    /// <summary>Deterministically drains UI posts, including async continuations, without sleeps.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));

        public Task Start(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }

        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.TryDequeue(out var item)) item.Callback(item.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
