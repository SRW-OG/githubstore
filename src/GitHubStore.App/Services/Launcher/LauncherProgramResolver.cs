using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>Reads shortcut metadata without executing it, searching for moved targets, or showing Shell UI.</summary>
public sealed class LauncherProgramResolver : ILauncherProgramResolver
{
    private static readonly SemaphoreSlim Workers = new(2);
    private readonly Func<string, LauncherProgramTarget> _readShortcut;

    public LauncherProgramResolver() : this(ReadShortcut) { }
    public LauncherProgramResolver(Func<string, LauncherProgramTarget> readShortcut) => _readShortcut = readShortcut;

    public async Task<LauncherProgramTarget> ResolveAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = ManualAppLauncher.NormalizePath(path);
        if (Path.GetExtension(normalized).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(normalized, string.Empty);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows shortcuts require Windows.");
        await Workers.WaitAsync(ct).ConfigureAwait(false);
        var completion = new TaskCompletionSource<LauncherProgramTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
                initialized = true;
                var current = normalized;
                var arguments = string.Empty;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (Path.GetExtension(current).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (visited.Count >= 8 || !visited.Add(current))
                        throw new LauncherException("manual_launcher_shortcut_invalid");
                    ManualAppLauncher.EnsureAvailable(current);
                    var target = _readShortcut(current);
                    current = ManualAppLauncher.NormalizePath(Environment.ExpandEnvironmentVariables(target.Path));
                    arguments = string.IsNullOrEmpty(arguments) ? target.Arguments
                        : string.IsNullOrEmpty(target.Arguments) ? arguments : target.Arguments + " " + arguments;
                    if (!LauncherEntryOptionsValidation.IsValid(new LauncherEntryOptions { Arguments = arguments }))
                        throw new LauncherException("manual_launcher_invalid_options");
                }
                ManualAppLauncher.EnsureAvailable(current);
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(new(current, arguments));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); }
            catch (LauncherException ex) { completion.TrySetException(ex); }
            catch (Exception) { completion.TrySetException(new LauncherException("manual_launcher_shortcut_invalid")); }
            finally
            {
                if (initialized) CoUninitialize();
                Workers.Release();
            }
        }) { IsBackground = true, Name = "Launcher shortcut reader" };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            Workers.Release();
            throw;
        }
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private static LauncherProgramTarget ReadShortcut(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows shortcuts require Windows.");
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, 0, 4); // SLGP_RAWPATH; do not call Resolve (which can search or display UI).
            link.GetArguments(arguments, arguments.Capacity);
            return new(target.ToString(), arguments.ToString());
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
