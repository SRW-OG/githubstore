using System.Runtime.InteropServices;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>
/// Owns WM_HOTKEY registrations for launcher entries. Every registration lives only while the
/// process runs; Dispose and window destruction both release the native registrations.
/// </summary>
public sealed class LauncherHotkeyRegistry : ILauncherHotkeyRegistry
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Func<nint, uint, uint, uint, bool> _register;
    private readonly Action<nint, uint> _unregister;
    private readonly bool _nativeSubclass;
    private readonly Dictionary<int, Binding> _byNativeId = [];
    private readonly Dictionary<(bool, string), int> _byEntry = [];
    private readonly SubclassProc _subclassProc;
    private nint _window;
    private int _nextNativeId = 0xA000;
    private bool _disposed;

    private sealed record Binding(int NativeId, bool IsManual, string EntryId, uint Modifiers, uint VirtualKey);

    public LauncherHotkeyRegistry()
        : this(RegisterHotKey, (window, id) => UnregisterHotKey(window, id), nativeSubclass: true) { }

    /// <summary>Test seam: native registration can be replaced so no real global key is installed.</summary>
    public LauncherHotkeyRegistry(Func<nint, uint, uint, uint, bool> register, Action<nint, uint> unregister,
        bool nativeSubclass)
    {
        _register = register;
        _unregister = unregister;
        _nativeSubclass = nativeSubclass;
        _subclassProc = OnSubclassMessage;
    }

    public event EventHandler<LauncherHotkeyEventArgs>? Triggered;

    public void Attach(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (windowHandle == 0) throw new LauncherException("launcher_hotkey_invalid_window");
        if (_window == windowHandle) return;
        DetachCurrentWindow();
        _window = windowHandle;
        if (_nativeSubclass
            && !SetWindowSubclass(windowHandle, _subclassProc, (nuint)0x4753, 0))
            throw new LauncherException("launcher_hotkey_subclass_failed");
    }

    public bool TrySet(bool isManual, string entryId, string? shortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window == 0) throw new LauncherException("launcher_hotkey_not_attached");
        if (string.IsNullOrWhiteSpace(entryId)) return false;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            Remove(isManual, entryId);
            return true;
        }
        if (!LauncherHotkeyParser.TryParse(shortcut, out var modifiers, out var virtualKey, out _)) return false;
        var key = (isManual, entryId);
        var existingId = _byEntry.TryGetValue(key, out var id) ? id : 0;
        // Another entry already owns this exact combination.
        if (_byNativeId.Values.Any(binding => binding.NativeId != existingId
            && binding.Modifiers == modifiers && binding.VirtualKey == virtualKey)) return false;
        var nativeId = _nextNativeId++;
        if (!_register(_window, (uint)nativeId, modifiers | MOD_NOREPEAT, virtualKey))
            return false;
        if (existingId != 0)
        {
            _unregister(_window, (uint)existingId);
            _byNativeId.Remove(existingId);
        }
        _byNativeId[nativeId] = new Binding(nativeId, isManual, entryId, modifiers, virtualKey);
        _byEntry[key] = nativeId;
        return true;
    }

    public void Retain(IReadOnlyCollection<(bool IsManual, string EntryId)> entries)
    {
        var keep = new HashSet<(bool, string)>(entries);
        foreach (var (manual, entryId) in _byEntry.Keys.ToArray())
        {
            if (!keep.Contains((manual, entryId))) Remove(manual, entryId);
        }
    }

    private void Remove(bool isManual, string entryId)
    {
        if (!_byEntry.Remove((isManual, entryId), out var nativeId)) return;
        if (_window != 0) _unregister(_window, (uint)nativeId);
        _byNativeId.Remove(nativeId);
    }

    /// <summary>Native callback entry point; kept internal so tests can simulate WM_HOTKEY.</summary>
    internal void OnHotkey(int nativeId)
    {
        if (_disposed || !_byNativeId.TryGetValue(nativeId, out var binding)) return;
        Triggered?.Invoke(this, new LauncherHotkeyEventArgs(binding.IsManual, binding.EntryId));
    }

    private nint OnSubclassMessage(nint hWnd, uint message, nuint wParam, nint lParam, nuint id, nint data)
    {
        if (message == WM_HOTKEY) OnHotkey((int)wParam);
        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void DetachCurrentWindow()
    {
        foreach (var binding in _byNativeId.Values.ToArray())
        {
            if (_window != 0) _unregister(_window, (uint)binding.NativeId);
        }
        _byNativeId.Clear();
        _byEntry.Clear();
        if (_window != 0 && _nativeSubclass)
            RemoveWindowSubclass(_window, _subclassProc, (nuint)0x4753);
        _window = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachCurrentWindow();
    }

    private delegate nint SubclassProc(nint hWnd, uint message, nuint wParam, nint lParam, nuint id, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, uint id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, uint id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc callback, nuint id, nint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint message, nuint wParam, nint lParam);
}
