using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubStore.App.Services.Inventory;

/// <summary>
/// 安装/卸载操作日志的一条记录。字段刻意保持扁平，
/// 便于直接用文本工具查看 JSONL，不依赖本程序。
/// </summary>
public sealed record OperationLogEntry
{
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>取值见 <see cref="OperationLog.ActionUninstall"/> 等常量。</summary>
    public required string Action { get; init; }

    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? Path { get; init; }

    /// <summary>结果：已启动用 "started"，启动失败用稳定错误码；不表示卸载完成。</summary>
    public required string Outcome { get; init; }

    /// <summary>补充信息：实际启动的命令行，或失败细节。</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// 追加式 JSONL 操作日志（每行一个 JSON 对象），写到
/// <c>&lt;dataDirectory&gt;/logs/operations.jsonl</c>。
///
/// 设计取舍：日志是「尽力而为」的旁路信息，只记录卸载器启动结果，不跟踪卸载完成。
/// 任何日志写入失败都只写 Trace，绝不抛出，以免把已经启动的操作显示为启动失败。
/// </summary>
public sealed class OperationLog
{
    public const string ActionUninstall = "uninstall";
    public const string ActionUninstallFailed = "uninstall_failed";
    public const string ActionScan = "scan";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 省略 null 字段，让每一行更短、更易读。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OperationLog(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _filePath = Path.Combine(Path.GetFullPath(dataDirectory), "logs", "operations.jsonl");
    }

    public string FilePath => _filePath;

    /// <summary>
    /// 追加一条记录。目录会自动创建；失败只写 Trace，不抛出。
    /// </summary>
    public async Task AppendAsync(OperationLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var acquired = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            // FileShare.Read 允许查看器在写入时同时读取；
            // 追加而不是覆盖，保证历史不被截断。
            // 注意：FileStream 构造后不能直接 .ConfigureAwait(false)——
            // 那会得到 ConfiguredAsyncDisposable，而不是流本身。
            await using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.WriteLine(ex);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    /// <summary>
    /// 读取最近的 <paramref name="maxEntries"/> 条记录（按文件顺序，末尾最新）。
    /// 文件不存在返回空集合；损坏行跳过；空行跳过。
    /// </summary>
    public async Task<IReadOnlyList<OperationLogEntry>> ReadAsync(int maxEntries, CancellationToken ct = default)
    {
        if (maxEntries <= 0) return [];

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return [];

            // 只保留尾部窗口：日志文件可能很大，没必要把全部历史读进内存。
            var window = new Queue<OperationLogEntry>(maxEntries);
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<OperationLogEntry>(line, JsonOptions) is not { } entry) continue;
                    if (window.Count == maxEntries) window.Dequeue();
                    window.Enqueue(entry);
                }
                catch (JsonException ex)
                {
                    // 单行损坏（例如上次写入被强制结束）不能让整次读取失败。
                    Trace.WriteLine(ex);
                }
            }

            return window.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }
}
