using System.Collections.Concurrent;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 下载队列在界面层的接线：后台事件的线程调度、逐任务命令映射、模板绑定所需的成员、
/// 以及销毁后的回调行为。这里不重复 <see cref="DownloadQueueTests"/> 覆盖的队列语义（持久化、
/// 重启对账、重试边界），只验证 ViewModel 是否把这些语义正确地呈现给界面。
/// </summary>
[Trait("Category", "Unit")]
public sealed class DownloadsQueueViewModelTests
{
    private static readonly Uri Source = new("https://github.com/owner/repo/releases/download/v1/app.zip");
    private const string Name = "app.zip";

    // ------------------------------------------------------------------ 线程调度

    [Fact]
    public async Task BackgroundJobEvent_IsMarshalledToTheInjectedContextInsteadOfRunningInlineAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        // 工作线程触发事件：绝不能就地改动 ObservableCollection。
        await Task.Run(() => queue.RaiseJobChanged(Job(DownloadJobState.Downloading)));

        Assert.Empty(vm.Queue);
        Assert.True(pump.PendingCount > 0);

        pump.Drain();

        var job = Assert.Single(vm.Queue);
        Assert.Equal(Name, job.FileName);
        Assert.Equal(DownloadJobState.Downloading, job.State);
    }

    [Fact]
    public async Task BackgroundProgressEvent_IsMarshalledAndUpdatesOnlyTheMatchingJobAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        var first = Job(DownloadJobState.Downloading, Name);
        var second = Job(DownloadJobState.Queued, "other.zip");
        await Task.Run(() => queue.RaiseJobChanged(first, second));
        pump.Drain();
        Assert.Equal(2, vm.Queue.Count);

        var progress = new DownloadQueueProgress { JobId = second.Id, Done = 512, Total = 1024 };
        await Task.Run(() => queue.RaiseProgress(progress));
        pump.Drain();

        var updated = vm.Queue.Single(j => j.Id == second.Id);
        Assert.Equal(50d, updated.Percent);
        // 其他任务的进度不能被顺手改动。
        Assert.Equal(0d, vm.Queue.Single(j => j.Id == first.Id).Percent);
    }

    [Fact]
    public void QueueEventRaisedOnTheUiContextItself_IsAppliedImmediately()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        var previous = SynchronizationContext.Current;
        try
        {
            // 界面线程自身触发时不应绕道排队，否则命令结果会延迟一拍才可见。
            SynchronizationContext.SetSynchronizationContext(pump);
            queue.RaiseJobChanged(Job(DownloadJobState.Completed));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(0, pump.PendingCount);
        Assert.Equal(DownloadJobState.Completed, Assert.Single(vm.Queue).State);
    }

    [Fact]
    public void ViewModelWithoutAnyQueueService_StaysEmptyAndReportsUnavailable()
    {
        using var directory = new TempDirectory();
        using var vm = CreateViewModel(directory, queue: null, new RecordingSynchronizationContext());

        Assert.Empty(vm.Queue);
        Assert.False(vm.HasQueueJobs);
        Assert.Empty(vm.QueueErrorCode);
    }

    // ------------------------------------------------------------------ 逐任务命令映射

    [Theory]
    [InlineData(DownloadQueueJobAction.Pause)]
    [InlineData(DownloadQueueJobAction.Resume)]
    [InlineData(DownloadQueueJobAction.Cancel)]
    [InlineData(DownloadQueueJobAction.Retry)]
    [InlineData(DownloadQueueJobAction.Remove)]
    public async Task JobCommand_InvokesTheMatchingQueueOperationForThatJobAsync(DownloadQueueJobAction action)
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Paused));

        var job = Assert.Single(vm.Queue);
        await InvokeAsync(job, action);

        // 命令必须落到该任务上，并且只落到该任务上。
        var call = Assert.Single(queue.Calls);
        Assert.Equal(action, call.Action);
        Assert.Equal(job.Id, call.JobId);
    }

    [Fact]
    public async Task EveryJobCommand_ReportsItsOwnLocalizedFailureTextAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue { Result = DownloadQueueCommandResult.Failure(DownloadQueueErrorCodes.InvalidState) };
        using var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Paused));

        var job = Assert.Single(vm.Queue);
        foreach (var action in Enum.GetValues<DownloadQueueJobAction>())
        {
            await InvokeAsync(job, action);

            // 每种命令都要有明确的失败文案，而不是笼统一句“失败”。
            Assert.Contains(ExpectedFailureKey(action), vm.QueueStatus, StringComparison.Ordinal);
            Assert.Contains("不允许", vm.QueueStatus, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SuccessfulJobCommand_RefreshesTheQueueAndReportsItAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Downloading));

        // 命令成功后队列返回新状态，界面必须立刻反映出来。
        queue.Jobs = [Job(DownloadJobState.Paused)];
        await vm.Queue[0].PauseCommand.ExecuteAsync(null);

        Assert.Equal(DownloadJobState.Paused, Assert.Single(vm.Queue).State);
        Assert.Contains("队列已更新", vm.QueueStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullJob_IsIgnoredInsteadOfThrowingAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        await vm.RunJobCommandAsync(null, DownloadQueueJobAction.Pause);

        Assert.Empty(queue.Calls);
    }

    // ------------------------------------------------------------------ 模板绑定所需成员

    [Fact]
    public async Task JobViewModel_ExposesTextSoTemplateBindingsResolveAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Paused));

        var job = Assert.Single(vm.Queue);

        // 模板里写的是 {Binding Text[DownloadQueuePause]}；任务必须自己提供 Text，
        // 否则按钮文字会退化成空字符串。
        Assert.Equal("暂停", job.Text["DownloadQueuePause"]);
        Assert.Equal("已暂停", job.StateLabel);
        Assert.True(job.HasCommands);
    }

    [Fact]
    public async Task JobViewModel_ExposesItsOwnCommandsSoNoCrossNamescopeBindingIsNeededAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Paused));

        var job = Assert.Single(vm.Queue);

        // 五个命令都存在且可执行：DataTemplate 只需 {Binding PauseCommand}。
        Assert.True(job.PauseCommand.CanExecute(null));
        Assert.True(job.ResumeCommand.CanExecute(null));
        Assert.True(job.CancelCommand.CanExecute(null));
        Assert.True(job.RetryCommand.CanExecute(null));
        Assert.True(job.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public void JobViewModelWithoutAnOwner_CannotExecuteCommands()
    {
        // 没有父视图时命令不可执行，而不是静默地什么都不做。
        var job = new DownloadQueueJobViewModel(Job(DownloadJobState.Queued), new LocalizationService("zh-CN"));

        Assert.False(job.HasCommands);
        Assert.False(job.PauseCommand.CanExecute(null));
        Assert.False(job.RemoveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(DownloadJobState.Queued, true, false, true, false, false)]
    [InlineData(DownloadJobState.Downloading, true, false, true, false, false)]
    [InlineData(DownloadJobState.Paused, false, true, true, false, false)]
    [InlineData(DownloadJobState.Completed, false, false, false, false, true)]
    [InlineData(DownloadJobState.Failed, false, false, false, true, true)]
    [InlineData(DownloadJobState.Canceled, false, false, false, true, true)]
    public void CommandAvailability_MatchesTheJobState(
        DownloadJobState state, bool pause, bool resume, bool cancel, bool retry, bool remove)
    {
        var job = new DownloadQueueJobViewModel(Job(state), new LocalizationService("zh-CN"));

        // 暂停与取消的区别必须体现在按钮可用性上：已取消的任务只能重试或移除。
        Assert.Equal(pause, job.CanPause);
        Assert.Equal(resume, job.CanResume);
        Assert.Equal(cancel, job.CanCancel);
        Assert.Equal(retry, job.CanRetry);
        Assert.Equal(remove, job.CanRemove);
    }

    [Fact]
    public async Task JobViewModel_ShowsStableErrorAndDetailTextWithoutRawExceptionsAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        var failed = Job(DownloadJobState.Failed, digest: "sha256:" + new string('a', 64)) with
        {
            ErrorCode = DownloadErrorCodes.ChecksumMismatch,
            Message = DownloadQueueErrorCodes.VerifyFailed,
        };
        await SeedQueueAsync(vm, queue, pump, failed);

        var job = Assert.Single(vm.Queue);
        Assert.Equal(DownloadErrorCodes.ChecksumMismatch, job.ErrorCode);
        // 断言取自本地化资源本身，而不是随手摘的中文片段：这样文案调整不会让测试变成假绿或误红。
        var chinese = new LocalizationService("zh-CN");
        Assert.Equal(chinese["DownloadIntegrityFailed"], job.ErrorText);
        // 说明文字与错误文字分开呈现：VerifyFailed 映射到同一句完整性文案。
        Assert.Equal(chinese["DownloadIntegrityFailed"], job.Detail);
    }

    // ------------------------------------------------------------------ 语言切换

    [Fact]
    public async Task RefreshLocalization_RecomputesAlreadyRenderedLabelsAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        var text = new LocalizationService("zh-CN");
        using var vm = CreateViewModel(directory, queue, pump, text);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Paused));
        Assert.Equal("已暂停", Assert.Single(vm.Queue).StateLabel);

        text.ApplyLanguage("en-US");
        vm.RefreshLocalization();
        pump.Drain();

        // 状态标签是事件到达时算好的；不刷新就会一直停留在旧语言。
        Assert.Equal("Paused", Assert.Single(vm.Queue).StateLabel);
    }

    // ------------------------------------------------------------------ 入队

    [Fact]
    public async Task Enqueue_ReportsDuplicateInsteadOfSilentlyDoingNothingAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue
        {
            EnqueueResult = DownloadQueueEnqueueResult.Duplicate(Job(DownloadJobState.Queued)),
        };
        using var vm = CreateViewModel(directory, queue, pump);

        var result = await vm.EnqueueAsync(Asset(directory));

        Assert.NotNull(result);
        Assert.Equal(DownloadQueueEnqueueOutcome.Duplicate, result!.Outcome);
        Assert.Contains("已在队列中", vm.QueueStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enqueue_RejectsInvalidAssetsBeforeReachingTheQueueAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        var result = await vm.EnqueueAsync(Asset(directory) with { DownloadUrl = "http://example.invalid/app.zip" });

        Assert.Null(result);
        Assert.Empty(queue.Enqueued);
        Assert.Contains("无效", vm.QueueStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeQueue_LoadsOnlyOnceAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        using var vm = CreateViewModel(directory, queue, pump);

        await vm.InitializeQueueAsync();
        await vm.InitializeQueueAsync();

        // 重复初始化不应重复读取队列文件。
        Assert.Equal(1, queue.InitializeCalls);
    }

    [Fact]
    public async Task InitializeQueue_SurfacesACorruptQueueFileWithoutLosingItAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue { LoadError = DownloadQueueErrorCodes.QueueFileInvalid };
        using var vm = CreateViewModel(directory, queue, pump);

        await vm.InitializeQueueAsync();

        Assert.Equal(DownloadQueueErrorCodes.QueueFileInvalid, vm.QueueErrorCode);
        Assert.Contains("损坏", vm.QueueStatus, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 销毁

    [Fact]
    public async Task AfterDispose_JobCommandsDoNotThrowOrReachTheQueueAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Paused));
        var job = Assert.Single(vm.Queue);

        vm.Dispose();

        // 界面绑定可能在销毁后仍触发命令；这里必须安静返回，而不是抛出无人观察的异常。
        foreach (var action in Enum.GetValues<DownloadQueueJobAction>())
            await InvokeAsync(job, action);
        Assert.Empty(queue.Calls);
    }

    [Fact]
    public async Task AfterDispose_BackgroundEventsAreDroppedWithoutTouchingTheCollectionAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        var vm = CreateViewModel(directory, queue, pump);
        await SeedQueueAsync(vm, queue, pump, Job(DownloadJobState.Downloading));
        var before = vm.Queue.Count;

        vm.Dispose();
        // 事件在销毁后仍可能已经在途：既不能抛异常，也不能再改动已释放视图的状态。
        queue.RaiseJobChanged(Job(DownloadJobState.Completed));
        pump.Drain();
        await Task.Run(() => queue.RaiseJobChanged(Job(DownloadJobState.Failed)));
        pump.Drain();

        Assert.Equal(before, vm.Queue.Count);
    }

    [Fact]
    public void Dispose_UnsubscribesFromQueueEvents()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        var vm = CreateViewModel(directory, queue, pump);

        vm.Dispose();

        // 取消订阅后队列不应再持有该视图的任何回调。
        Assert.Equal(0, queue.JobChangedSubscribers);
        Assert.Equal(0, queue.ProgressChangedSubscribers);
    }

    [Fact]
    public async Task AfterDispose_EnqueueIsRejectedWithoutTouchingTheQueueAsync()
    {
        using var directory = new TempDirectory();
        var pump = new RecordingSynchronizationContext();
        var queue = new FakeQueue();
        var vm = CreateViewModel(directory, queue, pump);
        vm.Dispose();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => vm.EnqueueAsync(Asset(directory)));
        Assert.Empty(queue.Enqueued);
    }

    // ------------------------------------------------------------------ 测试基础设施

    private static DownloadsViewModel CreateViewModel(TempDirectory directory, IDownloadQueueService? queue,
        RecordingSynchronizationContext pump, LocalizationService? text = null) =>
        new(new NoopDownload(), new PreferencesService(Path.Combine(directory.DirectoryPath, "settings.json")),
            text ?? new LocalizationService("zh-CN"), downloadQueue: queue, synchronizationContext: pump);

    /// <summary>把队列任务播种进视图，并同步等待可能的调度回调。</summary>
    private static async Task SeedQueueAsync(DownloadsViewModel vm, FakeQueue queue,
        RecordingSynchronizationContext pump, params DownloadQueueJobInfo[] jobs)
    {
        queue.Jobs = jobs;
        await Task.Run(() => queue.RaiseJobChanged(jobs));
        pump.Drain();
    }

    private static Task InvokeAsync(DownloadQueueJobViewModel job, DownloadQueueJobAction action) => action switch
    {
        DownloadQueueJobAction.Pause => job.PauseCommand.ExecuteAsync(null),
        DownloadQueueJobAction.Resume => job.ResumeCommand.ExecuteAsync(null),
        DownloadQueueJobAction.Cancel => job.CancelCommand.ExecuteAsync(null),
        DownloadQueueJobAction.Retry => job.RetryCommand.ExecuteAsync(null),
        _ => job.RemoveCommand.ExecuteAsync(null),
    };

    private static string ExpectedFailureKey(DownloadQueueJobAction action) => action switch
    {
        DownloadQueueJobAction.Pause => "暂停失败",
        DownloadQueueJobAction.Resume => "继续失败",
        DownloadQueueJobAction.Cancel => "取消失败",
        DownloadQueueJobAction.Retry => "重试失败",
        _ => "移除失败",
    };

    private static Asset Asset(TempDirectory directory) => new()
    {
        InstallerId = "fixture", ReleaseKey = "stable:1", Name = Name, Size = 6,
        DownloadUrl = Source.AbsoluteUri, Digest = "sha256:" + new string('a', 64),
    };

    private static DownloadQueueJobInfo Job(DownloadJobState state, string? name = null, string? digest = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DownloadQueueJobInfo
        {
            Id = Guid.NewGuid(),
            Source = Source,
            FileName = name ?? Name,
            TargetPath = Path.Combine(Path.GetTempPath(), name ?? Name),
            Engine = DownloadEngine.Http,
            ExpectedSize = 6,
            ExpectedSha256 = digest,
            State = state,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// 记录而不立即执行的回调队列，用来证明后台事件确实被调度到了界面上下文，
    /// 而不是在触发线程上就地执行。
    /// </summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public int PendingCount => _pending.Count;

        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>在调用线程上执行所有已排队的回调。</summary>
        public void Drain()
        {
            while (_pending.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    /// <summary>记录调用的队列替身；事件由测试显式触发，绝不访问网络或磁盘。</summary>
    private sealed class FakeQueue : IDownloadQueueService
    {
        public string QueueFilePath { get; } = Path.Combine(Path.GetTempPath(), "fake-queue.json");
        public string? LoadError { get; set; }
        public string? LastError { get; set; }
        public IReadOnlyList<DownloadQueueJobInfo> Jobs { get; set; } = [];
        public int InitializeCalls { get; private set; }
        public DownloadQueueCommandResult Result { get; set; } = DownloadQueueCommandResult.Ok();
        public DownloadQueueEnqueueResult EnqueueResult { get; set; } =
            DownloadQueueEnqueueResult.Enqueued(Job(DownloadJobState.Queued));
        public List<(DownloadQueueJobAction Action, Guid JobId)> Calls { get; } = [];
        public List<DownloadQueueRequest> Enqueued { get; } = [];

        public int JobChangedSubscribers { get; private set; }
        public int ProgressChangedSubscribers { get; private set; }

        // 自定义访问器的 event 不能在本类内直接 Invoke；显式保存委托字段再触发。
        private EventHandler<DownloadQueueJobInfo>? _jobChangedHandler;
        private EventHandler<DownloadQueueProgress>? _progressChangedHandler;

        public event EventHandler<DownloadQueueJobInfo>? JobChanged
        {
            add { _jobChangedHandler += value; JobChangedSubscribers++; }
            remove { _jobChangedHandler -= value; JobChangedSubscribers--; }
        }

        public event EventHandler<DownloadQueueProgress>? ProgressChanged
        {
            add { _progressChangedHandler += value; ProgressChangedSubscribers++; }
            remove { _progressChangedHandler -= value; ProgressChangedSubscribers--; }
        }

        /// <summary>
        /// 先更新 <see cref="Jobs"/> 再触发事件：ViewModel 的 RefreshQueue 读的是 Jobs，
        /// 事件参数只是“有变化”的通知。只触发事件而不改状态会让替身与真实服务行为不一致。
        /// </summary>
        public void RaiseJobChanged(params DownloadQueueJobInfo[] jobs)
        {
            Jobs = jobs;
            var handler = _jobChangedHandler;
            if (handler is null) return;
            foreach (var job in jobs) handler(this, job);
        }

        public void RaiseProgress(DownloadQueueProgress progress) => _progressChangedHandler?.Invoke(this, progress);

        public Task InitializeAsync(CancellationToken ct = default)
        {
            InitializeCalls++;
            return Task.CompletedTask;
        }

        public Task<DownloadQueueEnqueueResult> EnqueueAsync(DownloadQueueRequest request, CancellationToken ct = default)
        {
            Enqueued.Add(request);
            return Task.FromResult(EnqueueResult);
        }

        public Task<DownloadQueueCommandResult> PauseAsync(Guid jobId, CancellationToken ct = default) =>
            RecordAsync(DownloadQueueJobAction.Pause, jobId);

        public Task<DownloadQueueCommandResult> ResumeAsync(Guid jobId, CancellationToken ct = default) =>
            RecordAsync(DownloadQueueJobAction.Resume, jobId);

        public Task<DownloadQueueCommandResult> CancelAsync(Guid jobId, CancellationToken ct = default) =>
            RecordAsync(DownloadQueueJobAction.Cancel, jobId);

        public Task<DownloadQueueCommandResult> RetryAsync(Guid jobId, CancellationToken ct = default) =>
            RecordAsync(DownloadQueueJobAction.Retry, jobId);

        public Task<DownloadQueueCommandResult> RemoveAsync(Guid jobId, CancellationToken ct = default) =>
            RecordAsync(DownloadQueueJobAction.Remove, jobId);

        private Task<DownloadQueueCommandResult> RecordAsync(DownloadQueueJobAction action, Guid jobId)
        {
            lock (Calls) Calls.Add((action, jobId));
            return Task.FromResult(Result);
        }
    }

    /// <summary>单次下载路径不参与这些测试；被调用即视为意外。</summary>
    private sealed class NoopDownload : IDownloadService
    {
        public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);
        public Task<DownloadResult> DownloadAsync(DownloadRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("These tests must not run a real download.");
    }
}
