namespace GitHubStore.Core.Abstractions;

/// <summary>Launcher-only presentation overrides and usage, keyed by source and entry ID.</summary>
public sealed record LauncherCustomization
{
    public required string EntryId { get; init; }
    public bool IsManual { get; init; }
    public string? DisplayName { get; init; }
    public bool HasCategoryOverride { get; init; }
    public CategoryId? Category { get; init; }
    public long LaunchCount { get; init; }
    public LauncherEntryOptions Options { get; init; } = new();
}

/// <summary>User-selected launch settings and launcher presentation. Arguments go directly to the saved entry, never cmd.</summary>
public sealed record LauncherEntryOptions
{
    public string? IconPath { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string? WorkingDirectory { get; init; }
    public bool RunAsAdministrator { get; init; }
    public string? Color { get; init; }
    public string? Hotkey { get; init; }
    public string Notes { get; init; } = string.Empty;
}

/// <summary>Shared validation for persisted options and direct launch requests.</summary>
public static class LauncherEntryOptionsValidation
{
    public static bool RequiresCustomLaunch(LauncherEntryOptions options) =>
        !string.IsNullOrEmpty(options.Arguments) || !string.IsNullOrEmpty(options.WorkingDirectory) || options.RunAsAdministrator;

    public static bool IsValid(LauncherEntryOptions? options) => options is not null
        && options.Arguments is { Length: <= 8192 } && !options.Arguments.Contains('\0')
        && options.Notes is { Length: <= 4000 } && !options.Notes.Contains('\0')
        && (options.Hotkey is null || options.Hotkey.Length <= 80 && !options.Hotkey.Any(char.IsControl))
        && (string.IsNullOrEmpty(options.Color) || options.Color.Length is 7 or 9 && options.Color[0] == '#'
            && options.Color.Skip(1).All(Uri.IsHexDigit))
        && IsLocalPath(options.IconPath) && IsLocalPath(options.WorkingDirectory);

    private static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.Length > 2048 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':') return false;
            foreach (var part in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or ".." || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.')
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

public interface ILauncherCustomizationStore
{
    Task<IReadOnlyList<LauncherCustomization>> ListAsync(CancellationToken ct = default);
    Task<LauncherCustomization> RecordLaunchAsync(bool isManual, string entryId, CancellationToken ct = default);
    Task<LauncherCustomization> UpdatePropertiesAsync(bool isManual, string entryId, string displayName,
        CategoryId? category, CancellationToken ct = default);
    Task<LauncherCustomization> SavePropertiesAsync(bool isManual, string entryId, string displayName,
        CategoryId? category, LauncherEntryOptions options, CancellationToken ct = default) =>
        UpdatePropertiesAsync(isManual, entryId, displayName, category, ct);
}
