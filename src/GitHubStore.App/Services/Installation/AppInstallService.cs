using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.App.Services.Installation;

public sealed record InstallerProcessRequest(string FileName, IReadOnlyList<string> Arguments,
    string WorkingDirectory, bool UseShellExecute);
public sealed record InstallerProcessResult(int ExitCode);

/// <summary>External installers are interactive and must honor the target chosen by the user.</summary>
public sealed class AppInstallService : IAppInstallService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int MaximumEntries = 10000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private readonly IDownloadService _downloads;
    private readonly IManagedAppLauncher _launcher;
    private readonly PreferencesService _preferences;
    private readonly string _dataDirectory;
    private readonly Func<InstallerProcessRequest, CancellationToken, Task<InstallerProcessResult>> _runner;
    private readonly Func<string> _root;
    private readonly PackageCacheService _packageCache;
    private readonly Func<string?> _bsdtar;

    public AppInstallService(IDownloadService downloads, IManagedAppLauncher launcher, PreferencesService preferences,
        string dataDirectory, Func<InstallerProcessRequest, CancellationToken, Task<InstallerProcessResult>>? processRunner = null,
        Func<string>? installRootProvider = null, PackageCacheService? packageCache = null,
        Func<string?>? bsdtarProvider = null)
    {
        _downloads = downloads;
        _launcher = launcher;
        _preferences = preferences;
        _dataDirectory = dataDirectory;
        _runner = processRunner ?? RunProcessAsync;
        // 7z 解压工具可注入，便于在没有该工具的机器上确定性地测试“明确失败”分支。
        _bsdtar = bsdtarProvider ?? FindBsdtar;
        _root = installRootProvider ?? (() => StorageLocations.ResolveInstallRoot(preferences.Current));
        // 缓存根与“仅下载”按钮共用同一目录，因此“先下载、后安装”能真正复用同一份文件。
        _packageCache = packageCache ?? new PackageCacheService(
            () => StorageLocations.ResolveCacheDirectory(preferences.Current, dataDirectory));
    }

    public string GetTargetDirectory(CatalogApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var root = SafePath(_root());
        var name = Regex.Replace(app.DisplayName ?? "", "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
        if (name.Length > 80) name = name[..80].TrimEnd('.', ' ');
        if (!SafeSegment(name)) name = "App_" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(app.Id.ToString())))[..16];
        return SafePath(Path.Combine(root, name));
    }

    public async Task<AppInstallResult> InstallAsync(AppInstallRequest request, IProgress<AppInstallProgress>? progress = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return new(false, "install_cancelled");
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return new(false, "install_busy");
        // packageDirectory 是安装包所在的持久缓存槽，仅用作安装器的工作目录；安装流程不会删除它。
        string? target = null, packageDirectory = null, stage = null;
        bool committed = false, externalStarted = false, externalFinished = false;
        // 从安装包本身读到的权威事实：产品码用于卸载，安装根属性用于预填向导。
        string? productCode = null;
        bool prefilledInstallRoot = false;
        var operationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var lastPhase = AppInstallPhase.Preparing;
        string? downloadErrorCode = null;
        DownloadEngine? downloadEngine = null;
        int? installerExitCode = null;
        AppInstallResult Fail(string code) => new(false, code, target, FilesMayRemain: externalStarted || committed,
            Diagnostic: new InstallDiagnostic
            {
                OperationId = operationId, AppName = request?.App?.DisplayName ?? string.Empty,
                Version = request?.Release?.TagName ?? string.Empty, AssetName = request?.Asset?.Name ?? string.Empty,
                Phase = lastPhase, ErrorCode = code, DownloadErrorCode = downloadErrorCode,
                DownloadEngine = downloadEngine, InstallerExitCode = installerExitCode,
                ExternalInstallerStarted = externalStarted, ExternalInstallerFinished = externalFinished,
                FilesMayRemain = externalStarted || committed, StartedAt = startedAt, FailedAt = DateTimeOffset.UtcNow,
            });
        try
        {
            ct.ThrowIfCancellationRequested();
            Report(AppInstallPhase.Preparing);
            ValidateRequest(request);
            var app = request.App;
            var asset = request.Asset;
            var snapshot = _preferences.Current;
            target = GetTargetDirectory(app);
            if (Exists(target)) return Fail("install_target_exists");
            if ((await _launcher.ListAsync(ct).ConfigureAwait(false)).Any(x => x.AppId == app.Id
                || string.Equals(x.InstallDirectory, target, StringComparison.OrdinalIgnoreCase))) return Fail("install_already_installed");
            var extension = Path.GetExtension(asset.Name).ToLowerInvariant();
            // .7z 由系统自带的 bsdtar 解压；缺少该工具时明确失败，不退回 ZIP 假设。
            if (extension == ".7z" && _bsdtar() is null) return Fail("install_7z_unsupported");
            // 缓存目录与安装目录必须互不包含，避免把安装产物当缓存清掉，反之亦然。
            await using var cacheLease = await _packageCache.AcquireAsync(ct).ConfigureAwait(false);
            var packageCache = cacheLease.Cache;
            var cacheRoot = SafePath(packageCache.Root);
            if (string.Equals(cacheRoot, target, StringComparison.OrdinalIgnoreCase)
                || cacheRoot.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Fail("install_unsafe_path");
            Directory.CreateDirectory(cacheRoot);
            SafePath(cacheRoot);
            var package = packageCache.TryResolvePath(new Uri(asset.DownloadUrl), asset.Name);
            if (package is null) return Fail("install_unsafe_path");
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            SafePath(package);
            packageDirectory = Path.GetDirectoryName(package)!;
            var cached = await packageCache.FindVerifiedAsync(new Uri(asset.DownloadUrl), asset.Name,
                asset.Size > 0 ? asset.Size : null, asset.Digest, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                // 已下载且校验通过：不再联网，直接复用缓存的安装包。
                downloadEngine = snapshot.DownloadEngine;
                Report(AppInstallPhase.UsingCache, 100);
            }
            else
            {
                await packageCache.PrepareDownloadAsync(new Uri(asset.DownloadUrl), asset.Name, ct).ConfigureAwait(false);
                var downloadRequest = new DownloadRequest
                {
                    Source = new Uri(asset.DownloadUrl), TargetPath = package, FileName = asset.Name,
                    ExpectedSize = asset.Size > 0 ? asset.Size : null, ExpectedSha256 = asset.Digest,
                    Engine = snapshot.DownloadEngine, UseAccelerator = snapshot.UseAccelerator,
                };
                Report(AppInstallPhase.Downloading);
                var downloadProgress = new InlineProgress<DownloadProgress>(p => Report(AppInstallPhase.Downloading, p.Total > 0 ? p.Fraction * 100 : null));
                var downloaded = _downloads is ConfiguredDownloadService configured
                    ? await configured.DownloadWithSnapshotAsync(downloadRequest, snapshot, downloadProgress, ct).ConfigureAwait(false)
                    : await _downloads.DownloadAsync(downloadRequest, downloadProgress, ct).ConfigureAwait(false);
                downloadErrorCode = SafeDownloadErrorCode(downloaded.ErrorCode);
                downloadEngine = downloaded.Engine;
                ct.ThrowIfCancellationRequested();
                if (!downloaded.Succeeded) return Fail(DownloadCode(downloaded.ErrorCode));
                SafePath(package);
                if (!File.Exists(package) || !string.Equals(downloaded.OutputPath, package, StringComparison.OrdinalIgnoreCase))
                    return Fail("install_download_failed");
            }
            var hash = await VerifyDownloadAsync(package, asset, ct).ConfigureAwait(false);
            if (cached is null)
            {
                try
                {
                    await packageCache.RecordCompletedAsync(new Uri(asset.DownloadUrl), asset.Name,
                        asset.Size > 0 ? asset.Size : null, asset.Digest, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.WriteLine("Package retained; its cache verification record could not be saved."); }
            }
            var portable = request.PackagePolicy?.InstallFormat switch
            {
                PackageInstallFormat.PortableArchive or PackageInstallFormat.PortableExecutable => true,
                PackageInstallFormat.Installer => false,
                // .zip 与 .7z 都是压缩包，必须走本程序解压后再校验入口的绿色安装路径，
                // 不能交给外部安装器去猜。
                _ => extension is ".zip" or ".7z" || asset.Kind == AssetKind.PortableExe
                    || DefaultAssetSelectionPolicy.IsRecognizedPortable(asset, $"{app.Owner}/{app.Repo}"),
            };
            string relative;
            bool restart = false;
            if (portable)
            {
                var root = SafePath(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(root);
                SafePath(root);
                stage = Path.Combine(root, ".githubstore-stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                if (extension is ".zip" or ".7z")
                {
                    Report(AppInstallPhase.Extracting);
                    // 两种格式共用同一套路径、链接与展开量保护；7z 由 bsdtar 解压后再逐项复核。
                    if (extension == ".zip") await ExtractAsync(package, stage, ct).ConfigureAwait(false);
                    else await Extract7zAsync(package, stage, _bsdtar(), ct).ConfigureAwait(false);
                }
                else File.Copy(package, Path.Combine(stage, asset.Name), overwrite: false);
                Report(AppInstallPhase.Verifying);
                relative = await SelectExecutableAsync(stage, app, chooseExecutable, ct, request.PackagePolicy?.ExecutableRelativePath).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                SafePath(target);
                if (Exists(target)) return Fail("install_target_exists");
                Directory.Move(stage, target);
                stage = null;
                committed = true;
            }
            else
            {
                if (extension == ".exe" && !IsExecutable(package)) return Fail("install_executable_invalid");
                var root = SafePath(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(root);
                SafePath(root);
                if (Exists(target)) return Fail("install_target_exists");
                ct.ThrowIfCancellationRequested();
                InstallerProcessRequest processRequest;
                if (extension == ".msi")
                {
                    // 只读检查安装包本身，找出“公开”的安装根目录属性，让向导预填应用的目标目录。
                    // 检查是防御性的：读不到就退回原来的行为（不预填、由用户在向导里选择），
                    // 绝不臆造属性名——私有属性从命令行设置无效，猜错只会静默忽略。
                    var inspection = InspectMsi(package, ct);
                    productCode = inspection.ProductCode;
                    var arguments = new List<string> { "/i", package, "/norestart" };
                    if (inspection.InstallRootProperty is { } rootProperty)
                    {
                        // 形如 INSTALL_ROOT=C:\githubapps\<app>；路径已由 SafePath 规范化。
                        arguments.Add(rootProperty + "=" + target);
                        prefilledInstallRoot = true;
                    }
                    processRequest = new InstallerProcessRequest(TrustedMsiExec(), arguments, packageDirectory, true);
                    // 读不到公开目录属性时不预填，也不谎称已预填：本地留痕，UI 文案同时说明
                    // “能预填则预填，否则请在向导中选择”。
                    if (!prefilledInstallRoot)
                        Trace.WriteLine("MSI exposes no public install-root property; the wizard is not pre-filled.");
                }
                else processRequest = new InstallerProcessRequest(package, Array.Empty<string>(), packageDirectory, true);
                Report(AppInstallPhase.RunningInstaller);
                // A runner may have started a child even if it throws: retain cache conservatively.
                externalStarted = true;
                var result = await _runner(processRequest, ct).ConfigureAwait(false);
                installerExitCode = result.ExitCode;
                externalFinished = true;
                ct.ThrowIfCancellationRequested();
                if (result.ExitCode is 1602 or 1223) return Fail("install_installer_cancelled");
                if (result.ExitCode is not (0 or 3010)) return Fail("install_installer_failed");
                restart = result.ExitCode == 3010;
                Report(AppInstallPhase.Verifying);
                if (!Directory.Exists(target)) return Fail("install_target_not_honored");
                relative = await SelectExecutableAsync(target, app, chooseExecutable, ct, request.PackagePolicy?.ExecutableRelativePath).ConfigureAwait(false);
            }
            var executable = SafePath(Path.Combine(target, relative));
            if (!IsExecutable(executable)) return Fail("install_executable_invalid");
            ct.ThrowIfCancellationRequested();
            Report(AppInstallPhase.Registering);
            var now = DateTimeOffset.UtcNow;
            var receipt = new InstallRecord
            {
                InstallId = Guid.NewGuid().ToString("N"), AppId = app.Id,
                Source = new SourceBinding
                {
                    SourceId = app.Id.SourceId, PackageId = app.Id.PackageId, SourceRevision = app.SourceRevision,
                    SourceEntryUrl = new Uri($"https://github.com/{app.Owner}/{app.Repo}"),
                    InstallerId = asset.InstallerId, ArtifactUrl = new Uri(asset.DownloadUrl), ArtifactSha256 = hash,
                },
                ReleaseKey = request.Release.Key, Origin = Origin.Store, Channel = Channel.Http,
                Strategy = portable ? Strategy.Green : Strategy.Custom,
                Installer = portable ? InstallerKind.Portable : extension == ".msi" ? InstallerKind.Msi : asset.Installer,
                Version = new GitHubStore.Core.Abstractions.Version { Raw = request.Release.TagName, Scheme = VersionScheme.Opaque },
                DisplayVersion = request.Release.TagName, TargetDir = target, ExePath = executable,
                // 产品码来自我们本次下载并校验过的那个 MSI：它是卸载的唯一权威依据，
                // 不是从别处“发现”或猜出来的卸载命令。第三方安装程序不会写入此字段。
                ProductCode = productCode,
                Fingerprint = new Fingerprint { AppId = app.Id, AssetSha256 = hash }, TrustLevel = TrustLevel.Unverified,
                InstalledAt = now, UpdatedAt = now, State = InstallState.Done,
            };
            try { await _launcher.RegisterCompletedInstallAsync(receipt, app.DisplayName, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return Fail("install_registration_failed"); }
            Report(AppInstallPhase.Completed, 100);
            return new(true, TargetDirectory: target, ExecutablePath: executable, RequiresRestart: restart);
        }
        catch (OperationCanceledException) { return Fail(externalStarted && !externalFinished ? "install_installer_cancelled" : "install_cancelled"); }
        catch (InstallException ex) { return Fail(ex.Code); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return Fail("install_installer_cancelled"); }
        catch (InvalidDataException) { return Fail("install_invalid_archive"); }
        catch (UnauthorizedAccessException) { return Fail("install_access_denied"); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return Fail("install_io_failed"); }
        catch (Exception) { return Fail(externalStarted ? "install_installer_failed" : "install_failed"); }
        finally
        {
            // Retain committed portable targets on registration failure/cancel and report FilesMayRemain.
            // Never delete a target: an installer or another process may have changed its ownership.
            if (stage is not null) CleanupOwned(stage);
            // 下载的安装包一律保留：这样再次安装同一版本可以直接复用，不再重复下载。
            // 删除缓存的唯一入口是设置页的“清空下载缓存”，只能由用户显式触发。
            Gate.Release();
        }
        void Report(AppInstallPhase phase, double? percent = null)
        {
            lastPhase = phase;
            try { progress?.Report(new(phase, percent)); }
            catch (Exception) { /* Observers cannot change installation ownership or outcome. */ }
        }
    }

    private static void ValidateRequest(AppInstallRequest request)
    {
        if (request?.App is not { } app || request.Release is not { } release || request.Asset is not { } asset
            || app.Id.SourceId.Value == Guid.Empty || string.IsNullOrWhiteSpace(app.Id.PackageId.Value)
            || app.SourceRevision <= 0 || string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName.Length > 200
            || !Regex.IsMatch(app.Owner ?? "", "^[A-Za-z0-9-]+$") || !Regex.IsMatch(app.Repo ?? "", "^[A-Za-z0-9_.-]+$")
            || app.Repo is "." or ".." || release.AppId != app.Id || release.Draft
            || string.IsNullOrWhiteSpace(release.Key) || release.Key.Length > 256
            || string.IsNullOrWhiteSpace(release.TagName) || release.TagName.Length > 256
            || asset.ReleaseKey != release.Key || string.IsNullOrWhiteSpace(asset.InstallerId) || asset.InstallerId.Length > 256
            || release.Assets.Count(a => a == asset) != 1 || release.Assets.Count(a => a.InstallerId == asset.InstallerId) != 1
            || !SafeSegment(asset.Name) || asset.Size < 0
            || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InstallException("install_invalid_request");
        var extension = Path.GetExtension(asset.Name).ToLowerInvariant();
        if (extension is not (".zip" or ".7z" or ".exe" or ".msi")) throw new InstallException("install_unsupported_asset");
        if (request.PackagePolicy is { } policy)
        {
            try { PackagePolicyValidator.Validate(policy); }
            catch (RepositoryCatalogException) { throw new InstallException("install_invalid_request"); }
            if (!Enum.IsDefined(policy.InstallFormat)
                || policy.InstallFormat == PackageInstallFormat.PortableArchive && extension is not (".zip" or ".7z")
                || policy.InstallFormat == PackageInstallFormat.PortableExecutable && extension != ".exe"
                || policy.InstallFormat == PackageInstallFormat.Installer && extension is not (".exe" or ".msi"))
                throw new InstallException("install_invalid_request");
            if (policy.ExecutableRelativePath is { } entry
                && (entry.Replace('\\', '/').Split('/').Any(part => !SafeSegment(part))
                    || !Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InstallException("install_invalid_request");
        }
        var architecture = RuntimeInformation.OSArchitecture switch
        { Architecture.Arm64 => Arch.Arm64, Architecture.X86 => Arch.X86, _ => Arch.Amd64 };
        // Policy rejects auxiliary, incompatible OS and conflicting architecture names.
        // Manual generic archives are eligible after portable metadata is added for this validation only.
        var probe = extension is ".zip" or ".7z"
            ? asset with { Name = Path.GetFileNameWithoutExtension(asset.Name) + "-windows-portable" + extension, Installer = InstallerKind.Portable }
            : asset;
        // 7z 已由 bsdtar 支持；仍要求本机存在该工具，否则在下载前就明确失败。
        if (DefaultAssetSelectionPolicy.Select(new[] { probe }, architecture, $"{app.Owner}/{app.Repo}") is null)
            throw new InstallException("install_incompatible_asset");
        if (!string.IsNullOrWhiteSpace(asset.Digest)) _ = NormalizeDigest(asset.Digest);
    }

    private static async Task<string> VerifyDownloadAsync(string package, Asset asset, CancellationToken ct)
    {
        await using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length == 0 || asset.Size > 0 && stream.Length != asset.Size) throw new InstallException("install_length_mismatch");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(asset.Digest) && !string.Equals(hash, NormalizeDigest(asset.Digest), StringComparison.OrdinalIgnoreCase))
            throw new InstallException("install_checksum_mismatch");
        return hash;
    }

    private static string NormalizeDigest(string value)
    {
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (!Regex.IsMatch(value, "^[a-fA-F0-9]{64}$")) throw new InstallException("install_invalid_digest");
        return value;
    }

    private static async Task ExtractAsync(string package, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > MaximumEntries) throw new InstallException("install_archive_limit");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var parts = (directory ? name[..^1] : name).Split('/');
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (parts.Any(p => !SafeSegment(p)) || mode is 0xa000 or 0x6000 or 0x2000 or 0x1000 or 0xc000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || !paths.Add(string.Join('/', parts))) throw new InstallException("install_unsafe_archive");
            if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - total) throw new InstallException("install_archive_limit");
            total += entry.Length;
            var destination = SafePath(Path.Combine(stage, Path.Combine(parts)));
            if (directory) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePath(destination);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InstallException("install_archive_limit");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InstallException("install_invalid_archive");
        }
    }

    /// <summary>
    /// 用系统自带的 bsdtar（libarchive）解压 7z。找不到该工具时返回 null，调用方据此明确失败。
    /// 只信任 System32 下的固定路径，不使用 PATH，避免解析到被替换的可执行文件。
    /// </summary>
    private static string? FindBsdtar()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrEmpty(system)) return null;
        var candidate = Path.Combine(system, "tar.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 7z 解压：先让 bsdtar 只列出条目名做路径安全检查，再解压到隔离的临时目录，
    /// 最后逐项校验并搬入 stage。ZIP 已有的路径、链接、重复名和展开量保护在这里同样生效，
    /// 不能因为换了格式就跳过任何一项。
    /// </summary>
    private static async Task Extract7zAsync(string package, string stage, string? tar, CancellationToken ct)
    {
        if (tar is null) throw new InstallException("install_7z_unsupported");
        SafePath(package);
        // 解压到 stage 的同级临时目录，全部校验通过后再搬入，避免半成品被当成安装结果。
        var scratch = Path.Combine(Path.GetDirectoryName(stage)!, ".githubstore-7z-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            // 先列出：条目名与大小在解压前校验，恶意归档不会先落盘。
            var listing = await RunCaptureAsync(tar, ["-tf", package], ct).ConfigureAwait(false);
            if (listing.ExitCode != 0) throw new InstallException("install_invalid_archive");
            var names = listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).ToArray();
            if (names.Length > MaximumEntries) throw new InstallException("install_archive_limit");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                var normalized = name.Replace('\\', '/').TrimEnd('/');
                if (normalized.Length == 0) continue;
                var parts = normalized.Split('/');
                // 与 ZIP 相同的逐段校验：绝对路径、上跳、保留名、ADS、结尾点或空格一律拒绝。
                if (parts.Any(part => !SafeSegment(part)) || normalized.Contains(':', StringComparison.Ordinal)
                    || !seen.Add(string.Join('/', parts)))
                    throw new InstallException("install_unsafe_archive");
            }
            var extract = await RunCaptureAsync(tar, ["-xf", package, "-C", scratch], ct).ConfigureAwait(false);
            if (extract.ExitCode != 0) throw new InstallException("install_invalid_archive");
            await CommitExtractedAsync(scratch, stage, ct).ConfigureAwait(false);
        }
        finally { CleanupOwned(scratch); }
    }

    /// <summary>
    /// 把已验证的临时目录内容搬入 stage，并复核 bsdtar 可能创建的链接与展开量。
    /// 这里不直接信任解压结果：符号链接、重解析点和超量文件都在提交前拒绝。
    /// </summary>
    private static async Task CommitExtractedAsync(string scratch, string stage, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(SafePath(scratch));
        var visited = 0;
        long total = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var item in Directory.EnumerateFileSystemEntries(current))
            {
                if (++visited > MaximumEntries) throw new InstallException("install_archive_limit");
                SafePath(item);
                var attributes = File.GetAttributes(item);
                // 任何链接都可能把写入引向 stage 之外，一律拒绝而不是跟随。
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InstallException("install_unsafe_archive");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(item); continue; }
                var length = new FileInfo(item).Length;
                if (length < 0 || length > MaximumExpandedBytes - total) throw new InstallException("install_archive_limit");
                total += length;
                var relative = Path.GetRelativePath(scratch, item);
                var destination = SafePath(Path.Combine(stage, relative));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(item, destination, overwrite: false);
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>运行 bsdtar 并捕获输出；只用于格式识别与解压，不经过 shell。</summary>
    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(string fileName, string[] arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InstallException("install_invalid_archive");
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, output);
    }

    private static async Task<string> SelectExecutableAsync(string directory, CatalogApp app,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? choose, CancellationToken ct,
        string? configuredRelativePath = null)
    {
        if (configuredRelativePath is not null)
        {
            ct.ThrowIfCancellationRequested();
            var parts = configuredRelativePath.Replace('\\', '/').Split('/');
            if (parts.Any(part => !SafeSegment(part))
                || !Path.GetExtension(configuredRelativePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InstallException("install_invalid_request");
            var configured = SafePath(Path.Combine(directory, Path.Combine(parts)));
            if (!File.Exists(configured)) throw new InstallException("install_executable_missing");
            if (!IsExecutable(configured)) throw new InstallException("install_executable_invalid");
            return Path.Combine(parts);
        }
        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(SafePath(directory));
        var visited = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++visited > MaximumEntries) throw new InstallException("install_archive_limit");
                SafePath(item);
                if (Directory.Exists(item)) { pending.Push(item); continue; }
                var relative = Path.GetRelativePath(directory, item);
                if (Path.GetExtension(item).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(relative, @"(?i)(^|[\\/_.\- ])(unins\w*|setup|install(?:er)?|update(?:r)?|crash\w*|helper|uninstall\w*)(?=$|[\\/_.\- ])")
                    && IsExecutable(item)) candidates.Add(relative);
            }
        }
        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0) throw new InstallException("install_executable_missing");
        if (candidates.Count == 1) return candidates[0];
        static string Key(string value) => Regex.Replace(value, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        var strong = candidates.Where(x => Key(Path.GetFileNameWithoutExtension(x)) == Key(app.DisplayName)
            || Key(Path.GetFileNameWithoutExtension(x)) == Key(app.Repo)).ToArray();
        if (strong.Length == 1) return strong[0];
        if (choose is null) throw new InstallException("install_executable_choice_required");
        var selected = await choose(candidates.AsReadOnly(), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (selected is null) throw new InstallException("install_cancelled");
        if (!candidates.Contains(selected, StringComparer.Ordinal)) throw new InstallException("install_invalid_executable_choice");
        return selected;
    }

    private static bool IsExecutable(string path)
    {
        SafePath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length < 64 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false;
        stream.Position = 60;
        Span<byte> offsetBytes = stackalloc byte[4];
        if (stream.Read(offsetBytes) != 4) return false;
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(offsetBytes);
        if (offset < 64 || offset > stream.Length - 24) return false;
        stream.Position = offset;
        Span<byte> header = stackalloc byte[24];
        if (stream.Read(header) != 24 || header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0) return false;
        var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        return (flags & 2) != 0 && (flags & 0x2000) == 0;
    }

    private static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)) throw new InstallException("install_unsafe_path");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var rawRoot = Path.GetPathRoot(path)!;
        if (path[rawRoot.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Length > 0 && !SafeSegment(p)))
            throw new InstallException("install_unsafe_path");
        if (full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Length > 0 && !SafeSegment(p)))
            throw new InstallException("install_unsafe_path");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InstallException("install_unsafe_path"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static bool SafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..")
        && !value.EndsWith('.') && !value.EndsWith(' ') && value.Length <= 255
        && !Regex.IsMatch(value, "[<>:\"/\\\\|?*\\x00-\\x1f]")
        && !Regex.IsMatch(value.Split('.')[0].TrimEnd(' '), "^(CON|PRN|AUX|NUL|CLOCK\\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase);
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void CleanupOwned(string path)
    {
        try
        {
            SafePath(path);
            if (!Directory.Exists(path)) return;
            foreach (var item in Directory.EnumerateFileSystemEntries(path))
            {
                SafePath(item);
                if (Directory.Exists(item)) CleanupOwned(item); else File.Delete(item);
            }
            Directory.Delete(path, false);
        }
        catch (Exception) { /* Never traverse reparse points or mask installation outcome. */ }
    }
    private static string TrustedMsiExec()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrEmpty(system)) throw new InstallException("install_platform_unsupported");
        return SafePath(Path.Combine(system, "msiexec.exe"));
    }

    /// <summary>
    /// 只读检查下载好的 MSI，绝不因为它读不出来就让安装失败：文件被占用、损坏、COM 不可用，
    /// 都退化成“什么都没读到”，安装继续按原来的向导流程走。取消是唯一会向上传播的信号。
    /// </summary>
    private static MsiPackageInspection InspectMsi(string package, CancellationToken ct)
    {
        try { return MsiPackageInspector.Inspect(package, ct: ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 发现的失败一律降级为“没有可预填的目录属性、没有产品码”，不影响安装本身。
            return MsiPackageInspection.Failed("msi_inspection_failed");
        }
    }
    private static async Task<InstallerProcessResult> RunProcessAsync(InstallerProcessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo { FileName = request.FileName, WorkingDirectory = request.WorkingDirectory, UseShellExecute = request.UseShellExecute };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InstallException("install_installer_failed");
        // Disposing releases the handle. Cancellation deliberately does not terminate installers.
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new(process.ExitCode);
    }
    private static string? SafeDownloadErrorCode(string? code) => code switch
    {
        null => null,
        DownloadErrorCodes.NoTargetDirectory or DownloadErrorCodes.TargetNotWritable
            or DownloadErrorCodes.UnsupportedSource or DownloadErrorCodes.NetworkError
            or DownloadErrorCodes.HttpStatusError or DownloadErrorCodes.Cancelled
            or DownloadErrorCodes.Aria2NotConfigured or DownloadErrorCodes.Aria2NotFound
            or DownloadErrorCodes.Aria2Failed or DownloadErrorCodes.Aria2TimedOut
            or DownloadErrorCodes.InvalidPeerSource or DownloadErrorCodes.PeerNotSupported
            or DownloadErrorCodes.InvalidResponse or DownloadErrorCodes.LengthMismatch
            or DownloadErrorCodes.ChecksumMismatch or DownloadErrorCodes.InvalidVerification => code,
        _ => "download_failed",
    };
    private static string DownloadCode(string? code) => code switch
    {
        DownloadErrorCodes.Cancelled => "install_cancelled",
        DownloadErrorCodes.ChecksumMismatch => "install_checksum_mismatch",
        DownloadErrorCodes.LengthMismatch => "install_length_mismatch",
        DownloadErrorCodes.InvalidVerification => "install_invalid_digest",
        _ => "install_download_failed",
    };
    private sealed class InstallException(string code) : Exception(code) { public string Code { get; } = code; }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
