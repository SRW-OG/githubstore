using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHubStore.App.Services.Download;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 持久化下载队列。所有网络响应都由内存 handler 产生；测试绝不访问真实网络。
/// <para>
/// 这里刻意不重复 <see cref="DownloadRegressionTests"/> 已经覆盖的 HTTP 续传细节（206 校验、
/// If-Range、416 恢复等）。本文件只验证“队列如何使用那条已验证的续传路径”：
/// 状态机、持久化、重启对账、去重、重试边界与产物核验。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class DownloadQueueTests
{
    private static readonly Uri Source = new("https://github.com/owner/repo/releases/download/v1/app.zip");
    private const string Name = "app.zip";
    private const string Payload = "abcdef";

    // ------------------------------------------------------------------ 入队与去重

    [Fact]
    public async Task Enqueue_CreatesJobAndReportsItToTheCaller()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);

        var result = await queue.EnqueueAsync(Request(directory));

        Assert.Equal(DownloadQueueEnqueueOutcome.Enqueued, result.Outcome);
        Assert.NotEqual(Guid.Empty, result.JobId);
        // 校验入队返回的快照：后台工作循环可能已经把它推进到 Downloading，因此不能断言实时状态。
        Assert.Equal(DownloadJobState.Queued, result.Job!.State);
        Assert.Equal(Source.AbsoluteUri, result.Job.Source.AbsoluteUri);
        Assert.Equal(Name, result.Job.FileName);
        Assert.Equal(directory.Combine(Name), result.Job.TargetPath);
        Assert.Equal(DownloadEngine.Http, result.Job.Engine);
        Assert.Single(queue.Jobs);
        Assert.Null(queue.LoadError);
    }

    [Fact]
    public async Task Enqueue_DuplicateIdentity_DoesNotCreateASecondJobAndTellsTheCaller()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);

        var first = await queue.EnqueueAsync(Request(directory));
        var second = await queue.EnqueueAsync(Request(directory));

        // 重复入队不是“静默什么都不做”：必须返回 Duplicate 并指向既有任务。
        Assert.Equal(DownloadQueueEnqueueOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.Accepted);
        Assert.Single(queue.Jobs);
    }

    [Theory]
    [InlineData("APP.ZIP")]
    [InlineData("App.Zip")]
    public async Task Enqueue_IdentityIsCaseInsensitiveForTheFileName(string duplicateName)
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);

        await queue.EnqueueAsync(Request(directory));
        var duplicate = await queue.EnqueueAsync(Request(directory) with { FileName = duplicateName });

        Assert.Equal(DownloadQueueEnqueueOutcome.Duplicate, duplicate.Outcome);
        Assert.Single(queue.Jobs);
    }

    [Fact]
    public async Task Enqueue_DifferentNamesOrSources_AreDistinctJobs()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);

        await queue.EnqueueAsync(Request(directory));
        var otherName = await queue.EnqueueAsync(Request(directory) with { FileName = "other.zip" });
        var otherSource = await queue.EnqueueAsync(Request(directory) with { Source = new Uri("https://example.invalid/app.zip") });

        Assert.Equal(DownloadQueueEnqueueOutcome.Enqueued, otherName.Outcome);
        Assert.Equal(DownloadQueueEnqueueOutcome.Enqueued, otherSource.Outcome);
        Assert.Equal(3, queue.Jobs.Count);
    }

    [Fact]
    public async Task Enqueue_RejectsUnsafeInputsBeforeAnythingIsWritten()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);
        var credentialSource = new UriBuilder(Source) { UserName = "fixture-user", Password = "fixture-password" }.Uri;

        var relative = await queue.EnqueueAsync(Request(directory) with { TargetPath = "app.zip" });
        var devicePath = await queue.EnqueueAsync(Request(directory) with { TargetPath = @"\\.\pipe\app.zip" });
        var http = await queue.EnqueueAsync(Request(directory) with { Source = new Uri("http://github.com/owner/app.zip") });
        var credentials = await queue.EnqueueAsync(Request(directory) with { Source = credentialSource });
        var unsafeName = await queue.EnqueueAsync(Request(directory) with { FileName = "../escape.zip" });
        var badDigest = await queue.EnqueueAsync(Request(directory) with { ExpectedSha256 = "md5:abc" });

        Assert.Equal(DownloadQueueErrorCodes.InvalidRequest, relative.ErrorCode);
        Assert.Equal(DownloadQueueErrorCodes.InvalidRequest, devicePath.ErrorCode);
        Assert.Equal(DownloadErrorCodes.UnsupportedSource, http.ErrorCode);
        Assert.Equal(DownloadErrorCodes.UnsupportedSource, credentials.ErrorCode);
        Assert.Equal(DownloadQueueErrorCodes.InvalidRequest, unsafeName.ErrorCode);
        Assert.Equal(DownloadErrorCodes.InvalidVerification, badDigest.ErrorCode);
        Assert.Empty(queue.Jobs);
        Assert.False(File.Exists(queue.QueueFilePath));
    }

    [Fact]
    public async Task Enqueue_RejectsFilesBeyondTheConfiguredSizeAndCountLimits()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory, options: o => o with { MaximumFileBytes = 100, MaximumJobs = 2 });

        var oversized = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 1_000 });
        Assert.Equal(DownloadQueueErrorCodes.LimitExceeded, oversized.ErrorCode);

        await queue.EnqueueAsync(Request(directory) with { FileName = "a.zip" });
        await queue.EnqueueAsync(Request(directory) with { FileName = "b.zip" });
        var overflow = await queue.EnqueueAsync(Request(directory) with { FileName = "c.zip" });

        Assert.Equal(DownloadQueueErrorCodes.LimitExceeded, overflow.ErrorCode);
        Assert.Equal(2, queue.Jobs.Count);
    }

    // ------------------------------------------------------------------ 串行顺序

    [Fact]
    public async Task Queue_RunsJobsStrictlyOneAtATimeInEnqueueOrder()
    {
        using var directory = new TempDirectory();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedDownload(async (request, call, ct) =>
        {
            if (call == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
            return WriteTarget(request, Payload);
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        await queue.EnqueueAsync(Request(directory) with { FileName = "one.zip" });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.EnqueueAsync(Request(directory) with { FileName = "two.zip" });
        await queue.EnqueueAsync(Request(directory) with { FileName = "three.zip" });

        // 第一个任务仍被阻塞时，后续任务绝不能开始下载。
        Assert.Equal(1, fake.Calls);
        releaseFirst.SetResult();
        await WaitForStateAsync(queue, DownloadJobState.Completed);

        Assert.Equal(["one.zip", "two.zip", "three.zip"], fake.Order);
        Assert.Equal(3, fake.Calls);
    }

    // ------------------------------------------------------------------ 暂停与取消

    [Fact]
    public async Task Pause_PreservesTheValidatedPartialAndIsResumable()
    {
        using var directory = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedDownload(async (request, call, ct) =>
        {
            if (call == 1)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.TargetPath)!);
                // 模拟 HttpDownloadService 在取消时保留的可验证断点：.part 与原子 sidecar。
                await File.WriteAllTextAsync(request.TargetPath + ".part", "abc", ct);
                await File.WriteAllTextAsync(request.TargetPath + ".part.json", Sidecar(6), ct);
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return WriteTarget(request, Payload);
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var paused = await queue.PauseAsync(enqueued.JobId);

        Assert.True(paused.Succeeded, paused.ErrorCode);
        Assert.Equal(DownloadJobState.Paused, paused.Job!.State);
        // 暂停必须保留可续传的部分数据，而不是丢弃它。
        Assert.Equal("abc", await File.ReadAllTextAsync(directory.Combine("app.zip.part")));
        Assert.True(File.Exists(directory.Combine("app.zip.part.json")));

        var resumed = await queue.ResumeAsync(enqueued.JobId);

        Assert.True(resumed.Succeeded, resumed.ErrorCode);
        await WaitForStateAsync(queue, DownloadJobState.Completed);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task Cancel_IsATerminalStateDistinctFromPause()
    {
        using var directory = new TempDirectory();
        // 用闸门让两个任务都停在“进行中/排队中”，否则瞬时的替身会在命令到达前就跑完。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new[] { true };
        var fake = new ScriptedDownload(async (request, _, ct) =>
        {
            if (Volatile.Read(ref blocking[0])) await gate.Task.WaitAsync(ct);
            return WriteTarget(request, Payload);
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        var paused = await queue.EnqueueAsync(Request(directory) with { FileName = "paused.zip" });
        await fake.WaitForCallsAsync(1);
        var canceled = await queue.EnqueueAsync(Request(directory) with { FileName = "canceled.zip" });

        var pauseResult = await queue.PauseAsync(paused.JobId);
        await fake.WaitForCallsAsync(2);
        var cancelResult = await queue.CancelAsync(canceled.JobId);

        Assert.Equal(DownloadJobState.Paused, pauseResult.Job!.State);
        Assert.Equal(DownloadJobState.Canceled, cancelResult.Job!.State);
        Assert.NotEqual(pauseResult.Job.State, cancelResult.Job.State);
        Assert.Equal(DownloadErrorCodes.Cancelled, cancelResult.Job.ErrorCode);
        // 取消是终态：不自动继续，只能显式重试；暂停则可以直接继续。
        Assert.True(cancelResult.Job.IsTerminal);
        Assert.False(pauseResult.Job.IsTerminal);
        Assert.Equal(DownloadQueueErrorCodes.InvalidState, (await queue.ResumeAsync(canceled.JobId)).ErrorCode);

        Volatile.Write(ref blocking[0], false);
        gate.SetResult();
        Assert.True((await queue.ResumeAsync(paused.JobId)).Succeeded);
        Assert.True((await queue.RetryAsync(canceled.JobId)).Succeeded);
        await WaitForStateAsync(queue, DownloadJobState.Completed);
    }

    [Fact]
    public async Task Cancel_WhileDownloading_StopsWorkAndNeverClaimsCompletion()
    {
        using var directory = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedDownload(async (request, call, ct) =>
        {
            if (call == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return WriteTarget(request, Payload);
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await queue.CancelAsync(enqueued.JobId);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(DownloadJobState.Canceled, result.Job!.State);
        Assert.False(File.Exists(directory.Combine("app.zip")));
        Assert.Equal(1, fake.Calls);
    }

    // ------------------------------------------------------------------ 重启恢复

    [Fact]
    public async Task Restart_InterruptedJobBecomesPausedAndKeepsItsPartial()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        await File.WriteAllTextAsync(target + ".part", "abc");
        await File.WriteAllTextAsync(target + ".part.json", Sidecar(6));
        WriteQueueFile(directory, Job(target, DownloadJobState.Downloading, attempts: 2, digest: Hash(Payload)));

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        var job = Assert.Single(queue.Jobs);
        // 既不是 Completed，也没有被静默重试或丢弃。
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.Equal(DownloadQueueErrorCodes.Interrupted, job.Message);
        Assert.Equal(0, job.Attempts);
        Assert.Equal("abc", await File.ReadAllTextAsync(target + ".part"));
        Assert.True(File.Exists(target + ".part.json"));
    }

    [Fact]
    public async Task Restart_InterruptedJobWithoutAnyEvidenceNeverClaimsCompletion()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        // 没有已知大小也没有摘要：一个同名文件不能证明它就是本次下载的产物。
        WriteQueueFile(directory, Job(target, DownloadJobState.Downloading, expectedSize: null, digest: null));
        await File.WriteAllTextAsync(target, "someone else's file");

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        Assert.Equal(DownloadJobState.Paused, Assert.Single(queue.Jobs).State);
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Restart_VerifiesThePublishedFileAndReconcilesAnUnmarkedCompletion()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        await File.WriteAllTextAsync(target, Payload);
        WriteQueueFile(directory, Job(target, DownloadJobState.Downloading, attempts: 1, digest: Hash(Payload)));

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(DownloadQueueErrorCodes.Reconciled, job.Message);
    }

    [Fact]
    public async Task Restart_UnmarkedCompletionWithAMismatchedHashStaysPaused()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        await File.WriteAllTextAsync(target, "wrong bytes");
        WriteQueueFile(directory, Job(target, DownloadJobState.Downloading, digest: Hash(Payload)));

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        // 核验不通过就不是完成；回落为 Paused 而不是被当成成功或直接判失败。
        Assert.Equal(DownloadJobState.Paused, Assert.Single(queue.Jobs).State);
    }

    [Fact]
    public async Task Restart_CompletedJobWhoseFileIsGone_IsNoLongerClaimedAsCompleted()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        await File.WriteAllTextAsync(target, Payload);
        WriteQueueFile(directory, Job(target, DownloadJobState.Completed, digest: Hash(Payload)));

        await using (var queue = CreateQueue(directory))
        {
            await queue.InitializeAsync();
            Assert.Equal(DownloadJobState.Completed, Assert.Single(queue.Jobs).State);
        }

        // 产物被删除后，已完成这一说法不再成立。
        File.Delete(target);
        await using var reopened = CreateQueue(directory);
        await reopened.InitializeAsync();

        var job = Assert.Single(reopened.Jobs);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Equal(DownloadErrorCodes.LengthMismatch, job.ErrorCode);
    }

    [Fact]
    public async Task Restart_TamperedCompletedFileIsDemotedToFailed()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        // 只用摘要约束（expectedSize 为 null），这样能真正走到 SHA-256 比对而不是先被长度检查拦下。
        WriteQueueFile(directory, Job(target, DownloadJobState.Completed, expectedSize: null, digest: Hash(Payload)));
        await File.WriteAllTextAsync(target, "tampered content");

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Equal(DownloadErrorCodes.ChecksumMismatch, job.ErrorCode);
    }

    [Fact]
    public async Task Restart_ResumeContinuesFromTheValidatedHttpRange()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        await File.WriteAllTextAsync(target + ".part", "abc");
        await File.WriteAllTextAsync(target + ".part.json", Sidecar(6));
        WriteQueueFile(directory, Job(target, DownloadJobState.Downloading, expectedSize: 6, digest: Hash(Payload)));

        using var client = Client(message =>
        {
            // 继续必须走已验证的范围路径，而不是从零重下。
            Assert.Equal(3, message.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"v1\"", message.Headers.IfRange!.EntityTag!.ToString());
            var response = Body("def", HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        await using var queue = CreateQueue(directory, downloads: new HttpDownloadService(client));
        await queue.InitializeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Paused, job.State);

        var resumed = await queue.ResumeAsync(job.Id);

        Assert.True(resumed.Succeeded, resumed.ErrorCode);
        await WaitForStateAsync(queue, DownloadJobState.Completed);
        Assert.Equal(Payload, await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }

    // ------------------------------------------------------------------ 损坏的队列文件

    [Fact]
    public async Task CorruptQueueFile_IsReportedAndNeverReplacedWithAnEmptyQueue()
    {
        using var directory = new TempDirectory();
        var queueFile = directory.Combine("download-queue.json");
        const string original = "{ this is not valid json";
        await File.WriteAllTextAsync(queueFile, original);

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        Assert.Equal(DownloadQueueErrorCodes.QueueFileInvalid, queue.LoadError);
        Assert.Empty(queue.Jobs);
        // 原始文件必须原封不动：用户的任务不能因为一次读取失败而消失。
        Assert.Equal(original, await File.ReadAllTextAsync(queueFile));
        // 锁定期间拒绝一切变更，避免用空队列覆盖磁盘上的真实队列。
        var rejected = await queue.EnqueueAsync(Request(directory));
        Assert.Equal(DownloadQueueEnqueueOutcome.Rejected, rejected.Outcome);
        Assert.Equal(DownloadQueueErrorCodes.QueueUnavailable, rejected.ErrorCode);
        Assert.Equal(DownloadQueueErrorCodes.QueueUnavailable, (await queue.PauseAsync(Guid.NewGuid())).ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(queueFile));
    }

    [Theory]
    // 未知字段：拼写错误不能被当成默认值。
    [InlineData("{\"version\":1,\"jobs\":[],\"unexpected\":true}")]
    // 不支持的格式版本。
    [InlineData("{\"version\":99,\"jobs\":[]}")]
    // 缺少必需字段的任务。
    [InlineData("{\"version\":1,\"jobs\":[{\"id\":\"11111111-1111-1111-1111-111111111111\"}]}")]
    // 任务里出现未知字段。
    [InlineData("{\"version\":1,\"jobs\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"surprise\":1}]}")]
    public async Task QueueFile_UnknownMissingOrUnsupportedContentLocksTheQueue(string content)
    {
        using var directory = new TempDirectory();
        var queueFile = directory.Combine("download-queue.json");
        await File.WriteAllTextAsync(queueFile, content);

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        Assert.Equal(DownloadQueueErrorCodes.QueueFileInvalid, queue.LoadError);
        Assert.Empty(queue.Jobs);
        Assert.Equal(content, await File.ReadAllTextAsync(queueFile));
    }

    [Theory]
    // 非 HTTPS 来源。
    [InlineData("http://example.invalid/app.zip")]
    // URL 内嵌凭据。
    [InlineData("https://user:secret@example.invalid/app.zip")]
    // 设备路径目标。
    [InlineData(@"\\.\pipe\app.zip")]
    public async Task QueueFile_UnsafeSourceOrTargetPathLocksTheQueue(string sourceOrTarget)
    {
        using var directory = new TempDirectory();
        var queueFile = directory.Combine("download-queue.json");
        var isSource = sourceOrTarget.Contains("://", StringComparison.Ordinal);
        var source = isSource ? sourceOrTarget : Source.AbsoluteUri;
        var target = isSource ? directory.Combine(Name) : sourceOrTarget;
        File.WriteAllText(queueFile, QueueJson(source, target));

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        // 来源与目标路径在加载时必须和入队时用同一套规则校验；不安全的记录整份拒绝。
        Assert.Equal(DownloadQueueErrorCodes.QueueFileInvalid, queue.LoadError);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task QueueFile_DuplicateIdentitiesAreTreatedAsCorruptInsteadOfDroppedSilently()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine(Name);
        var first = Job(target, DownloadJobState.Queued);
        var second = Job(target, DownloadJobState.Queued);
        var json = new StringBuilder("{\"version\":1,\"jobs\":[")
            .Append(JobJson(first)).Append(',').Append(JobJson(second)).Append("]}").ToString();
        File.WriteAllText(directory.Combine("download-queue.json"), json);

        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        Assert.Equal(DownloadQueueErrorCodes.QueueFileInvalid, queue.LoadError);
        Assert.Empty(queue.Jobs);
    }

    // ------------------------------------------------------------------ 有界重试

    [Fact]
    public async Task TransientFailures_AreRetriedWithABoundedBudget()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((_, _, _) =>
            Task.FromResult(DownloadResult.Failure(DownloadErrorCodes.NetworkError, Source)));
        await using var queue = CreateQueue(directory, downloads: fake, options: o => o with { MaximumAttempts = 3 });

        var enqueued = await queue.EnqueueAsync(Request(directory));
        await WaitForStateAsync(queue, DownloadJobState.Failed);

        Assert.Equal(3, fake.Calls);
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadErrorCodes.NetworkError, job.ErrorCode);
        Assert.Equal(3, job.Attempts);
    }

    [Fact]
    public async Task TransientFailureThatRecovers_CompletesWithinTheBudget()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((request, call, _) => Task.FromResult(call < 3
            ? DownloadResult.Failure(DownloadErrorCodes.NetworkError, Source)
            : WriteTarget(request, Payload)));
        await using var queue = CreateQueue(directory, downloads: fake, options: o => o with { MaximumAttempts = 3 });

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6 });
        await WaitForStateAsync(queue, DownloadJobState.Completed);

        Assert.Equal(3, fake.Calls);
        Assert.Equal(Payload, await File.ReadAllTextAsync(directory.Combine("app.zip")));
    }

    [Fact]
    public async Task MaximumAttemptsOfOne_MeansASingleTry()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((_, _, _) =>
            Task.FromResult(DownloadResult.Failure(DownloadErrorCodes.NetworkError, Source)));
        await using var queue = CreateQueue(directory, downloads: fake, options: o => o with { MaximumAttempts = 1 });

        await queue.EnqueueAsync(Request(directory));
        await WaitForStateAsync(queue, DownloadJobState.Failed);

        Assert.Equal(1, fake.Calls);
    }

    [Theory]
    [InlineData(DownloadErrorCodes.UnsupportedSource)]
    [InlineData(DownloadErrorCodes.InvalidVerification)]
    [InlineData(DownloadErrorCodes.TargetNotWritable)]
    [InlineData(DownloadErrorCodes.PeerNotSupported)]
    [InlineData(DownloadErrorCodes.Aria2NotFound)]
    [InlineData(DownloadErrorCodes.LengthMismatch)]
    public async Task DeterministicFailures_AreNeverRetried(string errorCode)
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((_, _, _) => Task.FromResult(DownloadResult.Failure(errorCode, Source)));
        await using var queue = CreateQueue(directory, downloads: fake, options: o => o with { MaximumAttempts = 3 });

        await queue.EnqueueAsync(Request(directory));
        await WaitForStateAsync(queue, DownloadJobState.Failed);

        Assert.Equal(1, fake.Calls);
        Assert.Equal(errorCode, Assert.Single(queue.Jobs).ErrorCode);
    }

    // ------------------------------------------------------------------ 校验和是硬失败

    [Fact]
    public async Task ChecksumMismatch_IsAHardFailureAndNeverPublishesACorruptTarget()
    {
        using var directory = new TempDirectory();
        var target = directory.Combine("app.zip");
        using var client = Client(_ => Body(Payload));
        await using var queue = CreateQueue(directory, downloads: new HttpDownloadService(client),
            options: o => o with { MaximumAttempts = 3 });

        var enqueued = await queue.EnqueueAsync(Request(directory) with
        { ExpectedSize = 6, ExpectedSha256 = Hash("different") });
        await WaitForTerminalAsync(queue, enqueued.JobId);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Equal(DownloadErrorCodes.ChecksumMismatch, job.ErrorCode);
        // 校验和不匹配绝不能被当作瞬态失败重试。
        Assert.Equal(1, job.Attempts);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task DownloadServiceClaimingSuccess_IsStillIndependentlyVerifiedByTheQueue()
    {
        using var directory = new TempDirectory();
        // 服务谎报成功并写出错误内容；队列必须自行核验，绝不发布“完成”。
        var fake = new ScriptedDownload((request, _, _) =>
        {
            File.WriteAllText(request.TargetPath, "wrong bytes");
            return Task.FromResult(DownloadResult.Success(request.Source, request.TargetPath, 11, DownloadEngine.Http));
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6, ExpectedSha256 = Hash(Payload) });
        await WaitForTerminalAsync(queue, enqueued.JobId);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Equal(DownloadQueueErrorCodes.VerifyFailed, job.Message);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task FailedJob_IsRetriedByExplicitCommandOnly()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((request, call, _) => Task.FromResult(call == 1
            ? DownloadResult.Failure(DownloadErrorCodes.HttpStatusError, Source)
            : WriteTarget(request, Payload)));
        // 预算设为 1：首次运行必须失败（HTTP 状态错误是瞬态，但预算已用尽），
        // 之后由显式重试重置预算并成功。
        await using var queue = CreateQueue(directory, downloads: fake, options: o => o with { MaximumAttempts = 1 });

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6 });
        await WaitForStateAsync(queue, DownloadJobState.Failed);
        Assert.Equal(1, fake.Calls);

        var retried = await queue.RetryAsync(enqueued.JobId);

        Assert.True(retried.Succeeded, retried.ErrorCode);
        await WaitForStateAsync(queue, DownloadJobState.Completed);
        Assert.Equal(2, fake.Calls);
    }

    // ------------------------------------------------------------------ 移除与命令校验

    [Fact]
    public async Task Remove_DropsOnlyTerminalJobsAndKeepsPublishedFiles()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((request, _, _) => Task.FromResult(WriteTarget(request, Payload)));
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6 });
        await WaitForStateAsync(queue, DownloadJobState.Completed);
        var target = directory.Combine("app.zip");
        Assert.True(File.Exists(target));

        var removed = await queue.RemoveAsync(enqueued.JobId);

        Assert.True(removed.Succeeded, removed.ErrorCode);
        Assert.Empty(queue.Jobs);
        // 移除的是队列记录，不是已下载的文件。
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Remove_RefusesNonTerminalJobs()
    {
        using var directory = new TempDirectory();
        // 用可控阻塞的替身把任务稳定停在非终态：默认的瞬时替身可能在 Pause 到达前就已完成，
        // 那样 Remove 反而会合法成功，测试也就失去了意义。
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedDownload(async (request, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return WriteTarget(request, Payload);
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory) with { FileName = "queued.zip" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var paused = await queue.PauseAsync(enqueued.JobId);

        // 先确认任务确实停在非终态，再验证移除被拒；不依赖调度时序假设。
        Assert.True(paused.Succeeded, paused.ErrorCode);
        Assert.Equal(DownloadJobState.Paused, paused.Job!.State);
        Assert.False(paused.Job.IsTerminal);

        var result = await queue.RemoveAsync(enqueued.JobId);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueErrorCodes.InvalidState, result.ErrorCode);
        Assert.Single(queue.Jobs);
    }

    [Fact]
    public async Task Commands_OnUnknownJobReportAStableErrorCode()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);
        await queue.InitializeAsync();
        var missing = Guid.NewGuid();

        foreach (var result in new[]
        {
            await queue.PauseAsync(missing),
            await queue.ResumeAsync(missing),
            await queue.CancelAsync(missing),
            await queue.RetryAsync(missing),
            await queue.RemoveAsync(missing),
        })
        {
            Assert.False(result.Succeeded);
            Assert.Equal(DownloadQueueErrorCodes.JobNotFound, result.ErrorCode);
        }
    }

    [Fact]
    public async Task Commands_OnACompletedJobAreRejected()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((request, _, _) => Task.FromResult(WriteTarget(request, Payload)));
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6 });
        await WaitForStateAsync(queue, DownloadJobState.Completed);

        Assert.Equal(DownloadQueueErrorCodes.InvalidState, (await queue.PauseAsync(enqueued.JobId)).ErrorCode);
        Assert.Equal(DownloadQueueErrorCodes.InvalidState, (await queue.ResumeAsync(enqueued.JobId)).ErrorCode);
        Assert.Equal(DownloadQueueErrorCodes.InvalidState, (await queue.CancelAsync(enqueued.JobId)).ErrorCode);
    }

    // ------------------------------------------------------------------ 持久化与进度

    [Fact]
    public async Task QueueFile_IsWrittenUnderTheConfiguredPathAndSurvivesReopening()
    {
        using var directory = new TempDirectory();
        // 可控阻塞的替身：默认的瞬时替身可能在 Pause 到达前就跑完，那样任务会合法地变成
        // Completed，持久化出来的就不是我们想验证的 Paused。这里明确等到任务真的开始再暂停。
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedDownload(async (request, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return WriteTarget(request, Payload);
        });
        Guid enqueued;
        await using (var queue = CreateQueue(directory, downloads: fake))
        {
            enqueued = (await queue.EnqueueAsync(Request(directory))).JobId;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var paused = await queue.PauseAsync(enqueued);
            // 明确断言暂停成功且确实处于 Paused，而不是靠调度时序碰运气。
            Assert.True(paused.Succeeded, paused.ErrorCode);
            Assert.Equal(DownloadJobState.Paused, paused.Job!.State);
        }

        var queueFile = directory.Combine("download-queue.json");
        Assert.True(File.Exists(queueFile));
        await using var reopened = CreateQueue(directory);
        await reopened.InitializeAsync();

        var job = Assert.Single(reopened.Jobs);
        Assert.Equal(enqueued, job.Id);
        Assert.Equal(DownloadJobState.Paused, job.State);
    }

    [Fact]
    public async Task Queue_DoesNotCreateAFileWhenNothingWasEverEnqueued()
    {
        using var directory = new TempDirectory();
        await using var queue = CreateQueue(directory);

        await queue.InitializeAsync();

        // 从未使用过队列的用户不应该凭空得到一个队列文件。
        Assert.False(File.Exists(queue.QueueFilePath));
    }

    [Fact]
    public async Task Progress_ReportsTotalsAndPercentForTheRunningJob()
    {
        using var directory = new TempDirectory();
        var reported = new List<DownloadQueueProgress>();
        var fake = new ReportingDownload(Payload);
        await using var queue = CreateQueue(directory, downloads: fake);
        queue.ProgressChanged += (_, progress) => { lock (reported) reported.Add(progress); };

        var enqueued = await queue.EnqueueAsync(Request(directory) with { ExpectedSize = 6 });
        await WaitForTerminalAsync(queue, enqueued.JobId);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(6, job.TotalBytes);
        Assert.Equal(6, job.DoneBytes);
        Assert.Equal(1, job.Attempts);
        Assert.NotEmpty(reported);
        Assert.All(reported, progress => Assert.Equal(enqueued.JobId, progress.JobId));
        Assert.Contains(reported, progress => progress.Total == 6 && progress.Done == 6);
    }

    [Fact]
    public void TransientFailureClassification_IsDeliberatelyNarrow()
    {
        Assert.True(DownloadQueueService.IsTransientFailure(DownloadErrorCodes.NetworkError));
        Assert.True(DownloadQueueService.IsTransientFailure(DownloadErrorCodes.HttpStatusError));
        Assert.True(DownloadQueueService.IsTransientFailure(DownloadErrorCodes.InvalidResponse));
        Assert.True(DownloadQueueService.IsTransientFailure(DownloadErrorCodes.Aria2Failed));
        Assert.True(DownloadQueueService.IsTransientFailure(DownloadErrorCodes.Aria2TimedOut));
        // 完整性失败与确定性失败都不是瞬态，绝不能重试。
        foreach (var code in new[]
        {
            DownloadErrorCodes.ChecksumMismatch, DownloadErrorCodes.LengthMismatch,
            DownloadErrorCodes.InvalidVerification, DownloadErrorCodes.UnsupportedSource,
            DownloadErrorCodes.TargetNotWritable, DownloadErrorCodes.NoTargetDirectory,
            DownloadErrorCodes.PeerNotSupported, DownloadErrorCodes.Cancelled,
            DownloadErrorCodes.Aria2NotConfigured, DownloadErrorCodes.Aria2NotFound, null,
        })
            Assert.False(DownloadQueueService.IsTransientFailure(code));
    }

    // ------------------------------------------------------------------ 未预期异常

    [Fact]
    public async Task UnexpectedDependencyException_StillSettlesTheJobInsteadOfHangingInDownloadingAsync()
    {
        using var directory = new TempDirectory();
        // 抛出未知异常类型：修复前 outcome 永不赋值，控制流会跳过 Settle 与信号，
        // 任务将永远停在 Downloading（本测试会超时），且 StopAsync 会永久等待。
        var fake = new ScriptedDownload((_, _, _) =>
            throw new InvalidOperationException("unexpected dependency failure"));
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory));
        await WaitForTerminalAsync(queue, enqueued.JobId);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Equal(DownloadQueueErrorCodes.UnexpectedFailure, job.ErrorCode);
        // 原始异常内容绝不进入队列状态（Message 也不承载异常文本）。
        Assert.Null(job.Message);
        // 未知失败不参与瞬态重试：只尝试一次。
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task UnexpectedDependencyException_DoesNotStopTheWorker_SoLaterJobsStillRunAsync()
    {
        using var directory = new TempDirectory();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedDownload((request, call, _) =>
        {
            if (call == 1) throw new InvalidOperationException("boom");
            secondStarted.TrySetResult();
            return Task.FromResult(WriteTarget(request, Payload));
        });
        await using var queue = CreateQueue(directory, downloads: fake);

        // 每个任务必须有各自独立的目标路径：只改 FileName 会让两个任务都写同一个 app.zip，
        // 于是断言的文件根本不会被创建。
        await queue.EnqueueAsync(Request(directory) with { FileName = "one.zip", TargetPath = directory.Combine("one.zip") });
        await queue.EnqueueAsync(Request(directory) with { FileName = "two.zip", TargetPath = directory.Combine("two.zip") });

        // 工作循环必须在一次未预期异常后继续存活，否则后续任务永远不执行。
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => queue.Jobs.Count == 2 && queue.Jobs.All(j => j.IsTerminal),
            () => "Not all jobs reached a terminal state.");

        Assert.Equal(2, fake.Calls);
        Assert.Equal(DownloadJobState.Failed, queue.Jobs[0].State);
        Assert.Equal(DownloadJobState.Completed, queue.Jobs[1].State);
        Assert.True(File.Exists(directory.Combine("two.zip")));
    }

    [Fact]
    public async Task Commands_AfterAnUnexpectedFailure_ReturnPromptlyInsteadOfHangingAsync()
    {
        using var directory = new TempDirectory();
        var fake = new ScriptedDownload((_, _, _) => throw new InvalidOperationException("boom"));
        await using var queue = CreateQueue(directory, downloads: fake);

        var enqueued = await queue.EnqueueAsync(Request(directory));
        await WaitForTerminalAsync(queue, enqueued.JobId);

        // 任务已收束，命令立即返回明确结果；修复前 completion 永不被信号，这里会一直等待。
        var pause = await queue.PauseAsync(enqueued.JobId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pause.Succeeded);
        Assert.Equal(DownloadQueueErrorCodes.InvalidState, pause.ErrorCode);

        var retry = await queue.RetryAsync(enqueued.JobId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(retry.Succeeded, retry.ErrorCode);
    }

    // ------------------------------------------------------------------ 关闭（DisposeAsync）

    [Fact]
    public async Task DisposeAsync_WhenTheWorkerOutlivesTheShutdownTimeout_DoesNotDisposePrimitivesItStillUsesAsync()
    {
        using var directory = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 刻意忽略取消令牌：模拟一个响应取消很慢的下载器，让工作循环活过关闭超时。
        var fake = new ScriptedDownload(async (request, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            return WriteTarget(request, Payload);
        });
        var queue = CreateQueue(directory, downloads: fake,
            options: o => o with { ShutdownTimeout = TimeSpan.FromMilliseconds(50) });
        await queue.EnqueueAsync(Request(directory));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var worker = queue.WorkerCompletion!;

        await queue.DisposeAsync();

        // 关闭超时后工作循环仍在运行：此时绝不能让同步原语被释放，
        // 否则它随后在 finally 里 Release() 就会抛 ObjectDisposedException。
        Assert.False(worker.IsCompleted);
        // 释放被延后，尚未完成。
        var deferred = queue.DeferredRelease;
        Assert.NotNull(deferred);
        Assert.False(deferred!.IsCompleted);

        // 放行下载器：工作循环必须能正常收尾——这正是“原语仍可用”的证据。
        release.SetResult();
        await deferred.WaitAsync(TimeSpan.FromSeconds(10));

        // 收尾过程本身不能抛异常（不响应取消的下载器会让它在 finally 里 Release）。
        Assert.True(deferred.IsCompletedSuccessfully);
        Assert.Null(deferred.Exception);
        Assert.True(worker.IsCompletedSuccessfully);
        Assert.Null(worker.Exception);
    }

    [Fact]
    public async Task DisposeAsync_WithAnIdleWorker_CompletesAndStopsTheWorkerAsync()
    {
        using var directory = new TempDirectory();
        var queue = CreateQueue(directory,
            options: o => o with { ShutdownTimeout = TimeSpan.FromSeconds(10) });
        await queue.InitializeAsync();
        var worker = queue.WorkerCompletion!;

        await queue.DisposeAsync();

        // 空闲的工作循环会立刻响应取消，因此关闭是有界的、不需要走延后释放那条路径。
        Assert.True(worker.IsCompleted);
        Assert.True(worker.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AfterDisposeAsync_DisposingAgainIsASafeNoOpAsync()
    {
        using var directory = new TempDirectory();
        var queue = CreateQueue(directory);
        await queue.InitializeAsync();

        await queue.DisposeAsync();
        await queue.DisposeAsync();

        // 重复关闭必须是安全的空操作，而不是对已释放原语的二次释放。
        Assert.True(queue.WorkerCompletion!.IsCompletedSuccessfully);
    }

    // ------------------------------------------------------------------ 测试基础设施

    private static DownloadQueueService CreateQueue(TempDirectory directory,
        IDownloadService? downloads = null, Func<DownloadQueueOptions, DownloadQueueOptions>? options = null)
    {
        var configured = new DownloadQueueOptions
        {
            QueueFilePath = directory.Combine("download-queue.json"),
            // 退避置零：测试不应为了验证重试预算而真实等待。
            RetryBackoff = TimeSpan.Zero,
        };
        if (options is not null) configured = options(configured);
        return new DownloadQueueService(downloads ?? new ScriptedDownload((request, _, _) =>
            Task.FromResult(WriteTarget(request, Payload))), configured);
    }

    private static DownloadQueueRequest Request(TempDirectory directory) => new()
    {
        Source = Source,
        FileName = Name,
        TargetPath = directory.Combine(Name),
    };

    private static DownloadResult WriteTarget(DownloadRequest request, string payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.TargetPath)!);
        File.WriteAllText(request.TargetPath, payload);
        return DownloadResult.Success(request.Source, request.TargetPath, payload.Length, request.Engine);
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>与 HttpDownloadService 的 ResumeMetadata 字段名一致的断点描述文件。</summary>
    private static string Sidecar(long totalLength) => JsonSerializer.Serialize(new
    {
        Source = Source.AbsoluteUri,
        ETag = "\"v1\"",
        LastModified = (DateTimeOffset?)null,
        TotalLength = totalLength,
    });

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHttpHandler(respond));

    private static HttpResponseMessage Body(string text, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
        response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(text);
        response.Headers.ETag = EntityTagHeaderValue.Parse("\"v1\"");
        return response;
    }

    /// <summary>等待任务进入终态。队列在后台工作循环里推进，测试必须等待而不是同步阻塞。</summary>
    private static async Task WaitForTerminalAsync(DownloadQueueService queue, Guid jobId)
    {
        await WaitUntilAsync(() => queue.Jobs.FirstOrDefault(j => j.Id == jobId)?.IsTerminal == true,
            () => $"Job {jobId} did not reach a terminal state. Current: "
                + (queue.Jobs.FirstOrDefault(j => j.Id == jobId)?.State.ToString() ?? "missing"));
    }

    private static Task WaitForStateAsync(DownloadQueueService queue, DownloadJobState state) =>
        WaitUntilAsync(() => queue.Jobs.Count > 0 && queue.Jobs.All(j => j.State == state),
            () => $"Queue did not reach {state}. Current: {string.Join(", ", queue.Jobs.Select(j => j.State))}");

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail(describe());
    }

    // ------------------------------------------------------------------ 直接构造队列文件

    private static void WriteQueueFile(TempDirectory directory, params QueueJobDocument[] jobs) =>
        File.WriteAllText(directory.Combine("download-queue.json"), QueueJson(jobs));

    private static string QueueJson(params QueueJobDocument[] jobs) =>
        new StringBuilder("{\"version\":1,\"jobs\":[")
            .Append(string.Join(',', jobs.Select(JobJson))).Append("]}").ToString();

    private static string QueueJson(string source, string target) =>
        QueueJson(new QueueJobDocument(Guid.NewGuid(), source, Name, target, DownloadEngine.Http,
            false, null, null, DownloadJobState.Queued, 0, 0, 0, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    private static string JobJson(QueueJobDocument job) => JsonSerializer.Serialize(job, QueueJsonOptions);

    private static readonly JsonSerializerOptions QueueJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<DownloadEngine>(), new JsonStringEnumConverter<DownloadJobState>() },
    };

    private static QueueJobDocument Job(string target, DownloadJobState state, int attempts = 0,
        long? expectedSize = 6, string? digest = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new QueueJobDocument(Guid.NewGuid(), Source.AbsoluteUri, Name, target, DownloadEngine.Http,
            false, expectedSize, digest, state, attempts, 0, expectedSize ?? 0, null, null, now, now);
    }

    private sealed record QueueJobDocument(Guid Id, string Source, string FileName, string TargetPath,
        DownloadEngine Engine, bool UseAccelerator, long? ExpectedSize, string? ExpectedSha256,
        DownloadJobState State, int Attempts, long DoneBytes, long TotalBytes, string? ErrorCode, string? Message,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    /// <summary>可编排的下载替身。只记录调用顺序与次数；绝不启动真实进程或网络。</summary>
    private sealed class ScriptedDownload(Func<DownloadRequest, int, CancellationToken, Task<DownloadResult>> handler)
        : IDownloadService
    {
        private readonly object _sync = new();
        private readonly List<string> _order = [];
        private int _calls;

        public int Calls { get { lock (_sync) return _calls; } }
        public IReadOnlyList<string> Order { get { lock (_sync) return _order.ToArray(); } }

        /// <summary>等待替身被调用到指定次数，避免依赖任务完成的时机。</summary>
        public Task WaitForCallsAsync(int count) =>
            WaitUntilAsync(() => Calls >= count, () => $"The download stub was called {Calls} times, expected at least {count}.");

        public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);

        public Task<DownloadResult> DownloadAsync(DownloadRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            int call;
            lock (_sync)
            {
                call = ++_calls;
                _order.Add(request.FileName ?? Path.GetFileName(request.TargetPath));
            }
            return handler(request, call, ct);
        }
    }

    /// <summary>报告进度的替身，用于验证队列把总长与进度透出给界面。</summary>
    private sealed class ReportingDownload(string payload) : IDownloadService
    {
        public IReadOnlyList<DownloadEngine> AvailableEngines => [DownloadEngine.Http];
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);

        public Task<DownloadResult> DownloadAsync(DownloadRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var bytes = Encoding.UTF8.GetByteCount(payload);
            progress?.Report(new DownloadProgress { Source = request.Source, Done = bytes, Total = bytes });
            return Task.FromResult(WriteTarget(request, payload));
        }
    }
}
