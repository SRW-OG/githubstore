using System.ComponentModel;
using GitHubStore.App.Services.Winget;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>Offline regressions: every process boundary is a fake; no CLI is launched.</summary>
public sealed class WingetRegressionTests
{
    // Verbatim rows from artifacts/winget-search-raw.txt (including the single-space
    // boundaries before GOODJINC and before version 1.2.0.0). Keep this in source:
    // artifacts are local evidence and are not guaranteed to exist on CI.
    private const string CapturedSearchOutput = """
        名称                                            ID                                          版本         匹配                     源
        -----------------------------------------------------------------------------------------------------------------------------------------
        PowerToys                                       Microsoft.PowerToys                         0.102.2862.0                          winget
        Markets Extension for Command Palette           CostaFotiadis.MarketsExtensionForCmdPalette 1.2.0.0      Tag: powertoys           winget
        Browser Bookmarks for PowerToys Command Palette GOODJINC.CmdPalBrowserBookmarks             0.2.3        Tag: powertoys           winget
        Microsoft PowerToys                             XP89DCGQ3K6VLD                              Unknown                               msstore
        Unity Hub Extension for PowerToys               9N85WPWSJK4N                                Unknown                               msstore
        Visual Studio / Code for Command Palette        15722UsefulApp.WorkspaceLauncherForVSCode   1.30.0.0     Tag: microsoft-powertoys winget
        """;

    [Theory]
    [InlineData("PowerToys")]
    [InlineData("  name with spaces  ")]
    [InlineData("name \"quoted\" & value %PATH% | other > output")]
    [InlineData("%COMSPEC% /c & \" %TEMP% ")]
    public async Task Search_PreservesEntireQueryAsOneArgument(string query)
    {
        var runner = Returning(Exited(CapturedSearchOutput));
        var service = new WingetService(runner);

        var result = await service.SearchAsync(query);

        Assert.True(result.Succeeded, result.Diagnostic);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("winget.exe", request.FileName);
        Assert.Equal(new[] { "search", "--query", query, "--source", "winget", "--accept-source-agreements", "--disable-interactivity" }, request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public async Task ExplicitExecutablePath_IsPassedSeparatelyFromQuery()
    {
        const string executable = @"C:\Program Files\App Installer\winget.exe";
        var runner = Returning(Exited(CapturedSearchOutput));
        var service = new WingetService(runner, executable);

        Assert.True((await service.SearchAsync("query & %PATH%")).Succeeded);
        Assert.Equal(executable, Assert.Single(runner.Requests).FileName);
    }

    [Fact]
    public async Task CapturedTable_ParsesSingleSpaceBoundariesAndExcludesStore()
    {
        var result = await new WingetService(Returning(Exited(CapturedSearchOutput))).SearchAsync("powertoys");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "Microsoft.PowerToys", "CostaFotiadis.MarketsExtensionForCmdPalette", "GOODJINC.CmdPalBrowserBookmarks", "15722UsefulApp.WorkspaceLauncherForVSCode" },
            result.Items.Select(item => item.Id));
        Assert.Contains(result.Items, item => item.Name == "Browser Bookmarks for PowerToys Command Palette" && item.Version == "0.2.3");
        Assert.Contains(result.Items, item => item.Id == "CostaFotiadis.MarketsExtensionForCmdPalette" && item.Version == "1.2.0.0");
        Assert.All(result.Items, item => Assert.False(item.IsStatic));
    }

    [Fact]
    public async Task ChineseNames_UseTerminalCellWidthsRatherThanUtf16Offsets()
    {
        const string output = "名称                ID                  版本   源\n"
            + "-----------------------------------------------------\n"
            + "中文工具            Vendor.Tool         1.2    winget\n"
            + "工具 e\u0301              Vendor.Other        2.0    winget\n";
        var result = await new WingetService(Returning(Exited(output))).SearchAsync("工具");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(new[] { "Vendor.Tool", "Vendor.Other" }, result.Items.Select(item => item.Id));
        Assert.Equal(new[] { "中文工具", "工具 e\u0301" }, result.Items.Select(item => item.Name));
        Assert.Equal(new[] { "1.2", "2.0" }, result.Items.Select(item => item.Version));
    }

    [Fact]
    public async Task AnsiColorsAndCarriageReturnProgress_DoNotShiftColumns()
    {
        var decorated = "\u001b[?25l\r   -\r   \\\r\u001b[2K\u001b[0G\u001b]0;winget\u0007"
            + "\u001b[32m" + CapturedSearchOutput.Replace("Microsoft.PowerToys", "\u001b[1mMicrosoft.PowerToys\u001b[22m", StringComparison.Ordinal)
            + "\u001b[0m\u001b[?25h";
        var result = await new WingetService(Returning(Exited(decorated))).SearchAsync("powertoys");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(4, result.Items.Count);
        Assert.Equal("Microsoft.PowerToys", result.Items[0].Id);
    }

    [Fact]
    public async Task EnglishTableWithoutSourceColumn_IsValidForScopedSearch()
    {
        const string output = "Name                Id                  Version\n"
            + "-------------------------------------------------\n"
            + "Example Tool        Vendor.Tool         1.2\n";
        var result = await new WingetService(Returning(Exited(output))).SearchAsync("example");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("Vendor.Tool", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Limit_AppliesAfterParsingValidRows()
    {
        var result = await new WingetService(Returning(Exited(CapturedSearchOutput))).SearchAsync("powertoys", limit: 2);
        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(new[] { "Microsoft.PowerToys", "CostaFotiadis.MarketsExtensionForCmdPalette" }, result.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0x8A150001))]
    public async Task NonzeroExit_WithValidTable_RemainsFailure(int exitCode)
    {
        var service = new WingetService(Returning(Exited(CapturedSearchOutput, exitCode, "source failed")));
        var result = await service.SearchAsync("powertoys");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Items);
        Assert.Equal(WingetErrorCodes.CommandFailed, result.ErrorCode);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains($"0x{exitCode:X8}", result.Diagnostic!);
        Assert.Contains("source failed", result.Diagnostic!);
        Assert.Equal(result.Diagnostic, service.LastSearchError);
    }

    [Theory]
    [InlineData(ProcessOutcome.NotFound, WingetErrorCodes.NotInstalled)]
    [InlineData(ProcessOutcome.FailedToStart, WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.TimedOut, WingetErrorCodes.TimedOut)]
    public async Task RunnerFailure_UsesExplicitErrorCategoryAndCannotPublishTable(ProcessOutcome outcome, string expectedError)
    {
        var runner = Returning(new ProcessResult { Outcome = outcome, StandardOutput = CapturedSearchOutput, Diagnostic = "boundary diagnostic" });
        var result = await new WingetService(runner).SearchAsync("powertoys");

        Assert.False(result.Succeeded);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Empty(result.Items);
        Assert.Contains("boundary diagnostic", result.Diagnostic!);
    }

    [Theory]
    [InlineData(2, WingetErrorCodes.NotInstalled)]
    [InlineData(5, WingetErrorCodes.CommandFailed)]
    public async Task NativeLaunchErrors_DistinguishMissingFromAccessDenied(int nativeCode, string errorCode)
    {
        var runner = new RecordingRunner((_, _) => throw new Win32Exception(nativeCode));
        var result = await new WingetService(runner).SearchAsync("query");

        Assert.False(result.Succeeded);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.NotNull(result.Diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("winget help text, not search results")]
    [InlineData("Name                Id                  Version\n-------------------------------------------------\nExample Tool        invalid-id          1.2")]
    public async Task SuccessfulExit_WithMissingOrMalformedTable_IsInvalidOutput(string output)
    {
        var result = await new WingetService(Returning(Exited(output))).SearchAsync("query");

        Assert.False(result.Succeeded);
        Assert.Equal(WingetErrorCodes.InvalidOutput, result.ErrorCode);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("Name                Id                  Version\n-------------------------------------------------")]
    [InlineData("No package found matching input criteria.")]
    [InlineData("找不到与输入条件匹配的程序包。")]
    public async Task RecognizedEmptyResult_IsSuccessfulOnlyWithZeroExit(string output)
    {
        var result = await new WingetService(Returning(Exited(output))).SearchAsync("missing");
        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchSuccess_ClearsPreviousDiagnostic()
    {
        var calls = 0;
        var service = new WingetService(new RecordingRunner((_, _) => Task.FromResult(++calls == 1
            ? Exited("", 1, "failed") : Exited(CapturedSearchOutput))));
        Assert.False((await service.SearchAsync("first")).Succeeded);
        Assert.NotNull(service.LastSearchError);
        Assert.True((await service.SearchAsync("second")).Succeeded);
        Assert.Null(service.LastSearchError);
    }

    [Fact]
    public async Task Probe_RequiresSuccessfulVersionExecution_AndUsesSeparateArgument()
    {
        var runner = Returning(Exited("v1.29.380\r\n"));
        var service = new WingetService(runner);

        Assert.True(await service.ProbeAsync());
        Assert.Null(service.LastProbeError);
        Assert.Null(service.LastProbeErrorCode);
        Assert.Equal(0, service.LastProbeExitCode);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(new[] { "--version" }, request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
    }

    [Theory]
    [InlineData(ProcessOutcome.NotFound, null, "", WingetErrorCodes.NotInstalled)]
    [InlineData(ProcessOutcome.FailedToStart, null, "", WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.TimedOut, null, "v1.29.380", WingetErrorCodes.TimedOut)]
    [InlineData(ProcessOutcome.Exited, 1, "v1.29.380", WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.Exited, 0, "", WingetErrorCodes.InvalidOutput)]
    [InlineData(ProcessOutcome.Exited, 0, "unrelated output", WingetErrorCodes.InvalidOutput)]
    public async Task ProbeFailure_ReportsHonestCategoryAndExitCode(ProcessOutcome outcome, int? exitCode, string stdout, string expectedError)
    {
        var service = new WingetService(Returning(new ProcessResult
        {
            Outcome = outcome, ExitCode = exitCode, StandardOutput = stdout, StandardError = "diagnostic detail",
        }));

        Assert.False(await service.ProbeAsync());
        Assert.Equal(expectedError, service.LastProbeErrorCode);
        Assert.Equal(exitCode, service.LastProbeExitCode);
        Assert.Contains(expectedError, service.LastProbeError!);
        Assert.Contains("diagnostic detail", service.LastProbeError!);
    }

    [Fact]
    public async Task ProbeSuccess_ClearsPreviousDiagnostic()
    {
        var calls = 0;
        var service = new WingetService(new RecordingRunner((_, _) => Task.FromResult(++calls == 1
            ? Exited("", unchecked((int)0x8A150001)) : Exited("v1.29.380"))));
        Assert.False(await service.ProbeAsync());
        Assert.Contains("0x8A150001", service.LastProbeError!);
        Assert.True(await service.ProbeAsync());
        Assert.Null(service.LastProbeError);
        Assert.Null(service.LastProbeErrorCode);
    }

    [Fact]
    public async Task PreCancellation_PropagatesWithoutInvokingRunner()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = Returning(Exited(CapturedSearchOutput));
        var service = new WingetService(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("", ct: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProbeAsync(cancellation.Token));
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringExecution_PropagatesAndPassesCallerToken(bool probe)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new RecordingRunner((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromCanceled<ProcessResult>(token);
        });
        var service = new WingetService(runner);

        if (probe)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProbeAsync(cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("query", ct: cancellation.Token));
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_TakesPriorityOverReturnedTimeout(bool probe)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new RecordingRunner((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ProcessResult { Outcome = ProcessOutcome.TimedOut });
        });
        var service = new WingetService(runner);

        if (probe)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProbeAsync(cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("query", ct: cancellation.Token));
    }

    [Fact]
    public async Task Featured_AreEightStaticEntries_AndEmptyQueryDoesNotRunProcess()
    {
        var runner = Returning(Exited(CapturedSearchOutput));
        var service = new WingetService(runner);
        var featured = service.ListFeatured();

        Assert.Equal(8, featured.Count);
        Assert.All(featured, item => { Assert.True(item.IsFeatured); Assert.True(item.IsStatic); Assert.Null(item.Version); });
        Assert.Equal(8, featured.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True((await service.SearchAsync("  \t")).Succeeded);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveLimit_IsRejectedBeforeStartingProcess(int limit)
    {
        var runner = Returning(Exited(CapturedSearchOutput));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new WingetService(runner).SearchAsync("query", limit));
        Assert.Empty(runner.Requests);
    }

    private static ProcessResult Exited(string stdout, int exitCode = 0, string stderr = "") =>
        new() { Outcome = ProcessOutcome.Exited, ExitCode = exitCode, StandardOutput = stdout, StandardError = stderr };

    private static RecordingRunner Returning(ProcessResult result) => new((_, _) => Task.FromResult(result));

    private sealed class RecordingRunner(Func<ProcessRequest, CancellationToken, Task<ProcessResult>> run) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return run(request, ct);
        }
    }
}
