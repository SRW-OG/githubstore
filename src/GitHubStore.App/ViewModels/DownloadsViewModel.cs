using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>
/// Downloads into the same persistent, verified package cache used by installation.
/// <para>
/// 除了原有的单次下载，这里还暴露持久化队列（<see cref="DownloadQueueService"/>）的只读视图与逐任务命令。
/// 队列严格串行：<see cref="PackageCacheService"/> 用一个缓存根租约保护下载与安装，
/// 该租约无法表达多个下载并行共享缓存根，因此本视图不提供、也不承诺并行下载。
/// </para>
/// <para>
/// 线程纪律：队列事件由工作线程触发，<see cref="Queue"/> 与所有界面属性只能在界面线程上改动，
/// 因此事件回调统一经过 <see cref="Post"/> 调度到构造时捕获的 <see cref="SynchronizationContext"/>。
/// 本类的 async 方法刻意不使用 <c>ConfigureAwait(false)</c>：调用方（界面）需要在其自身上下文里
/// 继续执行，否则 await 之后的界面状态更新会落到线程池上。
/// </para>
/// <para>
/// 队列服务的生命周期由创建方持有（应用内为 DI 单例，测试中为测试本身），
/// 本视图只订阅其事件，不在 <see cref="Dispose"/> 中释放它。
/// </para>
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadService _service;
    private readonly PreferencesService _preferences;
    private readonly PackageCacheService _packageCache;
    private readonly IDownloadQueueService? _queue;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    private bool _singleDownloading;
    private bool _queueInitialized;

    /// <param name="synchronizationContext">
    /// 用于把队列事件（工作线程触发）调度回界面线程。null 表示捕获构造时的当前上下文；
    /// 在没有上下文的宿主（例如命令行测试）里事件会就地执行。
    /// </param>
    public DownloadsViewModel(IDownloadService service, PreferencesService preferences, LocalizationService text,
        PackageCacheService? packageCache = null, IDownloadQueueService? downloadQueue = null,
        SynchronizationContext? synchronizationContext = null)
    {
        _service = service;
        _preferences = preferences;
        Text = text;
        _packageCache = packageCache ?? new PackageCacheService(
            () => StorageLocations.ResolveCacheDirectory(preferences.Current, preferences.DataDirectory));
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        _queue = downloadQueue;
        if (_queue is not null)
        {
            _queue.JobChanged += OnQueueJobChanged;
            _queue.ProgressChanged += OnQueueProgressChanged;
            RefreshQueue();
        }
    }

    public LocalizationService Text { get; }
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;

    /// <summary>队列任务视图，按入队顺序排列；队列服务未接入时为空。</summary>
    public ObservableCollection<DownloadQueueJobViewModel> Queue { get; } = [];

    /// <summary>队列整体状态（例如队列文件损坏、持久化失败）。</summary>
    [ObservableProperty] private string _queueStatus = string.Empty;

    /// <summary>队列文件损坏等运行期问题的稳定错误码；空表示没有已知问题。</summary>
    [ObservableProperty] private string _queueErrorCode = string.Empty;

    /// <summary>队列中是否至少有一个任务。</summary>
    public bool HasQueueJobs => Queue.Count > 0;

    /// <summary>
    /// 加载并校验队列文件、对账中断任务，然后启动串行工作循环。可重复调用，只有第一次会真正加载。
    /// 队列文件损坏时只报告 <see cref="QueueErrorCode"/>，不会把用户的任务替换成空队列。
    /// </summary>
    public async Task InitializeQueueAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_queue is null || _queueInitialized) return;
        // 不使用 ConfigureAwait(false)：后续界面状态更新必须在调用方（界面）上下文上执行。
        await _queue.InitializeAsync(ct);
        // 只有真正完成后才记为已初始化：中途取消不应让队列永久停在未加载状态。
        // DownloadQueueService.InitializeAsync 自身可重入，并发重复调用不会重复加载。
        _queueInitialized = true;
        if (_disposed) return;
        RefreshQueue();
    }

    /// <summary>
    /// 重新套用本地化文本。切换语言时调用：状态标签等是在事件到达时算好的，
    /// 不刷新就会一直停留在旧语言上。
    /// </summary>
    public void RefreshLocalization() => Post(RefreshQueue);

    /// <summary>把资产加入队列。重复入队不会新建任务，界面会说明命中了既有任务。</summary>
    public async Task<DownloadQueueEnqueueResult?> EnqueueAsync(Asset asset, string? targetPath = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_queue is null)
        {
            QueueStatus = Text["DownloadQueueUnavailable"];
            return null;
        }
        if (!TryCreateRequest(asset, targetPath, out var request, out var invalidStatus))
        {
            QueueStatus = invalidStatus;
            return null;
        }

        var result = await _queue.EnqueueAsync(request, ct);
        if (_disposed) return result;
        RefreshQueue();
        QueueStatus = result.Outcome switch
        {
            // 重复入队必须明确告知，而不是静默地什么都不做。
            DownloadQueueEnqueueOutcome.Enqueued => Text["DownloadQueueEnqueued"] + " · " + asset.Name,
            DownloadQueueEnqueueOutcome.Duplicate => Text["DownloadQueueDuplicate"] + " · " + asset.Name,
            _ => Text[QueueErrorKey(result.ErrorCode)],
        };
        return result;
    }

    private bool TryCreateRequest(Asset asset, string? targetPath, out DownloadQueueRequest request, out string status)
    {
        request = null!;
        status = string.Empty;
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !PackageCacheService.IsSafeFileName(asset.Name))
        {
            status = Text["DownloadInvalidAsset"];
            return false;
        }
        var target = targetPath ?? _packageCache.TryResolvePath(uri, asset.Name);
        if (string.IsNullOrEmpty(target))
        {
            status = Text["DownloadInvalidAsset"];
            return false;
        }
        request = new DownloadQueueRequest
        {
            Source = uri,
            FileName = asset.Name,
            TargetPath = target,
            Engine = _preferences.Current.DownloadEngine,
            UseAccelerator = _preferences.Current.UseAccelerator,
            ExpectedSize = asset.Size > 0 ? asset.Size : null,
            ExpectedSha256 = asset.Digest,
        };
        return true;
    }

    // ------------------------------------------------------------------ 逐任务命令

    /// <summary>暂停任务：停止工作并保留可验证的 HTTP 断点，之后可继续。</summary>
    [RelayCommand]
    private Task PauseJobAsync(DownloadQueueJobViewModel? job) => RunJobCommandAsync(job, DownloadQueueJobAction.Pause);

    /// <summary>继续已暂停的任务：HTTP 引擎按已验证的范围续传。</summary>
    [RelayCommand]
    private Task ResumeJobAsync(DownloadQueueJobViewModel? job) => RunJobCommandAsync(job, DownloadQueueJobAction.Resume);

    /// <summary>
    /// 取消任务。取消是与暂停不同的终态：不会被自动继续，必须显式重试。
    /// </summary>
    [RelayCommand]
    private Task CancelJobAsync(DownloadQueueJobViewModel? job) => RunJobCommandAsync(job, DownloadQueueJobAction.Cancel);

    /// <summary>重试失败或已取消的任务。</summary>
    [RelayCommand]
    private Task RetryJobAsync(DownloadQueueJobViewModel? job) => RunJobCommandAsync(job, DownloadQueueJobAction.Retry);

    /// <summary>移除终态任务的队列记录；不会删除已发布的文件。</summary>
    [RelayCommand]
    private Task RemoveJobAsync(DownloadQueueJobViewModel? job) => RunJobCommandAsync(job, DownloadQueueJobAction.Remove);

    /// <summary>
    /// 逐任务命令的入口。任务展示模型通过它暴露自己的命令，模板无需跨命名域绑定。
    /// 已释放时静默返回而不是抛异常：这些命令由界面绑定触发，抛出的异常无人观察。
    /// </summary>
    internal async Task RunJobCommandAsync(DownloadQueueJobViewModel? job, DownloadQueueJobAction action)
    {
        if (_disposed || _queue is null || job is null) return;
        var result = await ExecuteAsync(action, job.Id);
        if (_disposed) return;
        RefreshQueue();
        QueueStatus = result.Succeeded
            ? Text["DownloadQueueUpdated"] + " · " + job.FileName
            : Text[FailureKey(action)] + " · " + Text[QueueErrorKey(result.ErrorCode)];
    }

    private Task<DownloadQueueCommandResult> ExecuteAsync(DownloadQueueJobAction action, Guid jobId) =>
        action switch
        {
            DownloadQueueJobAction.Pause => _queue!.PauseAsync(jobId),
            DownloadQueueJobAction.Resume => _queue!.ResumeAsync(jobId),
            DownloadQueueJobAction.Cancel => _queue!.CancelAsync(jobId),
            DownloadQueueJobAction.Retry => _queue!.RetryAsync(jobId),
            _ => _queue!.RemoveAsync(jobId),
        };

    private static string FailureKey(DownloadQueueJobAction action) => action switch
    {
        DownloadQueueJobAction.Pause => "DownloadQueuePauseFailed",
        DownloadQueueJobAction.Resume => "DownloadQueueResumeFailed",
        DownloadQueueJobAction.Cancel => "DownloadQueueCancelFailed",
        DownloadQueueJobAction.Retry => "DownloadQueueRetryFailed",
        _ => "DownloadQueueRemoveFailed",
    };

    // ------------------------------------------------------------------ 队列事件与线程调度

    /// <summary>
    /// 把动作调度到构造时捕获的界面上下文。已经在目标上下文上、或宿主没有上下文时就地执行，
    /// 这样界面线程调用不会被推迟，而工作线程事件不会直接改动界面集合。
    /// </summary>
    // VSTHRD001：本类刻意不依赖 WinUI 的 JoinableTaskFactory（否则 ViewModel 就无法被离线测试），
    // 因此这里只能用 SynchronizationContext.Post。Post 是明确的异步投递、不阻塞也不可重入等待，
    // 不存在该规则要防的“同步等待导致死锁”场景；调用方（界面）负责在自己的上下文中继续。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD001",
        Justification = "ViewModel 不依赖 WinUI JoinableTaskFactory 以便离线测试；Post 明确异步投递，不阻塞、不同步等待，无死锁风险。")]
    private void Post(Action action)
    {
        if (_disposed) return;
        var context = _synchronizationContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            action();
            return;
        }
        context.Post(_ =>
        {
            if (_disposed) return;
            action();
        }, null);
    }

    private void OnQueueJobChanged(object? sender, DownloadQueueJobInfo info) => Post(RefreshQueue);

    private void OnQueueProgressChanged(object? sender, DownloadQueueProgress progress) =>
        Post(() =>
        {
            var job = Queue.FirstOrDefault(j => j.Id == progress.JobId);
            job?.ApplyProgress(progress);
        });

    /// <summary>只允许在界面上下文上调用；所有调用点都已满足该前提。</summary>
    private void RefreshQueue()
    {
        if (_disposed || _queue is null) return;
        var jobs = _queue.Jobs;
        // 就地更新以保留选中项与命令绑定，避免每次事件都重建列表导致界面闪烁。
        for (var index = 0; index < jobs.Count; index++)
        {
            var info = jobs[index];
            var existing = Queue.FirstOrDefault(j => j.Id == info.Id);
            if (existing is null)
            {
                Queue.Insert(Math.Min(index, Queue.Count), new DownloadQueueJobViewModel(info, Text, this));
                continue;
            }
            existing.Apply(info, Text);
            var currentIndex = Queue.IndexOf(existing);
            if (currentIndex != index) Queue.Move(currentIndex, index);
        }
        for (var index = Queue.Count - 1; index >= 0; index--)
            if (jobs.All(j => j.Id != Queue[index].Id)) Queue.RemoveAt(index);

        if (_queue.LoadError is not null) QueueStatus = Text["DownloadQueueCorrupt"];
        QueueErrorCode = _queue.LoadError ?? _queue.LastError ?? string.Empty;
        OnPropertyChanged(nameof(HasQueueJobs));
    }

    private static string QueueErrorKey(string? code) => code switch
    {
        DownloadQueueErrorCodes.InvalidRequest => "DownloadQueueInvalidRequest",
        DownloadQueueErrorCodes.JobNotFound => "DownloadQueueJobMissing",
        DownloadQueueErrorCodes.InvalidState => "DownloadQueueInvalidState",
        DownloadQueueErrorCodes.QueueFileInvalid => "DownloadQueueCorrupt",
        DownloadQueueErrorCodes.QueueWriteFailed => "DownloadQueueWriteFailed",
        DownloadQueueErrorCodes.QueueUnavailable => "DownloadQueueUnavailable",
        DownloadQueueErrorCodes.LimitExceeded => "DownloadQueueLimitExceeded",
        DownloadErrorCodes.UnsupportedSource => "DownloadQueueUnsupportedSource",
        DownloadErrorCodes.InvalidVerification => "DownloadQueueInvalidVerification",
        DownloadQueueErrorCodes.VerifyFailed => "DownloadIntegrityFailed",
        DownloadQueueErrorCodes.CacheRecordFailed => "DownloadCacheRecordFailed",
        _ => "DownloadNetworkError",
    };

    // ------------------------------------------------------------------ 单次下载（保留原有行为）

    /// <summary>
    /// 单次下载。这是原有的、非队列路径：调用方必须显式选择它，仅“下载”入口会走队列
    /// （<see cref="EnqueueAsync"/>）。两者互不影响。
    /// </summary>
    public async Task DownloadAsync(Asset asset, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsDownloading) return;
        var snapshot = _preferences.Current;
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !PackageCacheService.IsSafeFileName(asset.Name))
        {
            Status = Text["DownloadInvalidAsset"];
            return;
        }

        _singleDownloading = true;
        IsDownloading = true;
        IsIndeterminate = true;
        Percent = 0;
        OutputPath = string.Empty;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _cancellation = cancellation;
        try
        {
            await using var lease = await _packageCache.AcquireAsync(cancellation.Token);
            var cache = lease.Cache;
            var target = cache.TryResolvePath(uri, asset.Name);
            if (target is null) { Status = Text["DownloadInvalidAsset"]; return; }
            var size = asset.Size > 0 ? asset.Size : (long?)null;
            Status = Text["DownloadCheckingCache"];
            var cached = await cache.FindVerifiedAsync(uri, asset.Name, size, asset.Digest, cancellation.Token);
            if (cached is not null)
            {
                if (_disposed) return;
                OutputPath = cached.Path;
                Percent = 100;
                Status = Text["DownloadUsingCache"] + " · " + OutputPath;
                return;
            }

            await cache.PrepareDownloadAsync(uri, asset.Name, cancellation.Token);
            var request = new DownloadRequest
            {
                Source = uri, TargetPath = target, FileName = asset.Name,
                Engine = snapshot.DownloadEngine, UseAccelerator = snapshot.UseAccelerator,
                ExpectedSize = size, ExpectedSha256 = asset.Digest,
            };
            Status = Text["DownloadInProgress"] + " · " + asset.Name;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (_disposed || !_singleDownloading || cancellation.IsCancellationRequested) return;
                IsIndeterminate = p.Total <= 0;
                Percent = p.Fraction * 100;
                Status = Text["DownloadInProgress"] + " · " + asset.Name + $" · {p.Done / 1048576d:N1} MiB"
                    + (p.Total > 0 ? $" / {p.Total / 1048576d:N1} MiB" : string.Empty);
            });
            var result = _service is ConfiguredDownloadService configured
                ? await configured.DownloadWithSnapshotAsync(request, snapshot, progress, cancellation.Token)
                : await _service.DownloadAsync(request, progress, cancellation.Token);
            if (_disposed) return;
            if (result.Succeeded)
            {
                if (!string.Equals(result.OutputPath, target, StringComparison.OrdinalIgnoreCase))
                { Status = Text["DownloadIntegrityFailed"]; return; }
                try
                {
                    await cache.RecordCompletedAsync(uri, asset.Name, size, asset.Digest, cancellation.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // 文件已下载成功，但无法写入可复用的缓存记录：如实说明，不谎称完成。
                    if (!_disposed) Status = Text["DownloadCacheRecordFailed"] + " · " + target;
                    return;
                }
                if (_disposed) return;
                OutputPath = target;
                Percent = 100;
                Status = Text["DownloadComplete"] + " · " + OutputPath;
            }
            else Status = Text[ErrorKey(result.ErrorCode)];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_disposed) Status = Text["DownloadCancelled"]; }
        catch (InvalidDataException)
        { if (!_disposed) Status = Text["DownloadIntegrityFailed"]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!_disposed) Status = Text["DownloadTargetNotWritable"]; }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            _singleDownloading = false;
            if (!_disposed)
            {
                IsDownloading = false;
                IsIndeterminate = false;
            }
        }
    }

    private static string ErrorKey(string? code) => code switch
    {
        DownloadErrorCodes.Cancelled => "DownloadCancelled",
        DownloadErrorCodes.NoTargetDirectory or DownloadErrorCodes.TargetNotWritable => "DownloadTargetNotWritable",
        DownloadErrorCodes.Aria2NotConfigured or DownloadErrorCodes.Aria2NotFound => "DownloadAria2NotConfigured",
        DownloadErrorCodes.HttpStatusError => "DownloadHttpStatus",
        DownloadErrorCodes.ChecksumMismatch or DownloadErrorCodes.LengthMismatch or DownloadErrorCodes.InvalidResponse
            or DownloadErrorCodes.InvalidVerification => "DownloadIntegrityFailed",
        DownloadErrorCodes.PeerNotSupported => "DownloadPeerPending",
        _ => "DownloadNetworkError",
    };

    /// <summary>取消单次下载。队列任务请使用逐任务的 CancelCommand，两者互不影响。</summary>
    [RelayCommand]
    private Task CancelAsync() => _cancellation?.CancelAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_queue is not null)
        {
            _queue.JobChanged -= OnQueueJobChanged;
            _queue.ProgressChanged -= OnQueueProgressChanged;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>队列任务可以执行的五种逐任务命令。</summary>
public enum DownloadQueueJobAction { Pause, Resume, Cancel, Retry, Remove }

/// <summary>
/// 单个队列任务的展示模型。所有文本均已本地化，状态与错误码直接来自队列服务的稳定快照。
/// <para>
/// 每个任务自己暴露五个命令（<see cref="PauseCommand"/> 等），因此 DataTemplate 只需
/// <c>{Binding PauseCommand}</c> 即可绑定，不必用 ElementName 跨命名域去找父级命令——
/// DataTemplate 拥有独立命名域，那种绑定不会生效。
/// </para>
/// </summary>
public sealed partial class DownloadQueueJobViewModel : ObservableObject
{
    private readonly DownloadsViewModel? _owner;

    public DownloadQueueJobViewModel(DownloadQueueJobInfo info, LocalizationService text, DownloadsViewModel? owner = null)
    {
        _owner = owner;
        Text = text;
        Id = info.Id;
        Apply(info, text);
    }

    public Guid Id { get; }

    /// <summary>本地化服务。模板里的 <c>{Binding Text[Key]}</c> 依赖它。</summary>
    public LocalizationService Text { get; }

    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string _targetPath = string.Empty;
    [ObservableProperty] private DownloadJobState _state;
    [ObservableProperty] private string _stateLabel = string.Empty;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _errorCode = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private int _attempts;

    /// <summary>暂停只在任务尚未结束时可用。</summary>
    public bool CanPause => State is DownloadJobState.Queued or DownloadJobState.Downloading;
    /// <summary>继续只适用于因暂停或中断而停止的任务。</summary>
    public bool CanResume => State is DownloadJobState.Paused;
    /// <summary>取消只适用于尚未结束的任务；已完成的任务不能被“取消”。</summary>
    public bool CanCancel => State is DownloadJobState.Queued or DownloadJobState.Downloading or DownloadJobState.Paused;
    /// <summary>重试只适用于失败或已取消的任务。</summary>
    public bool CanRetry => State is DownloadJobState.Failed or DownloadJobState.Canceled;
    /// <summary>移除只允许终态任务，避免误删正在进行的下载。</summary>
    public bool CanRemove => State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Canceled;

    /// <summary>任务自身携带的命令；未接入父视图时命令不可执行，而不是静默失败。</summary>
    public bool HasCommands => _owner is not null;

    [RelayCommand(CanExecute = nameof(HasCommands))]
    private Task PauseAsync() => ExecuteAsync(DownloadQueueJobAction.Pause);
    [RelayCommand(CanExecute = nameof(HasCommands))]
    private Task ResumeAsync() => ExecuteAsync(DownloadQueueJobAction.Resume);
    [RelayCommand(CanExecute = nameof(HasCommands))]
    private Task CancelAsync() => ExecuteAsync(DownloadQueueJobAction.Cancel);
    [RelayCommand(CanExecute = nameof(HasCommands))]
    private Task RetryAsync() => ExecuteAsync(DownloadQueueJobAction.Retry);
    [RelayCommand(CanExecute = nameof(HasCommands))]
    private Task RemoveAsync() => ExecuteAsync(DownloadQueueJobAction.Remove);

    private Task ExecuteAsync(DownloadQueueJobAction action) =>
        _owner?.RunJobCommandAsync(this, action) ?? Task.CompletedTask;

    internal void Apply(DownloadQueueJobInfo info, LocalizationService text)
    {
        FileName = info.FileName;
        Source = info.Source.AbsoluteUri;
        TargetPath = info.TargetPath;
        State = info.State;
        StateLabel = text[StateKey(info.State)];
        Attempts = info.Attempts;
        Percent = info.TotalBytes > 0
            ? Math.Clamp((double)info.DoneBytes / info.TotalBytes * 100, 0, 100)
            : info.State == DownloadJobState.Completed ? 100 : 0;
        SizeText = DescribeSize(info.DoneBytes, info.TotalBytes);
        ErrorCode = info.ErrorCode ?? string.Empty;
        ErrorText = string.IsNullOrEmpty(info.ErrorCode) ? string.Empty : text[ErrorTextKey(info.ErrorCode)];
        // Message 直接用稳定的队列/下载错误码表达，便于界面按语言渲染且不含原始异常文本。
        Detail = string.IsNullOrEmpty(info.Message) ? string.Empty : text[MessageTextKey(info.Message)];
        RaiseCommandStates();
    }

    internal void ApplyProgress(DownloadQueueProgress progress)
    {
        var total = progress.Total > 0 ? progress.Total : 0;
        Percent = total > 0 ? progress.Fraction * 100 : Percent;
        SizeText = DescribeSize(progress.Done, total);
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
    }

    private static string DescribeSize(long done, long total)
    {
        var doneText = done / 1048576d;
        if (total <= 0) return $"{doneText:N1} MiB";
        return $"{doneText:N1} / {total / 1048576d:N1} MiB · {(int)Math.Clamp((double)done / total * 100, 0, 100)}%";
    }

    internal static string StateKey(DownloadJobState state) => state switch
    {
        DownloadJobState.Queued => "DownloadQueueStateQueued",
        DownloadJobState.Downloading => "DownloadQueueStateDownloading",
        DownloadJobState.Paused => "DownloadQueueStatePaused",
        DownloadJobState.Completed => "DownloadQueueStateCompleted",
        DownloadJobState.Failed => "DownloadQueueStateFailed",
        _ => "DownloadQueueStateCanceled",
    };

    /// <summary>
    /// 队列内部说明键。中断、对账、核验失败等必须与普通下载错误码分开表述，
    /// 否则界面会把“上次运行被中断”说成网络失败。
    /// </summary>
    internal static string MessageTextKey(string message) => message switch
    {
        DownloadQueueErrorCodes.Interrupted => "DownloadQueueInterrupted",
        DownloadQueueErrorCodes.Reconciled => "DownloadQueueReconciled",
        DownloadQueueErrorCodes.VerifyFailed => "DownloadIntegrityFailed",
        DownloadQueueErrorCodes.CacheRecordFailed => "DownloadCacheRecordFailed",
        _ => ErrorTextKey(message),
    };

    internal static string ErrorTextKey(string? code) => code switch
    {
        DownloadErrorCodes.Cancelled => "DownloadCancelled",
        DownloadErrorCodes.NoTargetDirectory or DownloadErrorCodes.TargetNotWritable => "DownloadTargetNotWritable",
        DownloadErrorCodes.Aria2NotConfigured or DownloadErrorCodes.Aria2NotFound => "DownloadAria2NotConfigured",
        DownloadErrorCodes.HttpStatusError => "DownloadHttpStatus",
        DownloadErrorCodes.ChecksumMismatch or DownloadErrorCodes.LengthMismatch or DownloadErrorCodes.InvalidResponse
            or DownloadErrorCodes.InvalidVerification => "DownloadIntegrityFailed",
        DownloadErrorCodes.PeerNotSupported => "DownloadPeerPending",
        DownloadErrorCodes.UnsupportedSource => "DownloadQueueUnsupportedSource",
        DownloadQueueErrorCodes.InvalidRequest => "DownloadQueueInvalidRequest",
        DownloadQueueErrorCodes.LimitExceeded => "DownloadQueueLimitExceeded",
        DownloadQueueErrorCodes.QueueFileInvalid => "DownloadQueueCorrupt",
        DownloadQueueErrorCodes.QueueWriteFailed => "DownloadQueueWriteFailed",
        DownloadQueueErrorCodes.QueueUnavailable => "DownloadQueueUnavailable",
        DownloadQueueErrorCodes.VerifyFailed => "DownloadIntegrityFailed",
        // 未预期失败必须有自己的一句话，不能退化成“网络失败”而误导用户。
        // 该文案键由 lead 在 LocalizationService 中补齐；键缺失时索引器会返回键名本身。
        DownloadQueueErrorCodes.UnexpectedFailure => "DownloadQueueUnexpectedFailure",
        _ => "DownloadNetworkError",
    };
}
