using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>持久化下载队列的参数。默认值面向真实使用，测试可注入零退避。</summary>
public sealed record DownloadQueueOptions
{
    /// <summary>队列 JSON 文件的完整路径；必须位于应用数据目录内，以便重启后恢复。</summary>
    public required string QueueFilePath { get; init; }

    /// <summary>单个任务在一次运行内的最大尝试次数（含首次）。硬失败从不消耗重试预算。</summary>
    public int MaximumAttempts { get; init; } = 3;

    /// <summary>瞬态失败后的基础退避，按尝试次数指数增长并封顶。</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>队列中允许的最大任务数，防止队列文件无界增长。</summary>
    public int MaximumJobs { get; init; } = 500;

    /// <summary>已知总长超过该值的文件被拒绝；null 长度的任务不受此限制。</summary>
    public long MaximumFileBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>退避上限，避免长时间不推进队列。</summary>
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 持久化的串行下载队列。
/// <para>
/// 串行是刻意设计：<see cref="PackageCacheService"/> 用单个根租约保护下载与安装，
/// 该租约无法表达“多个下载并行共享缓存根”。因此本队列在同一时刻只执行一个任务，
/// 且每个任务的运行期持有一次缓存租约，从而与安装流程互斥。
/// 本服务不实现、也不承诺并行下载。
/// </para>
/// <para>
/// 断点续传完全复用 <see cref="HttpDownloadService"/> 的已验证实现：.part 文件加原子 JSON sidecar，
/// 配合 Range + If-Range 与严格的 206 Content-Range 校验。本队列只负责状态、持久化与调度，
/// 不重新实现续传逻辑。
/// </para>
/// </summary>
public interface IDownloadQueueService
{
    /// <summary>队列 JSON 文件的完整路径。</summary>
    string QueueFilePath { get; }

    /// <summary>
    /// 队列文件无法读取时给出的稳定错误码；为 null 表示队列可用。
    /// 非 null 时队列进入锁定状态：不覆盖原文件，也不接受任何变更命令。
    /// </summary>
    string? LoadError { get; }

    /// <summary>最近一次持久化失败等运行期问题的稳定错误码；为 null 表示没有已知问题。</summary>
    string? LastError { get; }

    /// <summary>当前队列的只读快照，按入队顺序排列。</summary>
    IReadOnlyList<DownloadQueueJobInfo> Jobs { get; }

    /// <summary>
    /// 读取并校验队列文件、对账中断的任务，然后启动串行工作循环。可重复调用。
    /// 队列文件损坏时只报告 <see cref="LoadError"/>，绝不把用户的任务替换成空队列。
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>按身份（来源 + 文件名，大小写不敏感）入队；重复入队返回既有任务而不新建。</summary>
    Task<DownloadQueueEnqueueResult> EnqueueAsync(DownloadQueueRequest request, CancellationToken ct = default);

    /// <summary>暂停任务；正在下载时取消当前工作但保留可验证的 HTTP 断点。</summary>
    Task<DownloadQueueCommandResult> PauseAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>继续已暂停的任务；HTTP 引擎按已验证的范围续传。</summary>
    Task<DownloadQueueCommandResult> ResumeAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>取消任务。取消是与暂停不同的终态，不会自动继续，需要显式重试。</summary>
    Task<DownloadQueueCommandResult> CancelAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>把失败或已取消的任务重新排队，并重置重试预算。</summary>
    Task<DownloadQueueCommandResult> RetryAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>移除任务记录；只允许终态（已完成/失败/已取消）。不删除已发布的文件。</summary>
    Task<DownloadQueueCommandResult> RemoveAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>任务快照发生变化时触发；可能在工作线程上触发，界面需要自行调度。</summary>
    event EventHandler<DownloadQueueJobInfo>? JobChanged;

    /// <summary>任务进度变化时触发；Done/Total 单位为字节。</summary>
    event EventHandler<DownloadQueueProgress>? ProgressChanged;
}

/// <summary>
/// 串行下载队列实现。状态机与持久化格式见 <see cref="DownloadJobState"/> 与 <see cref="QueueDocument"/>。
/// </summary>
public sealed class DownloadQueueService : IDownloadQueueService, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private const long MaximumQueueFileBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IDownloadService _downloads;
    private readonly PackageCacheService? _packageCache;
    private readonly DownloadQueueOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Job> _jobs = [];

    private DownloadQueueJobInfo[] _snapshot = [];
    private Task? _worker;
    private string? _loadErrorCode;
    private string? _loadErrorDetail;
    private string? _lastError;
    private bool _initialized;
    private bool _disposed;

    public DownloadQueueService(IDownloadService downloads, DownloadQueueOptions options,
        PackageCacheService? packageCache = null)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.QueueFilePath);
        if (options.MaximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumJobs < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _downloads = downloads;
        _options = options;
        _packageCache = packageCache;
        QueueFilePath = Path.GetFullPath(options.QueueFilePath);
    }

    public DownloadQueueService(IDownloadService downloads, string queueFilePath,
        PackageCacheService? packageCache = null)
        : this(downloads, new DownloadQueueOptions { QueueFilePath = queueFilePath }, packageCache) { }

    public string QueueFilePath { get; }
    public string? LoadError => Volatile.Read(ref _loadErrorCode);
    public string? LoadErrorDetail => Volatile.Read(ref _loadErrorDetail);
    public string? LastError => Volatile.Read(ref _lastError);
    public IReadOnlyList<DownloadQueueJobInfo> Jobs => Volatile.Read(ref _snapshot);

    public event EventHandler<DownloadQueueJobInfo>? JobChanged;
    public event EventHandler<DownloadQueueProgress>? ProgressChanged;

    // ------------------------------------------------------------------ 初始化与加载

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var startWorker = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _initialized = true;
            var loaded = await LoadLockedAsync(ct).ConfigureAwait(false);
            var reconciled = false;
            if (loaded)
            {
                // 只有加载成功才允许对账与回写；加载失败时原文件必须原封不动地保留。
                reconciled = await ReconcileLockedAsync(ct).ConfigureAwait(false);
            }
            PublishSnapshotLocked();
            // 仅在确实发生了状态变更时才写回：用户从未使用队列时不应凭空产生一个队列文件。
            if (loaded && reconciled) await PersistLockedAsync(ct).ConfigureAwait(false);
            startWorker = loaded;
        }
        finally
        {
            _gate.Release();
        }
        if (startWorker)
        {
            _worker = RunWorkerAsync(_lifetime.Token);
            // 上一进程留下的 Queued 任务会在这里被执行；中断的 Downloading 任务已在对账时回落为 Paused。
            Signal();
        }
    }

    /// <summary>返回 true 表示队列可用；false 表示文件不可读，队列被锁定。</summary>
    private async Task<bool> LoadLockedAsync(CancellationToken ct)
    {
        _jobs.Clear();
        Volatile.Write(ref _loadErrorCode, null);
        Volatile.Write(ref _loadErrorDetail, null);
        try
        {
            if (!File.Exists(QueueFilePath)) return true;
            if (new FileInfo(QueueFilePath).Length > MaximumQueueFileBytes)
            {
                Lock($"Queue file exceeds {MaximumQueueFileBytes} bytes.");
                return false;
            }

            var json = await File.ReadAllTextAsync(QueueFilePath, ct).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<QueueDocument>(json, JsonOptions)
                ?? throw new JsonException("The queue document must be a JSON object.");
            if (document.Version != SchemaVersion)
                throw new JsonException($"Unsupported queue schema version {document.Version}.");
            if (document.Jobs.Count > _options.MaximumJobs)
                throw new JsonException("The queue contains more jobs than the configured limit.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Jobs)
            {
                var job = Convert(entry);
                // 同身份的重复记录视为损坏：静默丢弃任何一条都会改变用户看到的队列。
                if (!seen.Add(job.IdentityKey()))
                    throw new JsonException("The queue contains duplicate job identities.");
                _jobs.Add(job);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Lock(ex.GetType().Name + ": queue JSON could not be validated.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Lock(ex.GetType().Name + ": queue file could not be read.");
            return false;
        }
    }

    /// <summary>
    /// 进入锁定状态：报告问题、保持内存队列为空，并且此后绝不写入队列文件。
    /// 这样用户原有的任务仍然留在磁盘上，可以人工恢复，而不是被一个空队列覆盖。
    /// </summary>
    private void Lock(string detail)
    {
        _jobs.Clear();
        Volatile.Write(ref _loadErrorCode, DownloadQueueErrorCodes.QueueFileInvalid);
        Volatile.Write(ref _loadErrorDetail, detail);
    }

    private Job Convert(QueueJobDocument entry)
    {
        var source = ParseSource(entry.Source);
        var error = ValidateFields(source, entry.FileName, entry.TargetPath, entry.ExpectedSize, entry.ExpectedSha256);
        if (error is not null) throw new JsonException("A queue job failed validation: " + error);
        if (!Enum.IsDefined(entry.Engine)) throw new JsonException("A queue job has an unknown engine.");
        if (!Enum.IsDefined(entry.State)) throw new JsonException("A queue job has an unknown state.");
        if (entry.Attempts < 0 || entry.DoneBytes < 0 || entry.TotalBytes < 0)
            throw new JsonException("A queue job has negative counters.");
        // 只有终态可以在重启后原样保留；Downloading 交给对账处理。
        return new Job
        {
            Id = entry.Id,
            Source = source,
            FileName = entry.FileName,
            TargetPath = Path.GetFullPath(entry.TargetPath),
            Engine = entry.Engine,
            UseAccelerator = entry.UseAccelerator,
            ExpectedSize = entry.ExpectedSize,
            ExpectedSha256 = entry.ExpectedSha256,
            State = entry.State,
            Attempts = entry.Attempts,
            DoneBytes = entry.DoneBytes,
            TotalBytes = entry.TotalBytes,
            ErrorCode = entry.ErrorCode,
            Message = entry.Message,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
        };
    }

    /// <summary>
    /// 重启对账。两类都必须处理，且都不能含糊：
    /// 1) 上次运行时处于 Downloading 的任务没有“完成”的证据——只有落盘产物通过大小/SHA-256 核验才是完成，
    ///    否则回落为 Paused 并保留断点，绝不自动重试、也绝不丢失。
    /// 2) 已标记 Completed 的任务要重新核验产物；文件被删除或损坏时不能继续声称完成。
    /// </summary>
    private async Task<bool> ReconcileLockedAsync(CancellationToken ct)
    {
        var changed = false;
        foreach (var job in _jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (job.State == DownloadJobState.Downloading)
            {
                changed = true;
                // 只有存在正期望值时才可能“凭落盘产物认定为已完成”。若任务没有已知大小也没有摘要，
                // 一个同名文件不能证明它就是本次下载的产物，此时一律回落为 Paused 并保留断点。
                var verifiable = HasPositiveExpectation(job);
                var verified = verifiable ? await VerifyTargetAsync(job, ct).ConfigureAwait(false) : null;
                if (verifiable && verified is null)
                {
                    job.State = DownloadJobState.Completed;
                    job.ErrorCode = null;
                    job.Message = DownloadQueueErrorCodes.Reconciled;
                    job.DoneBytes = job.TotalBytes;
                }
                else
                {
                    job.State = DownloadJobState.Paused;
                    job.ErrorCode = null;
                    job.Message = DownloadQueueErrorCodes.Interrupted;
                }
                job.Attempts = 0;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }
            if (job.State == DownloadJobState.Completed && job.TargetPath.Length > 0
                && await VerifyTargetAsync(job, ct).ConfigureAwait(false) is { } failure)
            {
                // 已完成任务的产物不再可信：如实降级，不继续声称完成。
                changed = true;
                job.State = DownloadJobState.Failed;
                job.ErrorCode = failure;
                job.Message = DownloadQueueErrorCodes.VerifyFailed;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        return changed;
    }

    // ------------------------------------------------------------------ 入队

    public async Task<DownloadQueueEnqueueResult> EnqueueAsync(DownloadQueueRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        // 必须先加载并校验既有队列文件：否则一次入队就可能覆盖掉尚未读取的用户队列。
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Locked) return DownloadQueueEnqueueResult.Rejected(DownloadQueueErrorCodes.QueueUnavailable);
            if (_jobs.Count >= _options.MaximumJobs)
                return DownloadQueueEnqueueResult.Rejected(DownloadQueueErrorCodes.LimitExceeded);

            var validation = ValidateFields(request.Source, request.FileName, request.TargetPath,
                request.ExpectedSize, request.ExpectedSha256);
            if (validation is not null) return DownloadQueueEnqueueResult.Rejected(validation);
            if (!Enum.IsDefined(request.Engine))
                return DownloadQueueEnqueueResult.Rejected(DownloadQueueErrorCodes.InvalidRequest);

            // 身份 = 来源绝对地址 + 文件名（大小写不敏感）。重复入队不新建任务，
            // 并且必须把既有任务告诉调用方，而不是静默地什么都不做。
            var identity = DownloadQueueIdentity.Key(request.Source, request.FileName);
            var duplicate = _jobs.FirstOrDefault(j => j.IdentityKey() == identity);
            if (duplicate is not null) return DownloadQueueEnqueueResult.Duplicate(duplicate.Info());

            var target = request.TargetPath;
            if (string.IsNullOrEmpty(target) && _packageCache is { } cache)
                target = cache.TryResolvePath(request.Source, request.FileName);
            if (string.IsNullOrEmpty(target))
                return DownloadQueueEnqueueResult.Rejected(DownloadQueueErrorCodes.InvalidRequest);

            var now = DateTimeOffset.UtcNow;
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Source = request.Source,
                FileName = request.FileName,
                TargetPath = Path.GetFullPath(target),
                Engine = request.Engine,
                UseAccelerator = request.UseAccelerator,
                ExpectedSize = request.ExpectedSize,
                ExpectedSha256 = request.ExpectedSha256,
                State = DownloadJobState.Queued,
                Attempts = 0,
                DoneBytes = 0,
                TotalBytes = request.ExpectedSize ?? 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _jobs.Add(job);
            PublishSnapshotLocked();
            await PersistLockedAsync(ct).ConfigureAwait(false);
            var info = job.Info();
            Signal();
            return DownloadQueueEnqueueResult.Enqueued(info);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ 逐任务命令

    /// <summary>
    /// 暂停：正在下载时取消当前工作，由 <see cref="HttpDownloadService"/> 保留可验证的 .part 断点。
    /// 排队中的任务直接转为 Paused；终态任务拒绝该命令。
    /// </summary>
    public Task<DownloadQueueCommandResult> PauseAsync(Guid jobId, CancellationToken ct = default) =>
        StopAsync(jobId, DownloadJobState.Paused, allowPaused: false, ct);

    /// <summary>
    /// 取消：与暂停不同的终态。取消不会自动继续，用户必须显式重试；
    /// 界面据此把两者区分开来。<see cref="DownloadEngine.Aria2"/> 的临时产物在取消时被删除，
    /// 因此 aria2 任务的重试是从零重新下载，而不是续传。
    /// </summary>
    public Task<DownloadQueueCommandResult> CancelAsync(Guid jobId, CancellationToken ct = default) =>
        StopAsync(jobId, DownloadJobState.Canceled, allowPaused: true, ct);

    // completion 由本类自己的工作循环创建（RunCompletion），并非外部上下文启动的任务；
    // 等待它正是“暂停/取消已真正收束”的唯一依据，这里不会造成死锁。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003",
        Justification = "等待的是本类自己启动的工作循环所发布的完成信号，不是外部任务。")]
    private async Task<DownloadQueueCommandResult> StopAsync(Guid jobId, DownloadJobState target, bool allowPaused,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? completion = null;
        CancellationTokenSource? runCts = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Locked) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.QueueUnavailable);
            var job = FindLocked(jobId);
            if (job is null) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.JobNotFound);
            switch (job.State)
            {
                case DownloadJobState.Downloading:
                    // 运行中的任务先记录意图，再由工作循环收束成最终状态；调用方等待该收束。
                    job.PendingState = target;
                    completion = job.RunCompletion;
                    // 只取出 CTS，取消动作留到锁外执行：Cancel 会同步运行回调，
                    // 而回调可能再次进入本类的工作循环并等待同一把门，锁内取消有死锁风险。
                    runCts = job.RunCts;
                    break;
                case DownloadJobState.Queued:
                case DownloadJobState.Paused when allowPaused:
                    job.State = target;
                    job.ErrorCode = target == DownloadJobState.Canceled ? DownloadErrorCodes.Cancelled : null;
                    job.Message = null;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    PublishSnapshotLocked();
                    await PersistLockedAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.InvalidState, job.Info());
            }
        }
        finally
        {
            _gate.Release();
        }
        // 取消必须在释放队列锁之后：Cancel 同步执行回调，回调可能回到本类并等待同一把门。
        CancelQuietly(runCts);
        if (completion is not null) await completion.Task.ConfigureAwait(false);
        return await ReadResultAsync(jobId, ct).ConfigureAwait(false);
    }

    private static void CancelQuietly(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException ex)
        {
            // 取消回调抛出的异常不能变成命令失败：任务本身的结局由工作循环收束决定。
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    /// <summary>继续：只适用于已暂停的任务。HTTP 引擎按已验证的范围续传。</summary>
    public Task<DownloadQueueCommandResult> ResumeAsync(Guid jobId, CancellationToken ct = default) =>
        RequireAsync(jobId, [DownloadJobState.Paused], ct);

    /// <summary>重试：只适用于失败或已取消的任务，并重置重试预算（Attempts）。</summary>
    public Task<DownloadQueueCommandResult> RetryAsync(Guid jobId, CancellationToken ct = default) =>
        RequireAsync(jobId, [DownloadJobState.Failed, DownloadJobState.Canceled], ct);

    private async Task<DownloadQueueCommandResult> RequireAsync(Guid jobId, DownloadJobState[] allowed, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Locked) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.QueueUnavailable);
            var job = FindLocked(jobId);
            if (job is null) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.JobNotFound);
            if (!allowed.Contains(job.State))
                return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.InvalidState, job.Info());
            job.State = DownloadJobState.Queued;
            job.Attempts = 0;
            job.DoneBytes = 0;
            job.TotalBytes = job.ExpectedSize ?? 0;
            job.ErrorCode = null;
            job.Message = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            PublishSnapshotLocked();
            await PersistLockedAsync(ct).ConfigureAwait(false);
            var info = job.Info();
            Signal();
            return DownloadQueueCommandResult.Ok(info);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>移除：只允许终态任务。移除的是队列记录，不删除任何已发布的文件。</summary>
    public async Task<DownloadQueueCommandResult> RemoveAsync(Guid jobId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Locked) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.QueueUnavailable);
            var job = FindLocked(jobId);
            if (job is null) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.JobNotFound);
            if (!job.IsTerminal()) return DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.InvalidState, job.Info());
            _jobs.Remove(job);
            PublishSnapshotLocked();
            await PersistLockedAsync(ct).ConfigureAwait(false);
            return DownloadQueueCommandResult.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DownloadQueueCommandResult> ReadResultAsync(Guid jobId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = FindLocked(jobId);
            return job is null
                ? DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.JobNotFound)
                : DownloadQueueCommandResult.Ok(job.Info());
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ 工作循环

    private async Task RunWorkerAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            var job = await TakeNextAsync(lifetime).ConfigureAwait(false);
            if (job is null)
            {
                try { await _signal.WaitAsync(lifetime).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            await RunJobAsync(job, lifetime).ConfigureAwait(false);
        }
    }

    private async Task<Job?> TakeNextAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 严格串行：一次只取一个任务，且只在整段运行结束后才取下一个。
            var job = _jobs.FirstOrDefault(j => j.State == DownloadJobState.Queued);
            if (job is null) return null;
            job.State = DownloadJobState.Downloading;
            job.Attempts = 0;
            job.DoneBytes = 0;
            job.TotalBytes = job.ExpectedSize ?? 0;
            job.ErrorCode = null;
            job.Message = null;
            job.PendingState = null;
            // RunCompletion 在进入 Downloading 时就存在，暂停/取消命令因此总能等到收束。
            job.RunCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            PublishSnapshotLocked();
            await PersistLockedAsync(ct).ConfigureAwait(false);
            return job;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken lifetime)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        TaskCompletionSource completion;
        var pendingCancel = false;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            job.RunCts = linked;
            completion = job.RunCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 暂停/取消可能发生在进入 Downloading 与注册 CTS 之间；这里补上那次取消请求。
            pendingCancel = job.PendingState is not null;
        }
        finally
        {
            _gate.Release();
        }
        // 与 StopAsync 一致：取消在锁外执行，避免回调重新进入本类时与门互等。
        if (pendingCancel) CancelQuietly(linked);

        JobOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(job, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = Cancelled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            outcome = new JobOutcome(false, DownloadJobState.Failed, DownloadErrorCodes.TargetNotWritable, null);
        }
        try
        {
            await SettleAsync(job, outcome).ConfigureAwait(false);
        }
        finally
        {
            linked.Dispose();
            completion.TrySetResult();
        }
    }

    private async Task SettleAsync(Job job, JobOutcome outcome)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var pending = job.PendingState;
            job.PendingState = null;
            job.RunCts = null;
            job.RunCompletion = null;
            if (outcome.Cancelled)
            {
                // 结束原因是取消还是暂停由命令意图决定；两者都不是自动重试的理由。
                job.State = pending ?? DownloadJobState.Paused;
                job.ErrorCode = job.State == DownloadJobState.Canceled ? DownloadErrorCodes.Cancelled : null;
                // 未经用户暂停而中断（例如退出进程）也必须如实报告，且保持可继续。
                job.Message = job.State == DownloadJobState.Paused ? DownloadQueueErrorCodes.Interrupted : null;
            }
            else
            {
                job.State = outcome.State;
                job.ErrorCode = outcome.ErrorCode;
                job.Message = outcome.Message;
            }
            job.UpdatedAt = DateTimeOffset.UtcNow;
            PublishSnapshotLocked();
            await PersistLockedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseJobChanged(job.Id);
    }

    private async Task<JobOutcome> ExecuteAsync(Job job, CancellationToken ct)
    {
        var target = job.TargetPath;
        PackageCacheLease? lease = null;
        try
        {
            if (_packageCache is { } cache)
            {
                // 每个任务运行期持有一次缓存根租约：与安装互斥，也解释了为什么队列必须串行。
                lease = await cache.AcquireAsync(ct).ConfigureAwait(false);
                var resolved = lease.Cache.TryResolvePath(job.Source, job.FileName);
                if (resolved is null)
                    return new JobOutcome(false, DownloadJobState.Failed, DownloadQueueErrorCodes.InvalidRequest, null);
                if (!string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase))
                {
                    // 缓存根可能已被用户改动；以当前解析结果为准并如实记录。
                    target = resolved;
                    await UpdateTargetPathAsync(job, resolved).ConfigureAwait(false);
                }
            }

            // 只有具备正期望值（大小或 SHA-256）时才可能“无需下载即完成”；
            // 否则任何同名文件都不构成完成证据。
            if (HasPositiveExpectation(job) && File.Exists(target)
                && await VerifyTargetAsync(job, target, ct).ConfigureAwait(false) is null)
            {
                var receiptMessage = await RecordReceiptAsync(lease, job, target, ct).ConfigureAwait(false);
                return new JobOutcome(false, DownloadJobState.Completed, null, receiptMessage);
            }

            if (lease is not null)
                await lease.Cache.PrepareDownloadAsync(job.Source, job.FileName, ct).ConfigureAwait(false);

            var request = new DownloadRequest
            {
                Source = job.Source,
                TargetPath = target,
                FileName = job.FileName,
                Engine = job.Engine,
                UseAccelerator = job.UseAccelerator,
                ExpectedSize = job.ExpectedSize,
                ExpectedSha256 = job.ExpectedSha256,
            };

            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await MarkAttemptAsync(job, attempt).ConfigureAwait(false))
                    return new JobOutcome(false, DownloadJobState.Failed, DownloadQueueErrorCodes.QueueWriteFailed, null);

                var progress = new JobProgress(job.Id, p => ReportProgress(job, attempt, p));
                DownloadResult result;
                try
                {
                    result = await _downloads.DownloadAsync(request, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Cancelled();
                }
                if (ct.IsCancellationRequested) return Cancelled();

                if (result.Succeeded)
                {
                    // 下载服务报告成功不构成完成证据：队列独立核验落盘产物，绝不发布未核验的“完成”。
                    if (!string.Equals(result.OutputPath, target, StringComparison.OrdinalIgnoreCase))
                        return new JobOutcome(false, DownloadJobState.Failed,
                            DownloadQueueErrorCodes.VerifyFailed, DownloadQueueErrorCodes.VerifyFailed);
                    var verification = await VerifyTargetAsync(job, target, ct).ConfigureAwait(false);
                    if (verification is not null)
                        return new JobOutcome(false, DownloadJobState.Failed, verification, DownloadQueueErrorCodes.VerifyFailed);
                    var receiptMessage = await RecordReceiptAsync(lease, job, target, ct).ConfigureAwait(false);
                    return new JobOutcome(false, DownloadJobState.Completed, null, receiptMessage);
                }

                if (result.ErrorCode == DownloadErrorCodes.Cancelled) return Cancelled();
                // 只有瞬态失败消耗有界重试预算；完整性类失败（含 SHA-256 不匹配）是硬失败，绝不重试。
                if (result.ErrorCode is not null && IsTransientFailure(result.ErrorCode)
                    && attempt < _options.MaximumAttempts)
                {
                    await DelayAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return new JobOutcome(false, DownloadJobState.Failed,
                    result.ErrorCode ?? DownloadErrorCodes.NetworkError, null);
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new JobOutcome(false, DownloadJobState.Failed, DownloadErrorCodes.TargetNotWritable, null);
        }
        finally
        {
            // 断点文件由 HttpDownloadService 自行保留或清理；这里只释放租约。
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JobOutcome Cancelled() => new(true, DownloadJobState.Paused, null, null);

    private async Task<bool> MarkAttemptAsync(Job job, int attempt)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            job.Attempts = attempt;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            PublishSnapshotLocked();
            return await PersistLockedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateTargetPathAsync(Job job, string target)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            job.TargetPath = target;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            PublishSnapshotLocked();
            await PersistLockedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DelayAsync(int attempt, CancellationToken ct)
    {
        var factor = Math.Pow(2, Math.Min(attempt - 1, 16));
        var delay = TimeSpan.FromTicks((long)Math.Min(_options.RetryBackoff.Ticks * factor, _options.MaximumBackoff.Ticks));
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 可重试的瞬态失败。刻意保持窄口径：只有网络与外部引擎的临时问题才重试。
    /// 校验和/长度/响应范围等完整性失败，以及来源、路径、权限、P2P、取消等确定性失败，都不在此列。
    /// </summary>
    internal static bool IsTransientFailure(string? errorCode) => errorCode switch
    {
        DownloadErrorCodes.NetworkError => true,
        DownloadErrorCodes.HttpStatusError => true,
        DownloadErrorCodes.InvalidResponse => true,
        DownloadErrorCodes.Aria2Failed => true,
        DownloadErrorCodes.Aria2TimedOut => true,
        _ => false,
    };

    private static bool HasPositiveExpectation(Job job) =>
        job.ExpectedSize.HasValue || !string.IsNullOrWhiteSpace(job.ExpectedSha256);

    private static Task<string?> VerifyTargetAsync(Job job, CancellationToken ct) => VerifyTargetAsync(job, job.TargetPath, ct);

    /// <summary>核验已发布产物。null 表示核验通过；否则返回稳定错误码。</summary>
    private static Task<string?> VerifyTargetAsync(Job job, string target, CancellationToken ct)
    {
        // 文件不存在时不能沿用 VerifyAsync（它会直接抛 FileNotFound）：这里给出明确长度错误码。
        if (!File.Exists(target)) return Task.FromResult<string?>(DownloadErrorCodes.LengthMismatch);
        var request = new DownloadRequest
        {
            Source = job.Source,
            TargetPath = target,
            FileName = job.FileName,
            ExpectedSize = job.ExpectedSize,
            ExpectedSha256 = job.ExpectedSha256,
        };
        return DownloadFileRules.VerifyAsync(target, request, job.ExpectedSize, ct);
    }

    /// <summary>
    /// 写入可复用的缓存收据。失败不改变“文件已下载并核验通过”这一事实，
    /// 但必须如实告知：下次可能需要重新下载。返回 null 表示没有需要说明的问题。
    /// </summary>
    private static async Task<string?> RecordReceiptAsync(PackageCacheLease? lease, Job job, string target, CancellationToken ct)
    {
        if (lease is null) return null;
        try
        {
            await lease.Cache.RecordCompletedAsync(job.Source, job.FileName, job.ExpectedSize, job.ExpectedSha256, ct)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DownloadQueueErrorCodes.CacheRecordFailed;
        }
    }

    private void ReportProgress(Job job, int attempt, DownloadProgress progress)
    {
        Volatile.Write(ref job.DoneBytes, progress.Done);
        if (progress.Total > 0) Volatile.Write(ref job.TotalBytes, progress.Total);
        ProgressChanged?.Invoke(this, new DownloadQueueProgress
        {
            JobId = job.Id,
            Done = progress.Done,
            Total = progress.Total > 0 ? progress.Total : job.TotalBytes,
            Attempt = attempt,
            Message = progress.Message,
        });
    }

    // ------------------------------------------------------------------ 持久化

    /// <summary>写入队列文件；失败只记录错误码，绝不丢弃内存中的任务。</summary>
    private async Task<bool> PersistLockedAsync(CancellationToken ct)
    {
        if (Locked) return false;
        try
        {
            var directory = Path.GetDirectoryName(QueueFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var document = new QueueDocument
            {
                Version = SchemaVersion,
                Jobs = _jobs.Select(j => j.Document()).ToArray(),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            var temporary = QueueFilePath + ".tmp";
            // 临时文件与目标同目录同卷，只有完整内容才通过一次移动发布。
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporary, QueueFilePath, overwrite: true);
            Volatile.Write(ref _lastError, null);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Volatile.Write(ref _lastError, DownloadQueueErrorCodes.QueueWriteFailed);
            return false;
        }
    }

    // ------------------------------------------------------------------ 校验

    /// <summary>
    /// 入队与加载共用同一套校验，避免“能写进文件却读不出来”的状态。
    /// 逐条拒绝：非 HTTPS 来源、URL 内嵌凭据、相对或设备路径、不安全文件名、负数/超大长度、非法摘要。
    /// </summary>
    private string? ValidateFields(Uri source, string? fileName, string? targetPath, long? expectedSize, string? expectedSha256)
    {
        if (!DownloadFileRules.IsSupportedSource(source)) return DownloadErrorCodes.UnsupportedSource;
        if (string.IsNullOrWhiteSpace(fileName) || !PackageCacheService.IsSafeFileName(fileName))
            return DownloadQueueErrorCodes.InvalidRequest;
        if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath))
            return DownloadQueueErrorCodes.InvalidRequest;
        string full;
        try { full = Path.GetFullPath(targetPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return DownloadQueueErrorCodes.InvalidRequest; }
        // 设备路径与 UNC 是拒绝目标：下载目标必须是一个普通的本地绝对路径。
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !PackageCacheService.IsSafeFileName(Path.GetFileName(full)))
            return DownloadQueueErrorCodes.InvalidRequest;
        if (expectedSize is < 0 or > long.MaxValue) return DownloadErrorCodes.InvalidVerification;
        if (expectedSize.HasValue && expectedSize.Value > _options.MaximumFileBytes)
            return DownloadQueueErrorCodes.LimitExceeded;
        if (!DownloadFileRules.TryNormalizeSha256(expectedSha256, out _))
            return DownloadErrorCodes.InvalidVerification;
        return null;
    }

    private static Uri ParseSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new JsonException("A queue job is missing its source.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new JsonException("A queue job has an invalid source.");
        return uri;
    }

    // ------------------------------------------------------------------ 基础设施

    private bool Locked => _initialized && Volatile.Read(ref _loadErrorCode) is not null;

    private Job? FindLocked(Guid jobId) => _jobs.FirstOrDefault(j => j.Id == jobId);

    private void PublishSnapshotLocked() => Volatile.Write(ref _snapshot, _jobs.Select(j => j.Info()).ToArray());

    private void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void RaiseJobChanged(Guid jobId)
    {
        if (JobChanged is not { } handlers) return;
        var info = Volatile.Read(ref _snapshot).FirstOrDefault(j => j.Id == jobId);
        if (info is null) return;
        foreach (EventHandler<DownloadQueueJobInfo> handler in handlers.GetInvocationList())
        {
            try { handler(this, info); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        Signal();
        if (_worker is { } worker)
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 关闭不阻塞界面；工作循环在此之前已尽力把状态持久化。
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
        _lifetime.Dispose();
        _gate.Dispose();
        _signal.Dispose();
    }

    private readonly record struct JobOutcome(bool Cancelled, DownloadJobState State, string? ErrorCode, string? Message);

    /// <summary>直接转发进度，不经过 <see cref="Progress{T}"/> 的上下文捕获，保证顺序与可预测性。</summary>
    private sealed class JobProgress(Guid jobId, Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
        public Guid JobId => jobId;
    }

    /// <summary>内存中的队列任务。运行时字段（CTS/TCS）不参与持久化。</summary>
    private sealed class Job
    {
        public required Guid Id { get; init; }
        public required Uri Source { get; init; }
        public required string FileName { get; init; }
        public required string TargetPath { get; set; }
        public required DownloadEngine Engine { get; init; }
        public required bool UseAccelerator { get; init; }
        public required long? ExpectedSize { get; init; }
        public required string? ExpectedSha256 { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DownloadJobState State { get; set; }
        public int Attempts { get; set; }
        public long DoneBytes;
        public long TotalBytes;
        public string? ErrorCode { get; set; }
        public string? Message { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public CancellationTokenSource? RunCts { get; set; }
        public TaskCompletionSource? RunCompletion { get; set; }
        public DownloadJobState? PendingState { get; set; }

        public string IdentityKey() => DownloadQueueIdentity.Key(Source, FileName);

        public bool IsTerminal() => State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Canceled;

        public DownloadQueueJobInfo Info() => new()
        {
            Id = Id,
            Source = Source,
            FileName = FileName,
            TargetPath = TargetPath,
            Engine = Engine,
            UseAccelerator = UseAccelerator,
            ExpectedSize = ExpectedSize,
            ExpectedSha256 = ExpectedSha256,
            State = State,
            Attempts = Attempts,
            DoneBytes = DoneBytes,
            TotalBytes = TotalBytes,
            ErrorCode = ErrorCode,
            Message = Message,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };

        public QueueJobDocument Document() => new()
        {
            Id = Id,
            Source = Source.AbsoluteUri,
            FileName = FileName,
            TargetPath = TargetPath,
            Engine = Engine,
            UseAccelerator = UseAccelerator,
            ExpectedSize = ExpectedSize,
            ExpectedSha256 = ExpectedSha256,
            State = State,
            Attempts = Attempts,
            DoneBytes = DoneBytes,
            TotalBytes = TotalBytes,
            ErrorCode = ErrorCode,
            Message = Message,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    /// <summary>
    /// 队列文件根对象。未知字段一律拒绝：把拼错的字段当成默认值会静默丢掉用户的队列状态，
    /// 因此宁可整份文件校验失败并保留原文件。
    /// </summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record QueueDocument
    {
        public required int Version { get; init; }
        public required IReadOnlyList<QueueJobDocument> Jobs { get; init; }
    }

    /// <summary>队列文件中的单个任务。所有字段都是必需的，缺失即视为文件损坏。</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record QueueJobDocument
    {
        public required Guid Id { get; init; }
        public required string Source { get; init; }
        public required string FileName { get; init; }
        public required string TargetPath { get; init; }
        [JsonConverter(typeof(JsonStringEnumConverter<DownloadEngine>))]
        public required DownloadEngine Engine { get; init; }
        public required bool UseAccelerator { get; init; }
        public required long? ExpectedSize { get; init; }
        public required string? ExpectedSha256 { get; init; }
        [JsonConverter(typeof(JsonStringEnumConverter<DownloadJobState>))]
        public required DownloadJobState State { get; init; }
        public required int Attempts { get; init; }
        public required long DoneBytes { get; init; }
        public required long TotalBytes { get; init; }
        public required string? ErrorCode { get; init; }
        public required string? Message { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
    }
}
