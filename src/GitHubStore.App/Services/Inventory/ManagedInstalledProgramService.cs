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
            // 默认按“无法核对”处理：读取失败与确实没有登记必须能区分开，不能都显示成未登记。
            var registrationStatus = UninstallRegistrationStatus.Unknown;
            if (!entry.StoreManagedRemoval)
            {
                try
                {
                    var receipt = await _launcher.GetAsync(entry.InstallId, ct).ConfigureAwait(false);
                    if (receipt is not null)
                    {
                        canRemoveRegistration = CanForget(receipt);
                        registration = _registered.Find(receipt, entry.DisplayName);
                        registrationStatus = registration is null
                            ? UninstallRegistrationStatus.NotFound : UninstallRegistrationStatus.Available;
                    }
                    else registrationStatus = UninstallRegistrationStatus.NotFound;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* 读取失败：保持 Unknown，界面必须显示“无法核对”。 */ }
            }
            programs.Add(new InstalledProgram
            {
                DisplayName = entry.DisplayName, DisplayVersion = entry.DisplayVersion,
                InstallLocation = entry.InstallDirectory, Origin = RegistryOrigin.StoreInstall,
                SourceKeyPath = entry.InstallId, StoreManagedRemoval = entry.StoreManagedRemoval,
                // 启动器读取登记时已经核对过入口文件；这里原样传递，不重新推断。
                Status = entry.Status,
                CanRemoveRegistration = canRemoveRegistration,
                RegistrationStatus = registrationStatus,
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

    /// <summary>
    /// 只有入口缺失的登记才可修复；读取失败或未登记时拒绝，避免把“无法核对”当成可修复。
    /// 路径校验与写入都由启动器按安装 ID 重读登记后完成，界面传入的路径不能授予任何权限。
    /// </summary>
    public async Task<UninstallLaunchResult> RepairExecutableAsync(InstalledProgram program, string executablePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (string.IsNullOrWhiteSpace(executablePath)) return UninstallLaunchResult.Failure(UninstallErrorCodes.ExecutableNotFound);
        ct.ThrowIfCancellationRequested();
        if (program.Origin != RegistryOrigin.StoreInstall || string.IsNullOrWhiteSpace(program.SourceKeyPath)
            || program.SourceKeyPath.Length > 256)
            return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
        try
        {
            var receipt = await _launcher.GetAsync(program.SourceKeyPath, ct).ConfigureAwait(false);
            // 登记必须仍然存在且身份一致：修复不能作用到另一条安装上。
            if (receipt is null) return UninstallLaunchResult.Failure(UninstallErrorCodes.InstallationNotFound);
            var result = await _launcher.RepairExecutableAsync(receipt.InstallId, receipt.AppId, executablePath, ct).ConfigureAwait(false);
            return result.Started ? new UninstallLaunchResult { Started = true, Kind = UninstallRemovalKind.RepairApplied }
                : UninstallLaunchResult.Failure(MapRepairError(result.ErrorCode));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return UninstallLaunchResult.Failure(UninstallErrorCodes.RemovalFailed); }
    }

    private static string MapRepairError(string? code) => code switch
    {
        "launcher_executable_missing" => UninstallErrorCodes.ExecutableNotFound,
        "launcher_executable_invalid" => UninstallErrorCodes.InvalidExecutable,
        "launcher_invalid_path" or "launcher_unsafe_path" => UninstallErrorCodes.UnsafeRemovalPath,
        "launcher_not_found" => UninstallErrorCodes.InstallationNotFound,
        _ => UninstallErrorCodes.RemovalFailed,
    };

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
