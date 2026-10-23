namespace GitHubStore.Core.Abstractions;

/// <summary>Runs one executable directly. Caller cancellation is propagated after process cleanup.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);
}

/// <summary>Arguments are individual values, never a shell command line.</summary>
public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum ProcessOutcome
{
    Exited,
    NotFound,
    FailedToStart,
    TimedOut,
}

public sealed record ProcessResult
{
    public required ProcessOutcome Outcome { get; init; }
    public int? ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string? Diagnostic { get; init; }
}
