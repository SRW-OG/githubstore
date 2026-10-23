using System.ComponentModel;
using System.Diagnostics;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Inventory;

/// <summary>已通过校验的卸载命令；参数原文直接交给程序，绝不经过 shell。</summary>
public sealed record UninstallCommand(string FileName, string Arguments)
{
    public string CommandLine => string.IsNullOrEmpty(Arguments)
        ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
}

public interface IUninstallLauncher
{
    Task<UninstallLaunchResult> LaunchAsync(UninstallCommand command, CancellationToken ct = default);
}

/// <summary>只负责启动，命令解析和允许规则由 UninstallCommandValidator 负责。</summary>
public sealed class ProcessUninstallLauncher : IUninstallLauncher
{
    public Task<UninstallLaunchResult> LaunchAsync(UninstallCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                WorkingDirectory = Path.GetDirectoryName(command.FileName)!,
                UseShellExecute = false,
            });
            return Task.FromResult(process is null
                ? UninstallLaunchResult.Failure(UninstallErrorCodes.LaunchFailed)
                : UninstallLaunchResult.Success(command.CommandLine));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 || ex.NativeErrorCode == 740)
        {
            return Task.FromResult(UninstallLaunchResult.Failure(UninstallErrorCodes.AccessDenied));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Trace.WriteLine(ex);
            return Task.FromResult(UninstallLaunchResult.Failure(UninstallErrorCodes.LaunchFailed));
        }
    }
}
