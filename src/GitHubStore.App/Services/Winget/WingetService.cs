using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubStore.App.Services.Processes;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Winget;

/// <summary>Winget-source queries and independent native installs. Caller cancellation always propagates.</summary>
public sealed class WingetService : IWingetService
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Static, offline recommendations; no version or availability was queried.</summary>
    private static readonly IReadOnlyList<WingetPackage> Featured = Array.AsReadOnly<WingetPackage>(
    [
        new() { Id = "Microsoft.PowerToys", Name = "PowerToys", Publisher = "Microsoft", License = "MIT", IsFeatured = true, IsStatic = true, Homepage = "https://github.com/microsoft/PowerToys" },
        new() { Id = "Microsoft.WindowsTerminal", Name = "Windows Terminal", Publisher = "Microsoft", License = "MIT", IsFeatured = true, IsStatic = true, Homepage = "https://github.com/microsoft/terminal" },
        new() { Id = "Notepad++.Notepad++", Name = "Notepad++", Publisher = "Notepad++ Team", IsFeatured = true, IsStatic = true },
        new() { Id = "7zip.7zip", Name = "7-Zip", Publisher = "Igor Pavlov", IsFeatured = true, IsStatic = true },
        new() { Id = "Git.Git", Name = "Git", Publisher = "The Git Development Community", IsFeatured = true, IsStatic = true },
        new() { Id = "voidtools.Everything", Name = "Everything", Publisher = "voidtools", IsFeatured = true, IsStatic = true },
        new() { Id = "ShareX.ShareX", Name = "ShareX", Publisher = "ShareX Team", IsFeatured = true, IsStatic = true },
        new() { Id = "FilesCommunity.Files", Name = "Files", Publisher = "Files Community", IsFeatured = true, IsStatic = true, Homepage = "https://github.com/files-community/Files" },
    ]);

    private readonly IProcessRunner _processRunner;
    private readonly string _executablePath;
    private readonly IToolManager? _tools;

    public WingetService(string? wingetPath = null) : this(new ProcessRunner(), wingetPath) { }

    public WingetService(IProcessRunner processRunner, string? wingetPath = null, IToolManager? tools = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _executablePath = string.IsNullOrWhiteSpace(wingetPath) ? "winget.exe" : wingetPath.Trim();
        _tools = tools;
    }

    public IReadOnlyList<WingetPackage> ListFeatured() => Featured;
    public string? LastProbeError { get; private set; }
    public string? LastProbeErrorCode { get; private set; }
    public int? LastProbeExitCode { get; private set; }
    public string? LastSearchError { get; private set; }

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LastProbeError = null;
        LastProbeErrorCode = null;
        LastProbeExitCode = null;
        ProcessResult result;
        try
        {
            result = await RunAsync(["--version"], ProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            LastProbeErrorCode = IsMissing(ex) ? WingetErrorCodes.NotInstalled : WingetErrorCodes.CommandFailed;
            LastProbeError = $"{LastProbeErrorCode}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        ct.ThrowIfCancellationRequested();
        LastProbeExitCode = result.ExitCode;
        LastProbeErrorCode = ErrorCode(result);
        if (LastProbeErrorCode is null && !WingetTableParser.CleanOutput(result.StandardOutput).Split('\n').Any(line =>
            Regex.IsMatch(line.Trim(), @"\Av?\d+(?:\.\d+)+(?:[-+][0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)))
            LastProbeErrorCode = WingetErrorCodes.InvalidOutput;
        if (LastProbeErrorCode is null) return true;
        LastProbeError = $"{LastProbeErrorCode}: {Describe("version-probe", result)}";
        return false;
    }

    public async Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        LastSearchError = null;
        if (string.IsNullOrWhiteSpace(query))
            return new WingetSearchResult { Succeeded = true };

        ProcessResult result;
        try
        {
            result = await RunAsync(
                ["search", "--query", query, "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
                SearchTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            LastSearchError = $"{ex.GetType().Name}: {ex.Message}";
            return new WingetSearchResult
            {
                ErrorCode = IsMissing(ex) ? WingetErrorCodes.NotInstalled : WingetErrorCodes.CommandFailed,
                Diagnostic = LastSearchError,
            };
        }
        ct.ThrowIfCancellationRequested();
        // Only a successful command can publish parsed rows as a successful query.
        var error = ErrorCode(result);
        IReadOnlyList<WingetPackage> items = [];
        if (error is null && !WingetTableParser.TryParse(result.StandardOutput, limit, out items))
            error = WingetErrorCodes.InvalidOutput;
        if (error is null)
            return new WingetSearchResult { Succeeded = true, Items = items, ExitCode = result.ExitCode };

        LastSearchError = $"{error}: {Describe("search", result)}";
        return new WingetSearchResult { ErrorCode = error, ExitCode = result.ExitCode, Diagnostic = LastSearchError };
    }

    public async Task<WingetInstallResult> InstallAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!WingetDetailsParser.IsSafeId(id)) return new() { ErrorCode = WingetErrorCodes.InvalidPackageId };
        if (!await InstallGate.WaitAsync(0, ct).ConfigureAwait(false)) return new() { ErrorCode = WingetErrorCodes.Busy };
        try
        {
            ProcessResult result;
            try
            {
                result = await RunAsync(
                    ["install", "--id", id, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--interactive", "--disable-interactivity"],
                    InstallTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                ct.ThrowIfCancellationRequested();
                return new()
                {
                    ErrorCode = IsMissing(ex) ? WingetErrorCodes.NotInstalled : WingetErrorCodes.CommandFailed,
                    Output = InstallOutput(ex.Message),
                };
            }
            ct.ThrowIfCancellationRequested();
            // Raw Windows installer success codes may request a restart. All other nonzero
            // winget statuses remain failures; do not infer success from localized console text.
            var succeeded = result.Outcome == ProcessOutcome.Exited && result.ExitCode is 0 or 3010 or 1641;
            return new()
            {
                Succeeded = succeeded,
                ErrorCode = succeeded ? null : ErrorCode(result) ?? WingetErrorCodes.CommandFailed,
                ExitCode = result.ExitCode,
                Output = InstallOutput(string.Join("\n", new[] { result.StandardOutput, result.StandardError, result.Diagnostic }.Where(value => !string.IsNullOrWhiteSpace(value)))),
                RebootRequired = succeeded && result.ExitCode is 3010 or 1641,
            };
        }
        finally { InstallGate.Release(); }
    }

    private static string? InstallOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var clean = WingetTableParser.CleanOutput(output).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= 8000 ? clean : clean[..8000];
    }

    public async Task<WingetDetailsResult> GetDetailsAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!WingetDetailsParser.IsSafeId(id))
            return new() { ErrorCode = WingetErrorCodes.InvalidPackageId };
        ProcessResult result;
        try
        {
            result = await RunAsync(
                ["show", "--id", id, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity", "--locale", "en-US"],
                SearchTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            return new() { ErrorCode = IsMissing(ex) ? WingetErrorCodes.NotInstalled : WingetErrorCodes.CommandFailed };
        }
        ct.ThrowIfCancellationRequested();
        var error = ErrorCode(result);
        if (error is not null) return new() { ErrorCode = error };
        return WingetDetailsParser.TryParse(result.StandardOutput, id, out var package)
            ? new() { Succeeded = true, Package = package }
            : new() { ErrorCode = WingetErrorCodes.InvalidOutput };
    }

    internal static IReadOnlyList<WingetPackage> ParseSearchOutput(string output, int limit) =>
        WingetTableParser.TryParse(output, limit, out var items) ? items : [];

    private static bool IsMissing(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException
        || exception is Win32Exception { NativeErrorCode: 2 or 3 };

    private static string? ErrorCode(ProcessResult result) => result.Outcome switch
    {
        ProcessOutcome.NotFound => WingetErrorCodes.NotInstalled,
        ProcessOutcome.TimedOut => WingetErrorCodes.TimedOut,
        ProcessOutcome.Exited when result.ExitCode == 0 => null,
        _ => WingetErrorCodes.CommandFailed,
    };

    private static string Describe(string operation, ProcessResult result) =>
        $"{operation} outcome={result.Outcome} exit={(result.ExitCode is { } code ? $"{code} (0x{code:X8})" : "<none>")} "
        + $"diagnostic={Truncate(result.Diagnostic)} stderr={Truncate(result.StandardError)} stdout={Truncate(result.StandardOutput)}";

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<empty>";
        var clean = WingetTableParser.CleanOutput(value).Trim();
        return clean.Length <= 300 ? clean : clean[..300];
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var executable = _executablePath;
        if (_tools is not null)
        {
            var resolved = await _tools.EnsureAsync(ManagedToolKind.Winget, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!resolved.IsAvailable || string.IsNullOrEmpty(resolved.ExecutablePath))
                return new ProcessResult
                {
                    Outcome = resolved.ErrorCode == ToolErrorCodes.SystemToolMissing
                        ? ProcessOutcome.NotFound : ProcessOutcome.FailedToStart,
                    Diagnostic = resolved.ErrorCode ?? ToolErrorCodes.SystemToolMissing,
                };
            executable = resolved.ExecutablePath;
        }
        var request = new ProcessRequest { FileName = executable, Arguments = arguments, Timeout = timeout };
        var result = await _processRunner.RunAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var tracePath = Environment.GetEnvironmentVariable("GITHUBSTORE_WINGET_TRACE");
        if (!string.IsNullOrWhiteSpace(tracePath))
        {
            try
            {
                var report = $"[{DateTimeOffset.Now:O}] executable={executable} args={JsonSerializer.Serialize(arguments)} "
                    + $"outcome={result.Outcome} exit={result.ExitCode} diagnostic={result.Diagnostic}{Environment.NewLine}"
                    + $"--- STDOUT ({result.StandardOutput.Length}) ---{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}"
                    + $"--- STDERR ({result.StandardError.Length}) ---{Environment.NewLine}{result.StandardError}{Environment.NewLine}";
                await File.AppendAllTextAsync(tracePath, report, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine(ex); }
        }
        return result;
    }
}
