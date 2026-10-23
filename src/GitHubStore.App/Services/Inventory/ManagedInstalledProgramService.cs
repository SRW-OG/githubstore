using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Inventory;

/// <summary>Store receipts only. Installer applications use Windows' registered uninstaller; portable files use managed removal.</summary>
public sealed class ManagedInstalledProgramService : IInstalledProgramService
{
    private readonly IManagedAppLauncher _launcher;
    private readonly ManagedAppRemovalService _removal;
    private readonly RegisteredUninstallService _registered;

    public ManagedInstalledProgramService(IManagedAppLauncher launcher, ManagedAppRemovalService? removal = null,
        RegisteredUninstallService? registered = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _removal = removal ?? new ManagedAppRemovalService(launcher);
        _registered = registered ?? new RegisteredUninstallService(launcher);
    }

    public async Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default)
    {
        var entries = await _launcher.ListAsync(ct).ConfigureAwait(false);
        var programs = new List<InstalledProgram>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            RegisteredUninstaller? registration = null;
            var canRemoveRegistration = false;
            if (!entry.StoreManagedRemoval)
            {
                try
                {
                    var receipt = await _launcher.GetAsync(entry.InstallId, ct).ConfigureAwait(false);
                    if (receipt is not null)
                    {
                        canRemoveRegistration = CanForget(receipt);
                        registration = _registered.Find(receipt, entry.DisplayName);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* An unverified uninstall entry must remain unavailable. */ }
            }
            programs.Add(new InstalledProgram
            {
                DisplayName = entry.DisplayName, DisplayVersion = entry.DisplayVersion,
                InstallLocation = entry.InstallDirectory, Origin = RegistryOrigin.StoreInstall,
                SourceKeyPath = entry.InstallId, StoreManagedRemoval = entry.StoreManagedRemoval,
                CanRemoveRegistration = canRemoveRegistration,
                ProductCode = registration?.ProductCode,
                UninstallString = registration?.CommandLine,
            });
        }
        return programs.OrderBy(program => program.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(program => program.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool CanForget(InstallRecord receipt) => receipt.Origin == Origin.Store
        && receipt.State == InstallState.Done
        && receipt.Installer is InstallerKind.Unknown or InstallerKind.Nsis or InstallerKind.Inno
            or InstallerKind.Squirrel or InstallerKind.Burn or InstallerKind.Msi
        && !StoreOwnedInstallation.OwnsExtractedFiles(receipt);

    public async Task<UninstallLaunchResult> RemoveRegistrationAsync(InstalledProgram program, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ct.ThrowIfCancellationRequested();
        if (program.Origin != RegistryOrigin.StoreInstall || string.IsNullOrWhiteSpace(program.SourceKeyPath)
            || program.SourceKeyPath.Length > 256)
            return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
        try
        {
            // Explicit user intent to forget a receipt is independent of Windows registration availability.
            // Re-read ownership by ID. Never infer uninstall or delete application/user files here.
            var receipt = await _launcher.GetAsync(program.SourceKeyPath, ct).ConfigureAwait(false);
            if (receipt is null) return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
            if (!CanForget(receipt))
                return UninstallLaunchResult.Failure(UninstallErrorCodes.NotStoreOwned);
            await _launcher.UnregisterAsync(receipt.InstallId, ct).ConfigureAwait(false);
            return new UninstallLaunchResult { Started = true, Kind = UninstallRemovalKind.RegistrationRemoved };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalFailed); }
    }

    public async Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ct.ThrowIfCancellationRequested();
        if (program.Origin != RegistryOrigin.StoreInstall)
            return UninstallLaunchResult.Failure(UninstallErrorCodes.NoUninstallString);
        if (string.IsNullOrWhiteSpace(program.SourceKeyPath) || program.SourceKeyPath.Length > 256)
            return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
        try
        {
            var receipt = await _launcher.GetAsync(program.SourceKeyPath, ct).ConfigureAwait(false);
            // Re-read both the receipt and Windows registration. Never run a command provided by the UI snapshot.
            if (receipt is not null && !StoreOwnedInstallation.OwnsExtractedFiles(receipt))
                return await _registered.UninstallAsync(program.SourceKeyPath, ct).ConfigureAwait(false);
            if (!program.StoreManagedRemoval)
                return UninstallLaunchResult.Failure(UninstallErrorCodes.NoUninstallString);
            var result = await _removal.RemoveAsync(program.SourceKeyPath, ct).ConfigureAwait(false);
            return result.Removed ? UninstallLaunchResult.Removed()
                : UninstallLaunchResult.Failure(result.ErrorCode ?? UninstallErrorCodes.RemovalFailed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalFailed); }
    }
}
