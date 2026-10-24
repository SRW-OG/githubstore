using System.Text.Json.Serialization;

namespace GitHubStore.Core.Abstractions;

/// <summary>
/// 队列任务状态。状态机单向迁移，只有 Resume/Retry 会把任务退回 Queued：
/// Queued → Downloading；Downloading → Paused | Completed | Failed | Canceled；
/// Paused → Queued；Failed | Canceled → Queued（Retry）。
/// 终态为 Completed | Failed | Canceled，只有终态任务允许移除。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DownloadJobState>))]
public enum DownloadJobState
{
    /// <summary>已入队，等待前一个任务结束。本队列严格串行，不存在并行下载。</summary>
    Queued,
    /// <summary>正在下载。该状态只在本进程内真实存在；重启后必定回落为 Paused 或（核验通过时）Completed。</summary>
    Downloading,
    /// <summary>已停止且保留可验证的 HTTP 断点，可由用户继续。</summary>
    Paused,
    /// <summary>已完成，落盘产物已按已知大小与 SHA-256 核验。</summary>
    Completed,
    /// <summary>已失败并停止。瞬态错误已用尽有界重试；硬失败（如 SHA-256 不匹配）从不重试。</summary>
    Failed,
    /// <summary>用户取消产生的终态。与 Paused 不同，取消不会自动或隐含地继续，需要显式重试。</summary>
    Canceled,
}

/// <summary>入队结果。Duplicate 表示同身份的既有任务仍在队列中，本次没有新建任务。</summary>
public enum DownloadQueueEnqueueOutcome { Enqueued, Duplicate, Rejected }

/// <summary>队列任务身份。来源绝对地址与文件名共同确定身份，比较时大小写不敏感。</summary>
public static class DownloadQueueIdentity
{
    /// <summary>
    /// 同名不同来源、同来源不同名都算不同任务——它们可能是完全不同的产物。
    /// 只有两者都相同（忽略大小写）才视为同一任务。
    /// </summary>
    public static string Key(Uri source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fileName);
        return source.AbsoluteUri.ToLowerInvariant() + "\n" + fileName.ToLowerInvariant();
    }
}

/// <summary>一次入队请求。字段与 DownloadRequest 对齐，但可由队列持久化并在重启后重建。</summary>
public sealed record DownloadQueueRequest
{
    public required Uri Source { get; init; }
    /// <summary>查询用文件名；同时参与身份判定与安全校验。</summary>
    public required string FileName { get; init; }
    /// <summary>目标文件完整路径。必须是绝对路径，且文件名通过安全校验。</summary>
    public required string TargetPath { get; init; }
    public DownloadEngine Engine { get; init; } = DownloadEngine.Http;
    public bool UseAccelerator { get; init; }
    /// <summary>已知总字节数；null 表示未知，0 表示确认为空文件。</summary>
    public long? ExpectedSize { get; init; }
    /// <summary>可选 SHA-256 十六进制摘要，接受 GitHub 的 sha256: 前缀。</summary>
    public string? ExpectedSha256 { get; init; }
}

/// <summary>单个任务的只读快照。DoneBytes/TotalBytes 单位为字节。</summary>
public sealed record DownloadQueueJobInfo
{
    public required Guid Id { get; init; }
    public required Uri Source { get; init; }
    public required string FileName { get; init; }
    public required string TargetPath { get; init; }
    public DownloadEngine Engine { get; init; } = DownloadEngine.Http;
    public bool UseAccelerator { get; init; }
    public long? ExpectedSize { get; init; }
    public string? ExpectedSha256 { get; init; }
    public DownloadJobState State { get; init; }
    /// <summary>已经发生的下载尝试次数，从 0 开始；进入 Downloading 时递增。</summary>
    public int Attempts { get; init; }
    public long DoneBytes { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>稳定错误码；只在该任务已失败或暂停时提供。</summary>
    public string? ErrorCode { get; init; }
    /// <summary>面向界面的简短说明键或文本，用于解释状态来源。</summary>
    public string? Message { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public bool IsTerminal => State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Canceled;
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1) : 0;
    public string IdentityKey => DownloadQueueIdentity.Key(Source, FileName);
}

/// <summary>队列进度。Done/Total 单位为字节，只描述单个任务。</summary>
public sealed record DownloadQueueProgress
{
    public required Guid JobId { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
    /// <summary>本次尝试的序号，从 1 开始。</summary>
    public int Attempt { get; init; }
    public string? Message { get; init; }
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>入队结果。Rejected 时 ErrorCode 说明拒绝原因；Duplicate 时 JobId 指向既有任务。</summary>
public sealed record DownloadQueueEnqueueResult
{
    public DownloadQueueEnqueueOutcome Outcome { get; init; }
    public Guid JobId { get; init; }
    public string? ErrorCode { get; init; }
    public DownloadQueueJobInfo? Job { get; init; }

    /// <summary>Enqueued 与 Duplicate 都不算失败：调用方据此区分“新建”与“已存在”。</summary>
    public bool Accepted => Outcome != DownloadQueueEnqueueOutcome.Rejected;

    public static DownloadQueueEnqueueResult Enqueued(DownloadQueueJobInfo job) =>
        new() { Outcome = DownloadQueueEnqueueOutcome.Enqueued, JobId = job.Id, Job = job };
    public static DownloadQueueEnqueueResult Duplicate(DownloadQueueJobInfo existing) =>
        new() { Outcome = DownloadQueueEnqueueOutcome.Duplicate, JobId = existing.Id, Job = existing };
    public static DownloadQueueEnqueueResult Rejected(string errorCode) =>
        new() { Outcome = DownloadQueueEnqueueOutcome.Rejected, ErrorCode = errorCode };
}

/// <summary>逐任务命令（暂停/继续/取消/重试/移除）的结果。</summary>
public sealed record DownloadQueueCommandResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public DownloadQueueJobInfo? Job { get; init; }

    public static DownloadQueueCommandResult Ok(DownloadQueueJobInfo? job = null) =>
        new() { Succeeded = true, Job = job };
    public static DownloadQueueCommandResult Failure(string errorCode, DownloadQueueJobInfo? job = null) =>
        new() { Succeeded = false, ErrorCode = errorCode, Job = job };
}

/// <summary>队列稳定错误码。任务自身的失败原因另外使用 DownloadErrorCodes。</summary>
public static class DownloadQueueErrorCodes
{
    /// <summary>请求本身不合法（来源、文件名、目标路径、大小或摘要）。</summary>
    public const string InvalidRequest = "download_queue_invalid_request";
    /// <summary>找不到该任务。</summary>
    public const string JobNotFound = "download_queue_job_not_found";
    /// <summary>当前状态不允许该命令（例如对已完成任务暂停，或移除非终态任务）。</summary>
    public const string InvalidState = "download_queue_invalid_state";
    /// <summary>队列文件无法读取；队列被锁定且不会覆盖原文件。</summary>
    public const string QueueFileInvalid = "download_queue_file_invalid";
    /// <summary>队列文件写入失败；内存中的队列未被丢弃。</summary>
    public const string QueueWriteFailed = "download_queue_write_failed";
    /// <summary>队列仍处于加载失败锁定状态，命令被拒绝以免丢失用户队列。</summary>
    public const string QueueUnavailable = "download_queue_unavailable";
    /// <summary>上次运行中断，任务已回落为 Paused，HTTP 断点保留。</summary>
    public const string Interrupted = "download_queue_interrupted";
    /// <summary>重启后核验落盘产物通过，任务被对账为已完成。</summary>
    public const string Reconciled = "download_queue_reconciled";
    /// <summary>下载服务报告成功，但目标文件未通过大小/SHA-256 核验。</summary>
    public const string VerifyFailed = "download_queue_verify_failed";
    /// <summary>文件已下载，但可复用的缓存记录未能写出。</summary>
    public const string CacheRecordFailed = "download_queue_cache_record_failed";
    /// <summary>队列大小或单文件大小超出上限。</summary>
    public const string LimitExceeded = "download_queue_limit_exceeded";
    /// <summary>
    /// 下载依赖抛出了未预期异常（不属于任何已知下载失败类别，例如 InvalidOperationException）。
    /// 任务按失败收束，原始异常内容不进入队列状态、也不泄漏给界面。
    /// 刻意不参与瞬态重试：“未知”不构成重试理由，重复执行可能放大副作用。
    /// </summary>
    public const string UnexpectedFailure = "download_queue_unexpected_failure";
}
