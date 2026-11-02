namespace GitHubStore.Core.Abstractions;

/// <summary>The executable and arguments extracted from a user-selected program or shortcut.</summary>
public sealed record LauncherProgramTarget(string Path, string Arguments);

public interface ILauncherProgramResolver
{
    Task<LauncherProgramTarget> ResolveAsync(string path, CancellationToken ct = default);
}
