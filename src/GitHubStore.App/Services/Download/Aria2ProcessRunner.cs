using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>可选 aria2 引擎；拥有进程、输出任务及隔离暂存产物的完整生命周期。</summary>
public sealed class Aria2ProcessRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private readonly string? _aria2cPath;

    public Aria2ProcessRunner(string? aria2cPath) => _aria2cPath = string.IsNullOrWhiteSpace(aria2cPath) ? null : aria2cPath.Trim();
    public string ExecutablePath => _aria2cPath ?? "aria2c";

    /// <summary>未配置/不存在/超时为 false；调用方取消在杀树并收束后传播。</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!HasExecutable) return false;
        try
        {
            var startInfo = CreateStartInfo();
            startInfo.ArgumentList.Add("--no-conf=true");
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var drain = DrainAsync(process, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await drain.ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                await KillAndWaitAsync(process, drain).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private bool HasExecutable => _aria2cPath is not null && Path.IsPathFullyQualified(_aria2cPath) && File.Exists(_aria2cPath);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, ProxySettings? proxy,
        bool isTorrent, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        DownloadResult Failure(string code) => DownloadResult.Failure(code, request.Source, DownloadEngine.Aria2);
        if (ct.IsCancellationRequested) return Failure(DownloadErrorCodes.Cancelled);
        if (isTorrent || !string.IsNullOrWhiteSpace(request.PeerSource)) return Failure(DownloadErrorCodes.PeerNotSupported);
        var validation = DownloadFileRules.Validate(request, out var targetPath);
        if (validation is not null) return Failure(validation);
        if (_aria2cPath is null) return Failure(DownloadErrorCodes.Aria2NotConfigured);
        if (!HasExecutable) return Failure(DownloadErrorCodes.Aria2NotFound);

        // 每次全新独立目录，旧 target/控制文件/HTTP 断点绝不充当本次下载的成功证据。
        var stagingDirectory = Path.Combine(Path.GetDirectoryName(targetPath)!, ".githubstore-download-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(stagingDirectory, "payload.part");
        var arguments = new List<string>
        {
            "--no-conf=true", $"--dir={stagingDirectory}", "--out=payload.part",
            "--continue=false", "--max-connection-per-server=8", "--split=8",
            "--file-allocation=none", "--summary-interval=1", "--console-log-level=warn",
            "--auto-file-renaming=false", "--allow-overwrite=false", "--max-tries=1",
            "--follow-torrent=false", "--follow-metalink=false", "--enable-rpc=false",
        };
        if (proxy?.ToProxyUri() is { } proxyUri) arguments.Add($"--all-proxy={proxyUri}");
        arguments.Add(request.Source.AbsoluteUri);
        var startInfo = CreateStartInfo();
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stagingDirectory);
            using var process = Process.Start(startInfo);
            if (process is null) return Failure(DownloadErrorCodes.Aria2NotFound);
            var drain = DrainAsync(process, ct, chunk =>
            {
                var (done, total) = ParseProgress(chunk);
                if (total > 0) progress?.Report(new DownloadProgress { Source = request.Source, Done = done, Total = total });
            });
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                await drain.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await KillAndWaitAsync(process, drain).ConfigureAwait(false);
                return Failure(DownloadErrorCodes.Cancelled);
            }
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(staging) || File.Exists(staging + ".aria2"))
                return Failure(DownloadErrorCodes.Aria2Failed);
            var verification = await DownloadFileRules.VerifyAsync(staging, request, request.ExpectedSize, ct).ConfigureAwait(false);
            if (verification is not null) return Failure(verification);
            var length = new FileInfo(staging).Length;
            ct.ThrowIfCancellationRequested();
            DownloadFileRules.Publish(staging, targetPath);
            progress?.Report(new DownloadProgress { Source = request.Source, Done = length, Total = length, Message = "completed" });
            return DownloadResult.Success(request.Source, targetPath, length, DownloadEngine.Aria2,
                SanitizeCommandLine(ExecutablePath, arguments));
        }
        catch (OperationCanceledException) { return Failure(DownloadErrorCodes.Cancelled); }
        catch (System.ComponentModel.Win32Exception) { return Failure(DownloadErrorCodes.Aria2NotFound); }
        catch (UnauthorizedAccessException) { return Failure(DownloadErrorCodes.TargetNotWritable); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return Failure(DownloadErrorCodes.Aria2Failed); }
        finally
        {
            // 只清理由本次 Guid 创建的精确产物；不递归删除，也不触及已有 target/HTTP .part。
            DownloadFileRules.TryDelete(staging);
            DownloadFileRules.TryDelete(staging + ".aria2");
            try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private ProcessStartInfo CreateStartInfo() => new()
    {
        FileName = ExecutablePath, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };

    // 不积累或记录原始输出（其中可能含 URI token/代理凭据）。
    private static async Task DrainAsync(Process process, CancellationToken ct, Action<string>? onChunk = null)
    {
        async Task DrainReaderAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                    onChunk?.Invoke(new string(buffer, 0, read));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
        }
        await Task.WhenAll(DrainReaderAsync(process.StandardOutput), DrainReaderAsync(process.StandardError)).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "drain 由当前下载/探测操作创建，在后台 ConfigureAwait(false) 收束；不是跨 UI 上下文任务。")]
    private static async Task KillAndWaitAsync(Process process, Task drain)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        await drain.ConfigureAwait(false);
    }

    internal static string SanitizeCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        static string Sanitize(string argument)
        {
            if (argument.StartsWith("--all-proxy=", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument["--all-proxy=".Length..];
                return Uri.TryCreate(value, UriKind.Absolute, out var proxy)
                    ? $"--all-proxy={proxy.Scheme}://{proxy.IdnHost}:{proxy.Port}" : "--all-proxy=<redacted>";
            }
            if (argument.StartsWith("--all-proxy-user=", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--all-proxy-passwd=", StringComparison.OrdinalIgnoreCase))
                return argument[..argument.IndexOf('=')] + "=<redacted>";
            if (Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
            return argument;
        }
        return $"{executable} {string.Join(' ', arguments.Select(Sanitize))}";
    }

    internal static (long Done, long Total) ParseProgress(string line)
    {
        var sizeIndex = line.IndexOf("SIZE:", StringComparison.Ordinal);
        if (sizeIndex < 0) return (0, 0);
        var payload = line[(sizeIndex + 5)..];
        var slash = payload.IndexOf('/');
        if (slash < 0) return (0, 0);
        return (ParseSize(payload[..slash]), ParseSize(payload[(slash + 1)..]));
    }

    private static long ParseSize(string text)
    {
        var end = text.IndexOfAny(['(', ']', ' ']);
        if (end >= 0) text = text[..end];
        foreach (var (suffix, factor) in new[] { ("GiB", 1024L * 1024 * 1024), ("MiB", 1024L * 1024), ("KiB", 1024L), ("B", 1L) })
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value >= 0 && value <= long.MaxValue / factor ? (long)(value * factor) : 0;
        return 0;
    }
}
