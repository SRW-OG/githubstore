using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>校验来源与实体后续传，校验完整产物后从同目录 .part 原子发布。</summary>
public sealed class HttpDownloadService : IDownloadService
{
    private const int BufferSize = 64 * 1024;
    private readonly HttpClient _http;

    public HttpDownloadService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
    }

    public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
    public Task<bool> ProbeAria2Async(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.PeerSource))
            return DownloadResult.Failure(DownloadErrorCodes.PeerNotSupported, request.Source);
        var inputError = DownloadFileRules.Validate(request, out var targetPath);
        if (inputError is not null) return DownloadResult.Failure(inputError, request.Source);
        var partPath = targetPath + ".part";
        var metadataPath = partPath + ".json";
        try
        {
            ct.ThrowIfCancellationRequested();
            var metadata = ReadMetadata(metadataPath);
            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            var resume = existing > 0 && metadata?.CanResume(request.Source, existing, request.ExpectedSize) == true;

            // 非法 206/416 只允许一次不带 Range 的重发；绝不读取错误响应正文。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, request.Source);
                // 字节范围必须基于未压缩实体；被注入的 HttpClient 不应自动解压下载内容。
                message.Headers.AcceptEncoding.ParseAdd("identity");
                if (resume)
                {
                    message.Headers.Range = new RangeHeaderValue(existing, null);
                    message.Headers.IfRange = metadata!.CreateIfRange();
                }
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var partial = response.StatusCode == HttpStatusCode.PartialContent;
                long? total = response.Content.Headers.ContentLength;
                var validPartial = partial && resume && IsValidPartial(response, metadata!, existing, request.ExpectedSize, out total);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable || (partial && !validPartial))
                {
                    if (attempt == 0)
                    {
                        resume = false;
                        continue;
                    }
                    return DownloadResult.Failure(DownloadErrorCodes.InvalidResponse, request.Source);
                }
                if (response.StatusCode != HttpStatusCode.OK && !validPartial)
                    return DownloadResult.Failure(DownloadErrorCodes.HttpStatusError, request.Source);
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    return DownloadResult.Failure(DownloadErrorCodes.InvalidResponse, request.Source);
                if (request.ExpectedSize.HasValue && total.HasValue && request.ExpectedSize.Value != total.Value)
                    return DownloadResult.Failure(DownloadErrorCodes.LengthMismatch, request.Source);
                total ??= request.ExpectedSize;

                var done = validPartial ? existing : 0;
                var responseBytes = 0L;
                var expectedResponseBytes = response.Content.Headers.ContentLength;
                // 打开流失败时不破坏现有断点；全量响应被接受后才重置断点身份。
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                if (!validPartial) DownloadFileRules.TryDelete(metadataPath);
                await using (var destination = new FileStream(partPath, validPartial ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    var nextMetadata = validPartial ? metadata! : new ResumeMetadata
                    {
                        Source = request.Source.AbsoluteUri,
                        ETag = response.Headers.ETag?.ToString(),
                        LastModified = response.Content.Headers.LastModified,
                        TotalLength = total,
                    };
                    WriteMetadata(metadataPath, nextMetadata);
                    var buffer = new byte[BufferSize];
                    var lastReport = Stopwatch.GetTimestamp();
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        if ((total.HasValue && read > total.Value - done)
                            || (expectedResponseBytes.HasValue && read > expectedResponseBytes.Value - responseBytes))
                        {
                            DownloadFileRules.TryDelete(metadataPath);
                            return DownloadResult.Failure(DownloadErrorCodes.LengthMismatch, request.Source);
                        }
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        responseBytes += read;
                        if (progress is not null && Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(100))
                        {
                            lastReport = Stopwatch.GetTimestamp();
                            progress.Report(new DownloadProgress { Source = request.Source, Done = done, Total = total ?? 0, Retries = attempt });
                        }
                    }
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                }
                if (expectedResponseBytes.HasValue && responseBytes != expectedResponseBytes.Value)
                    return DownloadResult.Failure(DownloadErrorCodes.LengthMismatch, request.Source);
                var verification = await DownloadFileRules.VerifyAsync(partPath, request, total, ct).ConfigureAwait(false);
                if (verification is not null)
                {
                    if (verification == DownloadErrorCodes.ChecksumMismatch) DownloadFileRules.TryDelete(metadataPath);
                    return DownloadResult.Failure(verification, request.Source);
                }
                ct.ThrowIfCancellationRequested();
                DownloadFileRules.Publish(partPath, targetPath);
                DownloadFileRules.TryDelete(metadataPath);
                progress?.Report(new DownloadProgress { Source = request.Source, Done = done, Total = total ?? done, Retries = attempt, Message = "completed" });
                return DownloadResult.Success(request.Source, targetPath, done, DownloadEngine.Http);
            }
            return DownloadResult.Failure(DownloadErrorCodes.InvalidResponse, request.Source);
        }
        catch (OperationCanceledException)
        {
            // 调用方取消返回稳定取消码，HttpClient 自身超时属于网络失败。
            return DownloadResult.Failure(ct.IsCancellationRequested ? DownloadErrorCodes.Cancelled : DownloadErrorCodes.NetworkError, request.Source);
        }
        catch (UnauthorizedAccessException) { return DownloadResult.Failure(DownloadErrorCodes.TargetNotWritable, request.Source); }
        catch (IOException) { return DownloadResult.Failure(DownloadErrorCodes.NetworkError, request.Source); }
        catch (HttpRequestException) { return DownloadResult.Failure(DownloadErrorCodes.NetworkError, request.Source); }
    }

    private static bool IsValidPartial(HttpResponseMessage response, ResumeMetadata metadata, long existing,
        long? expectedSize, out long? total)
    {
        var range = response.Content.Headers.ContentRange;
        total = range?.Length;
        if (range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || !range.HasRange || !range.HasLength || range.From != existing || range.To != range.Length - 1
            || range.Length <= existing || range.Length != metadata.TotalLength
            || (expectedSize.HasValue && range.Length != expectedSize)
            || (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != range.Length - existing))
            return false;
        // If-Range 已约束服务器；若返回了验证器，还必须与本地实体一致。
        if (metadata.StrongETag is { } etag && response.Headers.ETag is { } actual && !etag.Equals(actual)) return false;
        if (metadata.StrongETag is null && response.Content.Headers.LastModified is { } modified && metadata.LastModified != modified) return false;
        return true;
    }

    private static ResumeMetadata? ReadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return null;
            return JsonSerializer.Deserialize<ResumeMetadata>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteMetadata(string path, ResumeMetadata metadata)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
            File.Move(temporary, path, overwrite: true);
        }
        finally { DownloadFileRules.TryDelete(temporary); }
    }

    private sealed class ResumeMetadata
    {
        public ResumeMetadata() { }
        public string? Source { get; init; }
        public string? ETag { get; init; }
        public DateTimeOffset? LastModified { get; init; }
        public long? TotalLength { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public EntityTagHeaderValue? StrongETag => EntityTagHeaderValue.TryParse(ETag, out var value)
            && !value.IsWeak && value.Tag != "*" ? value : null;
        public bool CanResume(Uri source, long existing, long? expectedSize) => Source == source.AbsoluteUri
            && TotalLength.HasValue && existing < TotalLength.Value
            && (!expectedSize.HasValue || expectedSize.Value == TotalLength.Value)
            && (StrongETag is not null || LastModified.HasValue);
        public RangeConditionHeaderValue CreateIfRange() => StrongETag is { } etag
            ? new RangeConditionHeaderValue(etag) : new RangeConditionHeaderValue(LastModified!.Value);
    }
}
