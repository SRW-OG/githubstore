using System.ComponentModel;
using GitHubStore.App.Services.Winget;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

// Static install gate tests must not overlap each other in separate xUnit classes.
public sealed class WingetInstallTests
{
    [Theory]
    [InlineData("7zip.7zip")]
    [InlineData("Notepad++.Notepad++")]
    public async Task Install_UsesNativeExactScopedCommandWithoutManagedLocation(string id)
    {
        var runner = Returning(ProcessOutcome.Exited, 0);
        var result = await new WingetService(runner, @"C:\Tools\winget.exe").InstallAsync(id);
        Assert.True(result.Succeeded);
        Assert.False(result.RebootRequired);
        Assert.Null(result.ErrorCode);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(@"C:\Tools\winget.exe", request.FileName);
        Assert.Equal(new[] { "install", "--id", id, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--interactive", "--disable-interactivity" }, request.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(30), request.Timeout);
        Assert.DoesNotContain("--location", request.Arguments);
        Assert.DoesNotContain("--scope", request.Arguments);
        Assert.DoesNotContain("--force", request.Arguments);
        Assert.DoesNotContain("--silent", request.Arguments);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public async Task RestartSuccess_IsReportedExplicitly(int exit)
    {
        var result = await new WingetService(Returning(ProcessOutcome.Exited, exit)).InstallAsync("Vendor.Tool");
        Assert.True(result.Succeeded);
        Assert.True(result.RebootRequired);
        Assert.Equal(exit, result.ExitCode);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData(ProcessOutcome.Exited, 1, WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.Exited, -1978334975, WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.Exited, null, WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.NotFound, null, WingetErrorCodes.NotInstalled)]
    [InlineData(ProcessOutcome.TimedOut, 0, WingetErrorCodes.TimedOut)]
    [InlineData(ProcessOutcome.TimedOut, 3010, WingetErrorCodes.TimedOut)]
    [InlineData(ProcessOutcome.FailedToStart, null, WingetErrorCodes.CommandFailed)]
    public async Task Failures_DoNotInferSuccessFromOutputOrExitAlone(ProcessOutcome outcome, int? exit, string error)
    {
        var result = await new WingetService(Returning(outcome, exit, "Successfully installed")).InstallAsync("Vendor.Tool");
        Assert.False(result.Succeeded);
        Assert.False(result.RebootRequired);
        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(error, result.ErrorCode);
        Assert.Contains("Successfully installed", result.Output!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("Vendor.Tool --silent")]
    [InlineData("Vendor.Tool&cmd")]
    [InlineData("Vendor.Tool\nOther.Tool")]
    [InlineData(" Vendor.Tool")]
    public async Task InvalidIds_DoNotInvokeRunner(string id)
    {
        var runner = Returning(ProcessOutcome.Exited, 0);
        var result = await new WingetService(runner).InstallAsync(id);
        Assert.Equal(WingetErrorCodes.InvalidPackageId, result.ErrorCode);
        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Output_IsAnsiCleanedAndBoundedIncludingStderrAndDiagnostic()
    {
        var runner = new Runner((_, _) => Task.FromResult(new ProcessResult
        {
            Outcome = ProcessOutcome.Exited, ExitCode = 1,
            StandardOutput = "\u001b[31mfailed\u001b[0m\r\n", StandardError = "stderr", Diagnostic = "diagnostic",
        }));
        var result = await new WingetService(runner).InstallAsync("Vendor.Tool");
        Assert.Contains("failed", result.Output!);
        Assert.Contains("stderr", result.Output!);
        Assert.Contains("diagnostic", result.Output!);
        Assert.DoesNotContain("\u001b", result.Output!, StringComparison.Ordinal);
        result = await new WingetService(Returning(ProcessOutcome.Exited, 1, new string('x', 9000))).InstallAsync("Vendor.Tool");
        Assert.Equal(8000, result.Output!.Length);
    }

    [Fact]
    public async Task ConcurrentInstall_IsRejectedAcrossServiceInstancesWithoutQueueing()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner((_, _) => { entered.SetResult(true); return release.Task; });
        var first = new WingetService(runner).InstallAsync("Vendor.Tool");
        await entered.Task;
        try
        {
            var secondRunner = Returning(ProcessOutcome.Exited, 0);
            var second = await new WingetService(secondRunner).InstallAsync("Other.Tool");
            Assert.False(second.Succeeded);
            Assert.Equal(WingetErrorCodes.Busy, second.ErrorCode);
            Assert.Empty(secondRunner.Requests);
        }
        finally { release.TrySetResult(new ProcessResult { Outcome = ProcessOutcome.Exited, ExitCode = 0 }); await first; }
        Assert.True((await new WingetService(Returning(ProcessOutcome.Exited, 0)).InstallAsync("Other.Tool")).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_PropagatesAndReleasesGate(bool returnTimeout)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner((_, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            cancellation.Cancel();
            return returnTimeout ? Task.FromResult(new ProcessResult { Outcome = ProcessOutcome.TimedOut }) : Task.FromCanceled<ProcessResult>(ct);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WingetService(runner).InstallAsync("Vendor.Tool", cancellation.Token));
        Assert.True((await new WingetService(Returning(ProcessOutcome.Exited, 0)).InstallAsync("Other.Tool")).Succeeded);
        var preRunner = Returning(ProcessOutcome.Exited, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WingetService(preRunner).InstallAsync("Vendor.Tool", cancellation.Token));
        Assert.Empty(preRunner.Requests);
    }

    [Theory]
    [InlineData(2, WingetErrorCodes.NotInstalled)]
    [InlineData(5, WingetErrorCodes.CommandFailed)]
    public async Task LaunchException_IsReportedAndReleasesGate(int nativeCode, string error)
    {
        var result = await new WingetService(new Runner((_, _) => throw new Win32Exception(nativeCode))).InstallAsync("Vendor.Tool");
        Assert.Equal(error, result.ErrorCode);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Output);
        Assert.True((await new WingetService(Returning(ProcessOutcome.Exited, 0)).InstallAsync("Other.Tool")).Succeeded);
    }

    [Fact]
    public async Task LegacyService_DefaultMethodFailsAndHonorsCancellation()
    {
        IWingetService service = new Legacy();
        Assert.False((await service.InstallAsync("Vendor.Tool")).Succeeded);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync("Vendor.Tool", cancellation.Token));
    }

    private sealed class Legacy : IWingetService
    {
        public string? LastProbeError => null;
        public Task<bool> ProbeAsync(CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyList<WingetPackage> ListFeatured() => [];
        public Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default) => Task.FromResult(new WingetSearchResult());
    }
    private static Runner Returning(ProcessOutcome outcome, int? exit, string stdout = "") => new((_, _) => Task.FromResult(new ProcessResult { Outcome = outcome, ExitCode = exit, StandardOutput = stdout }));
    private sealed class Runner(Func<ProcessRequest, CancellationToken, Task<ProcessResult>> run) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return run(request, ct);
        }
    }
}
