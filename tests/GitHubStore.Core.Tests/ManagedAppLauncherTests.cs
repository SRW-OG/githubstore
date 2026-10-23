using System.Diagnostics;
using System.Text.Json;
using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ManagedAppLauncherTests
{
    [Fact]
    public async Task ExplicitInstallerReceiptRemovalPersistsAndPreservesAllApplicationFiles()
    {
        using var fixture = new Fixture();
        var installation = fixture.Installation("installer") with { Installer = InstallerKind.Inno, Strategy = Strategy.Custom };
        var other = fixture.Installation("other");
        var launcher = fixture.Launcher();
        await launcher.RegisterCompletedInstallAsync(installation, "Installer");
        await launcher.RegisterCompletedInstallAsync(other, "Other");
        var userFile = Path.Combine(installation.TargetDir!, "user-settings.json");
        await File.WriteAllTextAsync(userFile, "user data");
        var changed = 0;
        launcher.Changed += (_, _) => changed++;
        var registered = new GitHubStore.App.Services.Inventory.RegisteredUninstallService(launcher,
            () => [], (_, _, _) => throw new Exception("must not run"));
        var inventory = new GitHubStore.App.Services.Inventory.ManagedInstalledProgramService(launcher, registered: registered);
        var snapshot = (await inventory.ScanAsync()).Single(item => item.SourceKeyPath == installation.InstallId);
        Assert.Equal(2, (await fixture.Launcher().ListAsync()).Count);
        var result = await inventory.RemoveRegistrationAsync(snapshot);
        Assert.Equal(UninstallRemovalKind.RegistrationRemoved, result.Kind);
        Assert.Equal(other.InstallId, Assert.Single(await fixture.Launcher().ListAsync()).InstallId);
        Assert.True(File.Exists(installation.ExePath));
        Assert.True(File.Exists(other.ExePath));
        Assert.Equal("user data", await File.ReadAllTextAsync(userFile));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task EmptyLauncherDoesNotCreateAFileOrDiscoverExistingExecutables()
    {
        using var fixture = new Fixture();
        _ = fixture.Installation();
        var launcher = fixture.Launcher();
        var changed = 0;
        launcher.Changed += (_, _) => changed++;
        Assert.Empty(await launcher.ListAsync());
        await launcher.UnregisterAsync("unknown");
        Assert.False(File.Exists(fixture.Manifest));
        Assert.False(Directory.Exists(fixture.DataDirectory));
        Assert.Equal(0, changed);
        Assert.Equal("launcher_not_found", (await launcher.LaunchAsync("unknown")).ErrorCode);
    }

    [Fact]
    public async Task InstalledPageExcludesUnregisteredFoldersBesideManagedInstallations()
    {
        using var fixture = new Fixture();
        _ = fixture.Installation("external-folder");
        var receipt = fixture.Installation("registered-tool");
        var launcher = fixture.Launcher();
        var inventory = new GitHubStore.App.Services.Inventory.ManagedInstalledProgramService(launcher);
        Assert.Empty(await inventory.ScanAsync());
        await launcher.RegisterCompletedInstallAsync(receipt, "Registered Tool");
        var entry = Assert.Single(await inventory.ScanAsync());
        Assert.Equal("Registered Tool", entry.DisplayName);
        Assert.Equal(receipt.TargetDir, entry.InstallLocation);
        Assert.Equal(RegistryOrigin.StoreInstall, entry.Origin);
    }

    [Fact]
    public async Task CompletedReceiptPersistsInCamelCaseAndReopensWithTheSameIdentity()
    {
        using var fixture = new Fixture();
        var installation = fixture.Installation();
        var launcher = fixture.Launcher();
        var changed = 0;
        launcher.Changed += (_, _) => { Assert.True(File.Exists(fixture.Manifest)); changed++; };
        await launcher.RegisterCompletedInstallAsync(installation, "Tool");
        var app = Assert.Single(await fixture.Launcher().ListAsync());
        Assert.Equal(installation.AppId, app.AppId);
        Assert.Equal(installation.InstallId, app.InstallId);
        Assert.Equal(installation.TargetDir, app.InstallDirectory);
        Assert.Equal(installation.ExePath, app.ExecutablePath);
        Assert.Equal("1.0", app.DisplayVersion);
        Assert.Equal(installation.InstalledAt, app.InstalledAt);
        Assert.True(app.IsAvailable);
        Assert.Equal(1, changed);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Manifest));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var entry = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("Tool", entry.GetProperty("displayName").GetString());
        Assert.Equal(installation.InstallId, entry.GetProperty("installation").GetProperty("installId").GetString());
        Assert.False(entry.TryGetProperty("DisplayName", out _));
    }

    [Fact]
    public async Task SameInstallIdUpdatesOnlyItsOwnApp()
    {
        using var fixture = new Fixture();
        var original = fixture.Installation();
        var launcher = fixture.Launcher();
        await launcher.RegisterCompletedInstallAsync(original, "Original");
        await launcher.RegisterCompletedInstallAsync(original with { DisplayVersion = "2.0" }, "Renamed");
        var app = Assert.Single(await launcher.ListAsync());
        Assert.Equal("Renamed", app.DisplayName);
        Assert.Equal("2.0", app.DisplayVersion);
        Assert.Equal(original.InstallId, app.InstallId);
    }

    [Theory]
    [InlineData("install", "launcher_duplicate_install_id")]
    [InlineData("app", "launcher_duplicate_app")]
    [InlineData("target", "launcher_duplicate_target")]
    public async Task ConflictingIdentitiesNeverOverwriteAnotherRegistration(string collision, string code)
    {
        using var fixture = new Fixture();
        var original = fixture.Installation("one");
        var candidate = fixture.Installation("two");
        candidate = collision switch
        {
            "install" => candidate with { InstallId = original.InstallId },
            "app" => candidate with { AppId = original.AppId, Source = original.Source },
            _ => candidate with { TargetDir = original.TargetDir!.ToUpperInvariant(), ExePath = original.ExePath!.ToUpperInvariant() },
        };
        var launcher = fixture.Launcher();
        await launcher.RegisterCompletedInstallAsync(original, "One");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        Assert.Equal(code, (await Assert.ThrowsAsync<LauncherException>(() => launcher.RegisterCompletedInstallAsync(candidate, "Two"))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Single(await launcher.ListAsync());
    }

    [Theory]
    [InlineData(Origin.External, InstallState.Done)]
    [InlineData(Origin.Store, InstallState.Installing)]
    [InlineData(Origin.Store, InstallState.Failed)]
    [InlineData(Origin.Store, InstallState.Partial)]
    [InlineData(Origin.Store, InstallState.Cancelled)]
    public async Task ExternalOrIncompleteReceiptsCannotBecomeManagedApps(Origin origin, InstallState state)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation() with { Origin = origin, State = state };
        Assert.Equal("launcher_invalid_installation", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Theory]
    [InlineData("empty-install-id")]
    [InlineData("empty-source-id")]
    [InlineData("source-id")]
    [InlineData("source-package")]
    [InlineData("source-revision")]
    [InlineData("source-url")]
    [InlineData("fingerprint")]
    public async Task InconsistentReceiptIdentityIsRejected(string fault)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        receipt = fault switch
        {
            "empty-install-id" => receipt with { InstallId = " " },
            "empty-source-id" => receipt with { AppId = new AppId(default, receipt.AppId.PackageId) },
            "source-id" => receipt with { Source = receipt.Source with { SourceId = new(Guid.NewGuid()) } },
            "source-package" => receipt with { Source = receipt.Source with { PackageId = new("another-package") } },
            "source-revision" => receipt with { Source = receipt.Source with { SourceRevision = 0 } },
            "source-url" => receipt with { Source = receipt.Source with { SourceEntryUrl = new Uri("file:///C:/source.json") } },
            _ => receipt with { Fingerprint = new Fingerprint { AppId = new AppId(new(Guid.NewGuid()), new("other")) } },
        };
        Assert.Equal("launcher_invalid_installation", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
    }

    [Fact]
    public async Task MissingExecutableCannotBeInitiallyRegistered()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        fixture.DeleteFile(receipt.ExePath!);
        Assert.Equal("launcher_executable_missing", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task MissingExecutableRemainsVisibleButCannotLaunchUntilRestored()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var starts = 0;
        var launcher = fixture.Launcher((_, _) => { starts++; return true; });
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        fixture.DeleteFile(receipt.ExePath!);
        Assert.False(Assert.Single(await launcher.ListAsync()).IsAvailable);
        Assert.Equal("launcher_executable_missing", (await launcher.LaunchAsync(receipt.InstallId)).ErrorCode);
        Assert.Equal(0, starts);
        await File.WriteAllTextAsync(receipt.ExePath!, "fake test exe");
        Assert.True(Assert.Single(await launcher.ListAsync()).IsAvailable);
        Assert.True((await launcher.LaunchAsync(receipt.InstallId)).Started);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task LaunchPassesOnlyRegisteredExeAndItsOwnWorkingDirectoryToTheStarter()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation(nestedExecutable: true);
        string? receivedExe = null;
        string? receivedDirectory = null;
        var launcher = fixture.Launcher((exe, directory) => { receivedExe = exe; receivedDirectory = directory; return true; });
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        var result = await launcher.LaunchAsync(receipt.InstallId);
        Assert.True(result.Started);
        Assert.Null(result.ErrorCode);
        Assert.Equal(receipt.ExePath, receivedExe);
        Assert.Equal(Path.GetDirectoryName(receipt.ExePath), receivedDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartFailureUsesStableCodeWithoutLeakingProcessException(bool throws)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var launcher = fixture.Launcher((_, _) => throws ? throw new InvalidOperationException("sensitive arbitrary exception") : false);
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        Assert.Equal(new LauncherLaunchResult(false, "launcher_start_failed"), await launcher.LaunchAsync(receipt.InstallId));
    }

    [Theory]
    [InlineData("outside-root")]
    [InlineData("root-itself")]
    [InlineData("deep-target")]
    [InlineData("outside-exe")]
    [InlineData("unc")]
    [InlineData("traversal")]
    [InlineData("reserved")]
    [InlineData("trailing-dot")]
    [InlineData("alternate-stream")]
    [InlineData("non-exe")]
    public async Task PathsMustRemainWithinOneImmediateLocalInstallDirectory(string fault)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        receipt = fault switch
        {
            "outside-root" => receipt with { TargetDir = fixture.BaseDirectory, ExePath = Path.Combine(fixture.BaseDirectory, "tool.exe") },
            "root-itself" => receipt with { TargetDir = fixture.InstallRoot, ExePath = Path.Combine(fixture.InstallRoot, "tool.exe") },
            "deep-target" => receipt with { TargetDir = Path.Combine(receipt.TargetDir!, "child"), ExePath = Path.Combine(receipt.TargetDir!, "child", "tool.exe") },
            "outside-exe" => receipt with { ExePath = Path.Combine(fixture.BaseDirectory, "tool.exe") },
            "unc" => receipt with { TargetDir = @"\\server\share\app", ExePath = @"\\server\share\app\tool.exe" },
            "traversal" => receipt with { ExePath = Path.Combine(receipt.TargetDir!, "..", "one", "tool.exe") },
            "reserved" => receipt with { TargetDir = Path.Combine(fixture.InstallRoot, "CON"), ExePath = Path.Combine(fixture.InstallRoot, "CON", "tool.exe") },
            "trailing-dot" => receipt with { TargetDir = receipt.TargetDir + ".", ExePath = Path.Combine(receipt.TargetDir + ".", "tool.exe") },
            "alternate-stream" => receipt with { ExePath = receipt.ExePath + ":stream.exe" },
            _ => receipt with { ExePath = Path.Combine(receipt.TargetDir!, "tool.cmd") },
        };
        Assert.Equal("launcher_invalid_path", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task UnregisterRemovesOnlyTheReceiptAndIsolatesChangedSubscribers()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var launcher = fixture.Launcher();
        var changed = 0;
        launcher.Changed += (_, _) => throw new InvalidOperationException("subscriber failure");
        launcher.Changed += (_, _) => changed++;
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        await launcher.UnregisterAsync(receipt.InstallId);
        Assert.Empty(await launcher.ListAsync());
        Assert.True(File.Exists(receipt.ExePath));
        Assert.True(Directory.Exists(receipt.TargetDir));
        Assert.Equal(2, changed);
        await launcher.UnregisterAsync(receipt.InstallId);
        Assert.Equal(2, changed);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":2,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":null}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[null]}")]
    public async Task BadManifestIsNeverTreatedAsAnEmptyIndexOrOverwritten(string content)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.DataDirectory);
        await File.WriteAllTextAsync(fixture.Manifest, content);
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        var receipt = fixture.Installation();
        var launcher = fixture.Launcher();
        Assert.Equal("launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => launcher.ListAsync())).Code);
        Assert.Equal("launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => launcher.RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
        Assert.Equal("launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => launcher.UnregisterAsync("missing"))).Code);
        Assert.Equal("launcher_invalid_manifest", (await launcher.LaunchAsync("missing")).ErrorCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    [Fact]
    public async Task OversizedReadAndWritePreserveTheOriginalManifest()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var launcher = fixture.Launcher();
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        Assert.Equal("launcher_manifest_too_large", (await Assert.ThrowsAsync<LauncherException>(() =>
            launcher.RegisterCompletedInstallAsync(receipt with { UninstallString = new string('x', 1024 * 1024) }, "Tool"))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        await File.WriteAllTextAsync(fixture.Manifest, new string(' ', 1024 * 1024 + 1));
        var oversized = await File.ReadAllBytesAsync(fixture.Manifest);
        Assert.Equal("launcher_manifest_too_large", (await Assert.ThrowsAsync<LauncherException>(() => launcher.ListAsync())).Code);
        Assert.Equal("launcher_manifest_too_large", (await Assert.ThrowsAsync<LauncherException>(() => launcher.RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
        Assert.Equal(oversized, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Empty(Directory.GetFiles(fixture.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentInstancesShareTheManifestGateAndDoNotLoseRegistrations()
    {
        using var fixture = new Fixture();
        var first = fixture.Launcher();
        var second = fixture.Launcher();
        var receipts = Enumerable.Range(0, 24).Select(index => fixture.Installation("tool" + index)).ToArray();
        await Task.WhenAll(receipts.Select((receipt, index) => (index % 2 == 0 ? first : second).RegisterCompletedInstallAsync(receipt, "Tool " + index)));
        var items = await fixture.Launcher().ListAsync();
        Assert.Equal(24, items.Count);
        Assert.Equal(24, items.Select(item => item.InstallId).Distinct().Count());
        Assert.All(items, item => Assert.True(item.IsAvailable));
    }

    [Fact]
    public async Task CancellationWhileWaitingForLaunchGateCannotPublishARegistration()
    {
        using var fixture = new Fixture();
        var first = fixture.Installation("one");
        var next = fixture.Installation("two");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = fixture.Launcher((_, _) => { entered.SetResult(); return release.Wait(TimeSpan.FromSeconds(10)); });
        await launcher.RegisterCompletedInstallAsync(first, "One");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        var changes = 0;
        launcher.Changed += (_, _) => changes++;
        var launching = Task.Run(() => launcher.LaunchAsync(first.InstallId));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var pending = launcher.RegisterCompletedInstallAsync(next, "Two", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { release.Set(); }
        Assert.True((await launching).Started);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Equal(0, changes);
        Assert.Single(await launcher.ListAsync());
    }

    [Fact]
    public async Task AlreadyCancelledOperationsDoNotCreateAFileOrStartAProcess()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var starts = 0;
        var launcher = fixture.Launcher((_, _) => { starts++; return true; });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.RegisterCompletedInstallAsync(receipt, "Tool", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.ListAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.UnregisterAsync("missing", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.LaunchAsync("missing", cancellation.Token));
        Assert.Equal(0, starts);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task LaunchRereadsManifestAndRefusesTamperedPaths()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var starts = 0;
        var launcher = fixture.Launcher((_, _) => { starts++; return true; });
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.Manifest))!;
        node["entries"]![0]!["installation"]!["exePath"] = Path.Combine(fixture.BaseDirectory, "outside.exe");
        await File.WriteAllTextAsync(fixture.Manifest, node.ToJsonString());
        Assert.Equal("launcher_invalid_manifest", (await launcher.LaunchAsync(receipt.InstallId)).ErrorCode);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task DirectoryJunctionCannotRegisterOrBecomeLaunchableAfterRegistration()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Installation();
        var starts = 0;
        var launcher = fixture.Launcher((_, _) => { starts++; return true; });
        await launcher.RegisterCompletedInstallAsync(receipt, "Tool");
        var realDirectory = fixture.ValidateOwnedPath(receipt.TargetDir! + "-real");
        var target = fixture.ValidateOwnedPath(receipt.TargetDir!);
        Directory.Move(target, realDirectory);
        CreateJunction(target, realDirectory);
        Assert.True((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
        Assert.False(Assert.Single(await launcher.ListAsync()).IsAvailable);
        Assert.Equal("launcher_unsafe_path", (await launcher.LaunchAsync(receipt.InstallId)).ErrorCode);
        Assert.Equal("launcher_unsafe_path", (await Assert.ThrowsAsync<LauncherException>(() => launcher.RegisterCompletedInstallAsync(receipt, "Tool"))).Code);
        Assert.Equal(0, starts);
    }

    private static void CreateJunction(string link, string target)
    {
        // Only the test fixture invokes mklink to construct a real Windows reparse point.
        // The production launcher never invokes a shell.
        Assert.DoesNotContain('"', link);
        Assert.DoesNotContain('"', target);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create test junction.");
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            BaseDirectory = Path.Combine(Path.GetTempPath(), "launcher-tests-" + Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(BaseDirectory, "data");
            InstallRoot = Path.Combine(BaseDirectory, "apps");
            Directory.CreateDirectory(InstallRoot);
        }
        public string BaseDirectory { get; }
        public string DataDirectory { get; }
        public string InstallRoot { get; }
        public string Manifest => Path.Combine(DataDirectory, "managed-apps.json");
        public ManagedAppLauncher Launcher(Func<string, string, bool>? starter = null) => new(DataDirectory, InstallRoot, starter ?? ((_, _) => true));
        public InstallRecord Installation(string name = "one", bool nestedExecutable = false)
        {
            var target = Path.Combine(InstallRoot, name);
            var exeDirectory = nestedExecutable ? Path.Combine(target, "bin") : target;
            Directory.CreateDirectory(exeDirectory);
            var executable = Path.Combine(exeDirectory, "tool.exe");
            File.WriteAllText(executable, "fake test exe; never executed");
            var sourceId = new SourceInstanceId(new Guid("4e5bd1d2-b2d2-44de-af68-1b22b562fe71"));
            var appId = new AppId(sourceId, new PackageId("fixture/" + name));
            return new InstallRecord
            {
                InstallId = "install-" + name,
                AppId = appId,
                Source = new SourceBinding
                {
                    SourceId = sourceId, PackageId = appId.PackageId, SourceRevision = 1,
                    SourceEntryUrl = new Uri("https://example.invalid/software-source.json"), InstallerId = "portable-x64",
                },
                ReleaseKey = "stable:1.0", Origin = Origin.Store, State = InstallState.Done,
                Strategy = Strategy.Green, Installer = InstallerKind.Portable, Channel = Channel.Http,
                Version = new GitHubStore.Core.Abstractions.Version { Raw = "1.0", Scheme = VersionScheme.Semver },
                TargetDir = target, ExePath = executable, InstalledAt = DateTimeOffset.UtcNow,
            };
        }
        public string ValidateOwnedPath(string path)
        {
            var full = Path.GetFullPath(path);
            Assert.StartsWith(Path.GetFullPath(BaseDirectory) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
            return full;
        }
        public void DeleteFile(string path) => File.Delete(ValidateOwnedPath(path));
        public void Dispose()
        {
            var full = Path.GetFullPath(BaseDirectory);
            if (!Path.GetFileName(full).StartsWith("launcher-tests-", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)) return;
            try { if (Directory.Exists(full)) Directory.Delete(full, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
