namespace GitHubStore.Core.Abstractions;

/// <summary>Raised when a registered launcher hotkey is pressed while the application is running.</summary>
public sealed class LauncherHotkeyEventArgs : EventArgs
{
    public LauncherHotkeyEventArgs(bool isManual, string entryId)
    {
        IsManual = isManual;
        EntryId = entryId;
    }

    public bool IsManual { get; }
    public string EntryId { get; }
}

/// <summary>
/// Registers launcher entries to global hotkeys for the lifetime of the process.
/// Shortcuts are intentionally not persisted to the operating system by any other mechanism.
/// </summary>
public interface ILauncherHotkeyRegistry : IDisposable
{
    event EventHandler<LauncherHotkeyEventArgs>? Triggered;

    /// <summary>Binds the registry to the main window that receives WM_HOTKEY. Call on the UI thread.</summary>
    void Attach(nint windowHandle);

    /// <summary>
    /// Registers or clears one entry's shortcut. Returns false when the text is invalid or the
    /// combination is already owned by another entry, in which case the previous binding is kept.
    /// </summary>
    bool TrySet(bool isManual, string entryId, string? shortcut);

    /// <summary>Drops bindings for entries that no longer exist or no longer define a shortcut.</summary>
    void Retain(IReadOnlyCollection<(bool IsManual, string EntryId)> entries);
}

/// <summary>Parses user-entered gestures such as Ctrl+Alt+K into Win32 modifier and virtual-key values.</summary>
public static class LauncherHotkeyParser
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;

    /// <summary>
    /// Accepts Ctrl/Alt/Shift/Win plus A-Z, 0-9 or F1-F24. A bare key or a shortcut without
    /// Ctrl/Alt/Win is rejected so typing in other applications is not intercepted.
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey, out string normalized)
    {
        modifiers = 0;
        virtualKey = 0;
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        var control = false;
        var alt = false;
        var shift = false;
        var win = false;
        var key = string.Empty;
        foreach (var part in parts)
        {
            if (part.Length == 0) return false;
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    if (control) return false;
                    control = true;
                    break;
                case "ALT":
                    if (alt) return false;
                    alt = true;
                    break;
                case "SHIFT":
                    if (shift) return false;
                    shift = true;
                    break;
                case "WIN" or "WINDOWS":
                    if (win) return false;
                    win = true;
                    break;
                default:
                    if (key.Length != 0) return false;
                    key = part.ToUpperInvariant();
                    break;
            }
        }
        if (key.Length == 0 || !(control || alt || win)) return false;
        uint vk;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) vk = key[0];
        else if (key.Length is >= 2 and <= 3 && key[0] == 'F'
            && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24)
        {
            // F12 is reserved by the shell debugger and cannot be registered reliably.
            if (number == 12) return false;
            vk = (uint)(0x70 + (number - 1));
        }
        else return false;
        if (control) modifiers |= MOD_CONTROL;
        if (alt) modifiers |= MOD_ALT;
        if (shift) modifiers |= MOD_SHIFT;
        if (win) modifiers |= MOD_WIN;
        virtualKey = vk;
        var segments = new List<string>(5);
        if (control) segments.Add("Ctrl");
        if (alt) segments.Add("Alt");
        if (shift) segments.Add("Shift");
        if (win) segments.Add("Win");
        segments.Add(key);
        normalized = string.Join('+', segments);
        return true;
    }
}
