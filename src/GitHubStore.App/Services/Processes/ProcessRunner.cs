using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Processes;

/// <summary>Direct execution with explicit arguments, bounded lifetime and owned output readers.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        if (request.Timeout <= TimeSpan.Zero || request.Timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(request), "A finite positive process timeout is required.");
        ct.ThrowIfCancellationRequested();

        var executable = ResolveExecutable(request.FileName);
        ct.ThrowIfCancellationRequested();
        if (executable is null)
            return new ProcessResult { Outcome = ProcessOutcome.NotFound, Diagnostic = $"Executable not found: {request.FileName}" };

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start())
                return new ProcessResult { Outcome = ProcessOutcome.FailedToStart, Diagnostic = "Process.Start returned false." };
        }
        catch (Win32Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            return new ProcessResult
            {
                Outcome = ex.NativeErrorCode is 2 or 3 ? ProcessOutcome.NotFound : ProcessOutcome.FailedToStart,
                Diagnostic = $"{ex.GetType().Name} ({ex.NativeErrorCode}): {ex.Message}",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            return new ProcessResult { Outcome = ProcessOutcome.FailedToStart, Diagnostic = $"{ex.GetType().Name}: {ex.Message}" };
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(request.Timeout);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readOut = CaptureAsync(process.StandardOutput, stdout, lifetime.Token);
        var readErr = CaptureAsync(process.StandardError, stderr, lifetime.Token);
        try
        {
            // The deadline covers both process exit and pipe draining (including inherited handles).
            await Task.WhenAll(process.WaitForExitAsync(lifetime.Token), readOut, readErr).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ProcessResult
            {
                Outcome = ProcessOutcome.Exited,
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
            };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            var cleanupError = await StopAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(readOut, readErr).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ProcessResult
            {
                Outcome = ProcessOutcome.TimedOut,
                ExitCode = process.HasExited ? process.ExitCode : null,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                Diagnostic = cleanupError is null ? $"Process exceeded {request.Timeout}." : $"Process exceeded {request.Timeout}; {cleanupError}",
            };
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await StopAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(readOut, readErr).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, CancellationToken ct)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            output.Append(buffer, 0, count);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "两个读取任务由本次 RunAsync 创建；这里只在 ConfigureAwait(false) 后观察其完成状态。")]
    private static async Task ObserveReadersAsync(Task stdout, Task stderr)
    {
        try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Trace.WriteLine(ex);
        }
    }

    private static async Task<string?> StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // An exit racing Kill is harmless. A genuine kill failure must remain visible.
            if (!process.HasExited)
                return $"Could not terminate process tree: {ex.Message}";
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return null;
    }

    private static string? ResolveExecutable(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
            return File.Exists(fileName) ? Path.GetFullPath(fileName) : null;
        // Never implicitly search the current directory; PATH entries must be absolute.
        if (Path.IsPathRooted(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return null;
        if (string.Equals(fileName, "winget.exe", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Path.IsPathFullyQualified(localAppData))
            {
                var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                if (File.Exists(alias)) return alias;
            }
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
