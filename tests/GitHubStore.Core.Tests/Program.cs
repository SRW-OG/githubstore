using System.Collections.Concurrent;
using System.Text.Json;
using Xunit.Runners;

namespace GitHubStore.Core.Tests;

/// <summary>Runs the unchanged xUnit suite in this process when VSTest cannot start its host.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? resultPath = null;
        if (args.Length == 2 && args[0] == "--result") resultPath = args[1];
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: GitHubStore.Core.Tests [--result <json-file>]");
            return 2;
        }

        var completion = new TaskCompletionSource<ExecutionCompleteInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<Failure>();
        var skipped = new ConcurrentQueue<Skipped>();
        var errors = new ConcurrentQueue<string>();
        AssemblyRunner? runner = null;
        ExecutionCompleteInfo? summary = null;
        var timedOut = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(60);
        try
        {
            runner = AssemblyRunner.WithoutAppDomain(typeof(Program).Assembly.Location);
            runner.OnTestFailed = test =>
            {
                failures.Enqueue(new Failure(test.TestDisplayName, test.ExceptionType,
                    test.ExceptionMessage, test.ExceptionStackTrace, test.Output));
                Console.Error.WriteLine($"[FAIL] {test.TestDisplayName}\n{test.ExceptionType}: {test.ExceptionMessage}\n{test.ExceptionStackTrace}");
            };
            runner.OnTestSkipped = test =>
            {
                skipped.Enqueue(new Skipped(test.TestDisplayName, test.SkipReason));
                Console.WriteLine($"[SKIP] {test.TestDisplayName}: {test.SkipReason}");
            };
            runner.OnErrorMessage = error =>
            {
                var message = $"{error.ExceptionType}: {error.ExceptionMessage}\n{error.ExceptionStackTrace}";
                errors.Enqueue(message);
                Console.Error.WriteLine($"[ERROR] {message}");
            };
            runner.OnExecutionComplete = info => completion.TrySetResult(info);
            runner.Start();
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            summary = await completion.Task.WaitAsync(remaining);
            while (runner.Status != AssemblyRunnerStatus.Idle)
            {
                if (elapsed.Elapsed >= timeout) throw new TimeoutException();
                await Task.Delay(10);
            }
        }
        catch (TimeoutException)
        {
            timedOut = true;
            runner?.Cancel();
            errors.Enqueue("xUnit execution exceeded the 60 second timeout.");
            Console.Error.WriteLine("[ERROR] xUnit execution exceeded the 60 second timeout.");
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex.ToString());
            Console.Error.WriteLine($"[ERROR] {ex}");
        }
        finally
        {
            // Execution-complete callbacks can run just before Status becomes Idle.
            // Do not block timeout handling on disposal of a still-running test.
            if (runner?.Status == AssemblyRunnerStatus.Idle) runner.Dispose();
        }

        var success = !timedOut && errors.IsEmpty && failures.IsEmpty
            && summary is { TotalTests: > 0, TestsFailed: 0 };
        var report = new
        {
            runner = "xunit-in-process",
            success,
            timedOut,
            total = summary?.TotalTests ?? 0,
            failed = summary?.TestsFailed ?? failures.Count,
            skipped = summary?.TestsSkipped ?? skipped.Count,
            time = summary?.ExecutionTime,
            failures = failures.ToArray(),
            skipDetails = skipped.ToArray(),
            errors = errors.ToArray(),
        };
        Console.WriteLine($"xUnit: total={report.total}, failed={report.failed}, skipped={report.skipped}, "
            + $"seconds={report.time}, errors={report.errors.Length}, timedOut={report.timedOut}");
        if (summary is { TotalTests: 0 }) Console.Error.WriteLine("[ERROR] No xUnit tests were executed.");
        if (resultPath is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                var fullResultPath = Path.GetFullPath(resultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
                await File.WriteAllTextAsync(fullResultPath, json);
                Console.WriteLine($"JSON result: {fullResultPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Cannot write JSON result: {ex.Message}");
                return 1;
            }
        }
        return success ? 0 : 1;
    }

    private sealed record Failure(string Test, string ExceptionType, string Message, string StackTrace, string Output);
    private sealed record Skipped(string Test, string Reason);
}
