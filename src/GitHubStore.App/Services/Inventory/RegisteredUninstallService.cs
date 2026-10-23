using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using GitHubStore.Core.Abstractions;
using Microsoft.Win32;

namespace GitHubStore.App.Services.Inventory;

public sealed record RegisteredUninstaller(string Key, string DisplayName, string InstallLocation,
    string CommandLine, bool PerMachine, string? ProductCode = null);

/// <summary>Uses Windows' registered uninstaller for a Store receipt, including older MSI receipts without a product code.</summary>
public sealed class RegisteredUninstallService
{
    private readonly IManagedAppLauncher _launcher;
    private readonly Func<IReadOnlyList<RegisteredUninstaller>> _read;
    private readonly Func<UninstallCommand, bool, CancellationToken, Task<int>> _run;
    private readonly UninstallCommandValidator _validator;

    public RegisteredUninstallService(IManagedAppLauncher launcher,
        Func<IReadOnlyList<RegisteredUninstaller>>? reader = null,
        Func<UninstallCommand, bool, CancellationToken, Task<int>>? runner = null,
        UninstallCommandValidator? validator = null)
    {
        _launcher = launcher;
        _read = reader ?? ReadWindowsRegistrations;
        _run = runner ?? RunAsync;
        _validator = validator ?? new UninstallCommandValidator();
    }

    public RegisteredUninstaller? Find(InstallRecord receipt, string displayName)
    {
        if (receipt.Origin != Origin.Store || receipt.State != InstallState.Done
            || StoreOwnedInstallation.OwnsExtractedFiles(receipt)) return null;
        var matches = _read().Where(item => Matches(item, receipt, displayName)).ToArray();
        // Ambiguous registrations do not authorize choosing one by display name alone.
        return matches.Length == 1 ? matches[0] : null;
    }

    public async Task<UninstallLaunchResult> UninstallAsync(string installId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var receipt = await _launcher.GetAsync(installId, ct).ConfigureAwait(false);
        if (receipt is null) return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
        var entry = (await _launcher.ListAsync(ct).ConfigureAwait(false)).SingleOrDefault(item => item.InstallId == installId);
        if (entry is null) return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
        RegisteredUninstaller? registration;
        try { registration = Find(receipt, entry.DisplayName); }
        catch (Exception) { return UninstallLaunchResult.Failure(UninstallErrorCodes.ScanFailed); }
        if (registration is null) return UninstallLaunchResult.Failure(UninstallErrorCodes.NoUninstallString);
        if (!_validator.TryValidate(registration.CommandLine, out var command, out var error))
            return UninstallLaunchResult.Failure(error ?? UninstallErrorCodes.InvalidUninstallCommand);
        try
        {
            // The command is read again from Windows after confirmation; UI-provided commands are never used.
            var current = _read().SingleOrDefault(item => item.Key == registration.Key);
            if (current is null || current != registration || !Matches(current, receipt, entry.DisplayName))
                return UninstallLaunchResult.Failure(UninstallErrorCodes.InvalidUninstallCommand);
            var exit = await _run(command!, registration.PerMachine, ct).ConfigureAwait(false);
            if (exit is 1223 or 1602) return UninstallLaunchResult.Failure("uninstall_cancelled");
            if (exit is not (0 or 3010 or 1605))
                return UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalFailed);
            // An uninstaller may spawn another process and return early. Keep the receipt until Windows confirms absence.
            if (_read().Any(item => item.Key == registration.Key))
                return UninstallLaunchResult.Success(command!.CommandLine);
            await _launcher.UnregisterAsync(installId, ct).ConfigureAwait(false);
            return UninstallLaunchResult.RemovedByProductCode(command!.CommandLine);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return UninstallLaunchResult.Failure("uninstall_cancelled"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 740)
        { return UninstallLaunchResult.Failure(UninstallErrorCodes.AccessDenied); }
        catch (UnauthorizedAccessException)
        { return UninstallLaunchResult.Failure(UninstallErrorCodes.AccessDenied); }
        catch (Exception)
        { return UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalFailed); }
    }

    private bool Matches(RegisteredUninstaller item, InstallRecord receipt, string displayName)
    {
        if (!SameDirectory(item.InstallLocation, receipt.TargetDir) || Name(item.DisplayName) != Name(displayName)) return false;
        if (receipt.Installer == InstallerKind.Msi)
        {
            if (!Guid.TryParseExact(item.ProductCode, "B", out var registered)) return false;
            if (receipt.ProductCode is { Length: > 0 } code &&
                (!Guid.TryParseExact(code, "B", out var recorded) || registered != recorded)) return false;
        }
        return _validator.TryValidate(item.CommandLine, out _, out _);
    }

    private static string Name(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool SameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
                && Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
                    .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static IReadOnlyList<RegisteredUninstaller> ReadWindowsRegistrations()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return ReadRegistry();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RegisteredUninstaller> ReadRegistry()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var result = new List<RegisteredUninstaller>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(keyPath);
            if (root is null) continue;
            foreach (var keyName in root.GetSubKeyNames().Take(10000))
            {
                using var key = root.OpenSubKey(keyName);
                if (key is null) continue;
                var name = key.GetValue("DisplayName") as string;
                var location = key.GetValue("InstallLocation") as string;
                var command = key.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location)
                    || string.IsNullOrWhiteSpace(command)) continue;
                string? productCode = null;
                if (key.GetValue("WindowsInstaller") is int msi && msi == 1 && Guid.TryParseExact(keyName, "B", out var guid))
                {
                    productCode = guid.ToString("B").ToUpperInvariant();
                    // Windows MSI registrations often contain /I (maintenance). Explicit uninstall uses /x with the registered product code.
                    var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                    command = "\"" + exe + "\" /x " + productCode + " /norestart";
                }
                var id = hive + ":" + view + ":" + keyName;
                result.Add(new(id, name, location, command, hive == RegistryHive.LocalMachine, productCode));
            }
        }
        // HKCU can be shared between registry views; an identical registration is one entry.
        return result.GroupBy(item => new { item.PerMachine, item.ProductCode, item.DisplayName, item.InstallLocation, item.CommandLine })
            .Select(group => group.First()).ToArray();
    }

    private static async Task<int> RunAsync(UninstallCommand command, bool elevate, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = command.FileName, Arguments = command.Arguments,
            WorkingDirectory = Path.GetDirectoryName(command.FileName)!, UseShellExecute = true,
        };
        if (elevate) start.Verb = "runas";
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        // Cancellation stops waiting; it does not kill Windows Installer or claim it rolled back.
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}
