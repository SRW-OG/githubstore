namespace GitHubStore.Core.Abstractions;

/// <summary>A user-selected launcher entry, independent of managed installation receipts.</summary>
public sealed record ManualLauncherEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Path { get; init; }
    public CategoryId? Category { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public bool IsAvailable { get; init; }
    /// <summary>Null means legacy arguments are still supplied by the customization record.</summary>
    public string? Arguments { get; init; }
}

/// <summary>Manages launcher shortcuts only; entries never grant installation or removal ownership.</summary>
public interface IManualAppLauncher
{
    Task<IReadOnlyList<ManualLauncherEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>Returns the existing entry when the normalized path is already registered (ignoring case).</summary>
    Task<ManualLauncherEntry> AddAsync(string path, string displayName, CategoryId? category = null,
        CancellationToken ct = default);

    /// <summary>Adds the executable and arguments in a single atomic write; duplicate paths keep their existing settings.</summary>
    Task<ManualLauncherEntry> AddProgramAsync(string path, string displayName, CategoryId? category, string arguments,
        CancellationToken ct = default) => string.IsNullOrEmpty(arguments)
            ? AddAsync(path, displayName, category, ct)
            : throw new LauncherException("manual_launcher_options_unsupported");

    /// <summary>Atomically changes just the path and arguments, retaining the entry's metadata and identity.</summary>
    Task<ManualLauncherEntry> UpdateProgramAsync(string id, string path, string arguments, CancellationToken ct = default) =>
        throw new LauncherException("manual_launcher_update_unsupported");

    /// <summary>Updates by ID, preserving identity and added time. Existing missing paths may keep their metadata edited.</summary>
    Task<ManualLauncherEntry> UpdateAsync(string id, string path, string displayName, CategoryId? category,
        CancellationToken ct = default) => throw new LauncherException("manual_launcher_update_unsupported");

    /// <summary>Removes the entry only, leaving the target and all application files untouched.</summary>
    Task RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>Rereads the saved entry by ID before launching it.</summary>
    Task<LauncherLaunchResult> LaunchAsync(string id, CancellationToken ct = default);

    /// <summary>Launches the saved entry with user-selected arguments, directory or elevation.</summary>
    Task<LauncherLaunchResult> LaunchWithOptionsAsync(string id, LauncherEntryOptions options, CancellationToken ct = default) =>
        !LauncherEntryOptionsValidation.IsValid(options)
            ? Task.FromResult(new LauncherLaunchResult(false, "manual_launcher_invalid_options"))
            : LauncherEntryOptionsValidation.RequiresCustomLaunch(options)
                ? Task.FromResult(new LauncherLaunchResult(false, "manual_launcher_options_unsupported")) : LaunchAsync(id, ct);
}
