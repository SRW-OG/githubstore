using System.ComponentModel;
using GitHubStore.App.Services.Winget;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class WingetDetailsTests
{
    [Fact]
    public async Task Chinese7zipLabels_PreservePublisherAndMultilineMetadataBeforeInstaller()
    {
        // Locale en-US selects manifest content; the CLI still uses Chinese labels.
        const string output = """
            已找到 7-Zip [7zip.7zip]
            版本: 25.01
            发布服务器: Igor Pavlov
            发布服务器 URL: https://7-zip.org/
            发布服务器支持 URL: https://7-zip.org/support.html
            描述:
              7-Zip is a file archiver with a high compression ratio.
              7-Zip is free software with open source.
            主页: https://7-zip.org/
            许可证: LGPL-2.1-or-later
            许可证 URL: https://7-zip.org/license.txt
            发行说明:
              The bug was fixed.
              Some improvements were made.
            发行说明 URL: https://7-zip.org/history.txt
            安装：
              安装程序类型: exe
            描述: must not overwrite package metadata
            发布服务器 URL: https://invalid.example/installer
            """;
        var result = await new WingetService(Returning(output)).GetDetailsAsync("7zip.7zip");
        Assert.True(result.Succeeded);
        Assert.Equal("Igor Pavlov", result.Package!.Publisher);
        Assert.Equal("https://7-zip.org/", result.Package.PublisherUrl);
        Assert.Equal("7-Zip is a file archiver with a high compression ratio.\n7-Zip is free software with open source.", result.Package.Description);
        Assert.Equal("LGPL-2.1-or-later", result.Package.License);
        Assert.Equal("https://7-zip.org/license.txt", result.Package.LicenseUrl);
        Assert.Equal("The bug was fixed.\nSome improvements were made.", result.Package.ReleaseNotes);
        Assert.Equal("https://7-zip.org/history.txt", result.Package.ReleaseNotesUrl);
    }

    private const string Output = """
        Found Example Tool [Vendor.Tool]
        Version: 1.2.3
        Publisher: Example publisher
        Publisher Url: https://example.org/publisher
        Description: First paragraph: preserved.
          Second line with https://example.org/path

          Third paragraph.
        Homepage: https://example.org
        License: MIT
        License Url: https://example.org/license
        Release Notes:
          Fix: one
          Fix two
        Release Notes Url: https://example.org/releases
        Installer:
          Installer Type: exe
          Installer Url: https://example.org/installer.exe
          Description: installer metadata
        """;

    [Fact]
    public async Task Details_QueryIsExactScopedBoundedAndUsesSeparateArguments()
    {
        var runner = Returning(Output);
        var result = await new WingetService(runner).GetDetailsAsync("Vendor.Tool");
        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(new[] { "show", "--id", "Vendor.Tool", "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity", "--locale", "en-US" }, request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
        Assert.Equal("winget.exe", request.FileName);
        var package = result.Package!;
        Assert.Equal("Example Tool", package.Name);
        Assert.Equal("1.2.3", package.Version);
        Assert.Equal("Example publisher", package.Publisher);
        Assert.Equal("https://example.org/publisher", package.PublisherUrl);
        Assert.Equal("First paragraph: preserved.\nSecond line with https://example.org/path\n\nThird paragraph.", package.Description);
        Assert.Equal("https://example.org", package.Homepage);
        Assert.Equal("MIT", package.License);
        Assert.Equal("https://example.org/license", package.LicenseUrl);
        Assert.Equal("Fix: one\nFix two", package.ReleaseNotes);
        Assert.Equal("https://example.org/releases", package.ReleaseNotesUrl);
        Assert.False(package.IsStatic);
    }

    [Fact]
    public async Task ChineseLabelsAndAnsiOutput_AreAccepted()
    {
        var output = "\u001b[32m已找到 示例工具 [vendor.tool]\u001b[0m\r\n版本： 2.0\n发布者: 示例\n发布者 URL: https://example.org\n描述:\n  第一行\n  第二行\n主页: https://example.org/home\n许可证: MIT\n许可证 URL: https://example.org/license\n发行说明: 修复\n发行说明 URL: https://example.org/releases\n安装程序:\n  版本: wrong";
        var result = await new WingetService(Returning(output)).GetDetailsAsync("Vendor.Tool");
        Assert.True(result.Succeeded);
        Assert.Equal("第一行\n第二行", result.Package!.Description);
        Assert.Equal("2.0", result.Package.Version);
        Assert.Equal("示例", result.Package.Publisher);
        Assert.Equal("修复", result.Package.ReleaseNotes);
        Assert.Equal("https://example.org/releases", result.Package.ReleaseNotesUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("Vendor.Tool --version")]
    [InlineData("Vendor.Tool\nOther.Id")]
    [InlineData("Vendor.Tool&cmd")]
    [InlineData(" Vendor.Tool")]
    public async Task UnsafeIds_AreRejectedWithoutRunning(string id)
    {
        var runner = Returning(Output);
        var result = await new WingetService(runner).GetDetailsAsync(id);
        Assert.False(result.Succeeded);
        Assert.Equal(WingetErrorCodes.InvalidPackageId, result.ErrorCode);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("Found Other Tool [Other.Tool]\nVersion: 1")]
    [InlineData("Version: 1\nPublisher: Wrong")]
    [InlineData("No package found matching input criteria.")]
    [InlineData("Found Tool [Vendor.Tool]\nFound Another [Other.Tool]")]
    public async Task MissingOrMismatchingHeader_CannotPublishMetadata(string output)
    {
        var result = await new WingetService(Returning(output)).GetDetailsAsync("Vendor.Tool");
        Assert.False(result.Succeeded);
        Assert.Null(result.Package);
        Assert.Equal(WingetErrorCodes.InvalidOutput, result.ErrorCode);
    }

    [Fact]
    public async Task HeaderOnly_DoesNotInventMissingDetails()
    {
        var result = await new WingetService(Returning("Found Tool [Vendor.Tool]")).GetDetailsAsync("Vendor.Tool");
        Assert.True(result.Succeeded);
        Assert.Null(result.Package!.Version);
        Assert.Null(result.Package.Description);
        Assert.Null(result.Package.Publisher);
        Assert.Null(result.Package.Homepage);
        Assert.Null(result.Package.License);
        Assert.Null(result.Package.ReleaseNotes);
    }

    [Theory]
    [InlineData(ProcessOutcome.TimedOut, null, WingetErrorCodes.TimedOut)]
    [InlineData(ProcessOutcome.NotFound, null, WingetErrorCodes.NotInstalled)]
    [InlineData(ProcessOutcome.FailedToStart, null, WingetErrorCodes.CommandFailed)]
    [InlineData(ProcessOutcome.Exited, 1, WingetErrorCodes.CommandFailed)]
    public async Task ProcessFailure_CannotPublishEvenValidDetails(ProcessOutcome outcome, int? exit, string code)
    {
        var runner = new Runner((_, _) => Task.FromResult(new ProcessResult { Outcome = outcome, ExitCode = exit, StandardOutput = Output }));
        var result = await new WingetService(runner).GetDetailsAsync("Vendor.Tool");
        Assert.False(result.Succeeded);
        Assert.Null(result.Package);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData(2, WingetErrorCodes.NotInstalled)]
    [InlineData(5, WingetErrorCodes.CommandFailed)]
    public async Task NativeErrors_AreReported(int native, string code)
    {
        var result = await new WingetService(new Runner((_, _) => throw new Win32Exception(native))).GetDetailsAsync("Vendor.Tool");
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task Cancellation_PropagatesBeforeAndDuringQuery()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var runner = Returning(Output);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WingetService(runner).GetDetailsAsync("Vendor.Tool", canceled.Token));
        Assert.Empty(runner.Requests);
        using var cancellation = new CancellationTokenSource();
        runner = new Runner((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult(new ProcessResult { Outcome = ProcessOutcome.TimedOut });
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WingetService(runner).GetDetailsAsync("Vendor.Tool", cancellation.Token));
    }

    [Fact]
    public async Task LegacyService_DefaultDetailsMethodFailsHonestlyAndHonorsCancellation()
    {
        IWingetService service = new LegacyService();
        var result = await service.GetDetailsAsync("Vendor.Tool");
        Assert.False(result.Succeeded);
        Assert.Null(result.Package);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDetailsAsync("Vendor.Tool", cancellation.Token));
    }

    private sealed class LegacyService : IWingetService
    {
        public string? LastProbeError => null;
        public Task<bool> ProbeAsync(CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyList<WingetPackage> ListFeatured() => [];
        public Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default) => Task.FromResult(new WingetSearchResult());
    }

    private static Runner Returning(string output) => new((_, _) => Task.FromResult(new ProcessResult { Outcome = ProcessOutcome.Exited, ExitCode = 0, StandardOutput = output }));
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
