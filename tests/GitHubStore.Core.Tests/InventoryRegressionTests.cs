using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>所有注册表读取和启动均为 fake；exe 是临时空文件，绝不执行。</summary>
[Trait("Category", "Unit")]
public sealed class InventoryRegressionTests
{
    [Fact]
    public async Task Scan_KeepsSameNameInstallationsFromDifferentKeysAndViews()
    {
        var first = Program("A", RegistryOrigin.Hklm64);
        var second = Program("B", RegistryOrigin.Hklm64);
        var third = Program("A", RegistryOrigin.Hklm32);
        var service = Service(() => null, [first, second, third], new FakeLauncher());

        var programs = await service.ScanAsync();

        Assert.Equal(3, programs.Count);
        Assert.Equal(3, programs.Select(p => p.Id).Distinct().Count());
        Assert.All(programs, p => Assert.Equal(1, p.DuplicateCount));
    }

    [Fact]
    public async Task Scan_MapsDirectoryToRegistryOnlyWhenInstallPathMatches()
    {
        using var temp = new TemporaryDirectory();
        var registeredPath = temp.Directory("Registered");
        var separatePath = temp.Directory("Same name");
        var registered = Program("A") with { InstallLocation = registeredPath + Path.DirectorySeparatorChar };
        var service = Service(() => temp.Root, [registered], new FakeLauncher());

        var programs = await service.ScanAsync();

        Assert.Equal(2, programs.Count);
        Assert.Equal(2, programs.Single(p => p.Id == registered.Id).DuplicateCount);
        Assert.Equal(separatePath, programs.Single(p => p.Origin == RegistryOrigin.GreenInstall).InstallLocation);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"version\"")]
    [InlineData("{broken")]
    [InlineData("{\"version\":42}")]
    public async Task Scan_BadOptionalVersionDoesNotDiscardTheEntryOrOtherDirectories(string json)
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Directory("Broken"), "version.json"), json);
        File.WriteAllText(Path.Combine(temp.Directory("Valid"), "version.json"), "{\"version\":\"2.3.4\"}");
        var service = Service(() => temp.Root, [], new FakeLauncher());

        var programs = await service.ScanAsync();

        Assert.Equal(2, programs.Count);
        Assert.Null(programs.Single(p => p.DisplayName == "Broken").DisplayVersion);
        Assert.Equal("2.3.4", programs.Single(p => p.DisplayName == "Valid").DisplayVersion);
        Assert.All(programs, p => Assert.False(p.CanUninstall));
    }

    [Fact]
    public async Task Scan_VersionFileLimitIs64KiBOfBytes()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Directory("TooLarge"), "version.json");
        File.WriteAllText(path, "{\"version\":\"" + new string('界', 24_000) + "\"}");
        Assert.True(new FileInfo(path).Length > 64 * 1024);
        var service = Service(() => temp.Root, [], new FakeLauncher());

        Assert.Null(Assert.Single(await service.ScanAsync()).DisplayVersion);
    }

    [Fact]
    public async Task Scan_ReadsUpdatedInstallRootOnEveryScan()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        first.Directory("First");
        second.Directory("Second");
        string? root = first.Root;
        var reads = 0;
        var service = Service(() => { reads++; return root; }, [], new FakeLauncher());

        Assert.Equal("First", Assert.Single(await service.ScanAsync()).DisplayName);
        root = second.Root;
        Assert.Equal("Second", Assert.Single(await service.ScanAsync()).DisplayName);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task QuietOnlyEntry_NeverInvokesLauncher(string? normal)
    {
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);
        var program = Program("A") with { UninstallString = normal, QuietUninstallString = "msiexec /x {GUID} /qn" };

        var result = await service.LaunchUninstallAsync(program);

        Assert.False(program.CanUninstall);
        Assert.False(result.Started);
        Assert.Equal(UninstallErrorCodes.NoUninstallString, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Fact]
    public async Task InvalidNormalCommand_DoesNotFallBackToAnExistingQuietExecutable()
    {
        using var temp = new TemporaryDirectory();
        var exe = temp.File("uninstall.exe");
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);
        var program = Program("A") with { UninstallString = "\"\"", QuietUninstallString = $"\"{exe}\"" };

        var result = await service.LaunchUninstallAsync(program);

        Assert.Equal(UninstallErrorCodes.InvalidUninstallCommand, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Fact]
    public async Task UnquotedSpacePath_DoesNotLaunchExistingPrefixFile()
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.Directory("App Folder");
        var exe = Path.Combine(directory, "uninstall.exe");
        File.WriteAllText(exe, string.Empty);
        File.WriteAllText(Path.Combine(temp.Root, "App"), "prefix must never run");
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);

        var result = await service.LaunchUninstallAsync(Program("A") with { UninstallString = exe + " /remove" });

        Assert.Equal(UninstallErrorCodes.InvalidUninstallCommand, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Theory]
    [InlineData("uninstall.exe /remove")]
    [InlineData("relative/uninstall.exe /remove")]
    [InlineData("cmd.exe /c echo hello")]
    [InlineData("script.cmd /remove")]
    [InlineData("https://example.invalid/uninstall.exe")]
    [InlineData("\"\" /remove")]
    [InlineData("\"C:\\App\\uninstall.exe /remove")]
    public async Task UnsafeCommands_AreRejectedWithoutLauncherInvocation(string commandLine)
    {
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);

        var result = await service.LaunchUninstallAsync(Program("A") with { UninstallString = commandLine });

        Assert.Equal(UninstallErrorCodes.InvalidUninstallCommand, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("wscript.exe")]
    [InlineData("cscript.exe")]
    [InlineData("mshta.exe")]
    public async Task AbsoluteInterpreter_IsRejectedEvenWhenItExists(string fileName)
    {
        using var temp = new TemporaryDirectory();
        var exe = temp.File(fileName);
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);

        var result = await service.LaunchUninstallAsync(Program("A") with { UninstallString = $"\"{exe}\" /remove" });

        Assert.Equal(UninstallErrorCodes.InvalidUninstallCommand, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Theory]
    [InlineData("/S")]
    [InlineData("/qn")]
    [InlineData("/qn+")]
    [InlineData("/quiet")]
    [InlineData("/VERYSILENT")]
    [InlineData("--silent")]
    [InlineData("--silent=true")]
    [InlineData("--quiet=1")]
    [InlineData("--unattended")]
    [InlineData("-q")]
    [InlineData("/passive")]
    [InlineData("\"/quiet\"")]
    public async Task NormalCommandWithKnownSilentSwitch_IsRejectedWithoutRewriting(string arguments)
    {
        using var temp = new TemporaryDirectory();
        var exe = temp.File("uninstall.exe");
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);

        var result = await service.LaunchUninstallAsync(Program("A") with { UninstallString = $"\"{exe}\" {arguments}" });

        Assert.Equal(UninstallErrorCodes.InvalidUninstallCommand, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Fact]
    public async Task MissingAbsoluteExe_ReturnsSpecificError()
    {
        using var temp = new TemporaryDirectory();
        var launcher = new FakeLauncher();
        var service = Service(() => null, [], launcher);
        var commandLine = $"\"{Path.Combine(temp.Root, "missing.exe")}\" /remove";

        var result = await service.LaunchUninstallAsync(Program("A") with { UninstallString = commandLine });

        Assert.Equal(UninstallErrorCodes.ExecutableNotFound, result.ErrorCode);
        Assert.Empty(launcher.Commands);
    }

    [Fact]
    public async Task ValidAbsoluteExe_PreservesArgumentsAndLogsStartedOnly()
    {
        using var temp = new TemporaryDirectory();
        var exe = temp.File("uninstall.exe");
        const string arguments = "/remove /label:\"a b\"  --custom=1 & value";
        var log = new OperationLog(temp.Root);
        var launcher = new FakeLauncher();
        var service = new InstalledProgramService(() => null, log, _ => [], launcher);

        var result = await service.LaunchUninstallAsync(Program("A") with { UninstallString = $"\"{exe}\"\t{arguments}" });

        Assert.True(result.Started);
        var launched = Assert.Single(launcher.Commands);
        Assert.Equal(exe, launched.FileName);
        Assert.Equal(arguments, launched.Arguments);
        var entry = Assert.Single(await log.ReadAsync(10));
        Assert.Equal("started", entry.Outcome);
        Assert.Equal(OperationLog.ActionUninstall, entry.Action);
    }

    [Fact]
    public void MsiExecAlias_ResolvesOnlyFromTrustedSystemDirectory()
    {
        using var system = new TemporaryDirectory();
        using var impostor = new TemporaryDirectory();
        var trustedExe = system.File("msiexec.exe");
        var otherExe = impostor.File("msiexec.exe");
        var validator = new UninstallCommandValidator(system.Root);

        Assert.True(validator.TryValidate("MsiExec.exe /X{GUID}", out var command, out var error));
        Assert.Null(error);
        Assert.Equal(trustedExe, command!.FileName);
        Assert.Equal("/X{GUID}", command.Arguments);
        Assert.False(validator.TryValidate($"\"{otherExe}\" /X{{GUID}}", out _, out error));
        Assert.Equal(UninstallErrorCodes.InvalidUninstallCommand, error);
        Assert.False(validator.TryValidate("msiexec /X{GUID} /qn", out _, out error));
    }

    [Fact]
    public async Task ViewModel_IsBusyNotifiesCanUninstallInBothDirections()
    {
        var program = Program("A");
        var completion = Completion<IReadOnlyList<InstalledProgram>>();
        var service = new FakeInventory { Scan = _ => completion.Task };
        using var vm = new InstalledViewModel(service, new LocalizationService());
        vm.SelectedProgram = program;
        var values = new List<bool>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CanUninstall)) values.Add(vm.CanUninstall); };

        var scan = vm.RescanCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanUninstall);
        completion.SetResult([program]);
        await scan;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CanUninstall);
        Assert.Equal(new[] { false, true }, values);
    }

    [Fact]
    public async Task ViewModel_RestoresSelectionByInstallIdInsteadOfSameName()
    {
        var first = Program("A");
        var second = Program("B");
        var service = new FakeInventory { Scan = _ => Task.FromResult<IReadOnlyList<InstalledProgram>>([first, second]) };
        using var vm = new InstalledViewModel(service, new LocalizationService());
        await vm.InitializeAsync();
        vm.SelectedProgram = second;

        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Equal(second.Id, vm.SelectedProgram!.Id);
        service.Scan = _ => Task.FromResult<IReadOnlyList<InstalledProgram>>([first]);
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedProgram);
    }

    [Fact]
    public async Task ViewModel_NewerScanCancelsAndSupersedesOldResults()
    {
        var oldCompletion = Completion<IReadOnlyList<InstalledProgram>>();
        var newCompletion = Completion<IReadOnlyList<InstalledProgram>>();
        var count = 0;
        CancellationToken oldToken = default;
        var service = new FakeInventory
        {
            Scan = ct => ++count == 1 ? CaptureOld(ct) : newCompletion.Task,
        };
        Task<IReadOnlyList<InstalledProgram>> CaptureOld(CancellationToken ct)
        {
            oldToken = ct;
            return oldCompletion.Task; // 刻意模拟未及时响应取消的来源。
        }
        using var vm = new InstalledViewModel(service, new LocalizationService());
        var oldScan = vm.RescanCommand.ExecuteAsync(null);
        var newScan = vm.RescanCommand.ExecuteAsync(null);
        Assert.True(oldToken.IsCancellationRequested);
        newCompletion.SetResult([Program("new") with { DisplayName = "New result" }]);
        await newScan;
        var status = vm.StatusMessage;
        oldCompletion.SetResult([Program("old") with { DisplayName = "Old result" }]);
        await oldScan;

        Assert.Equal("New result", Assert.Single(vm.Programs).DisplayName);
        Assert.Equal(status, vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ViewModel_ConfirmedSnapshotSurvivesSelectionChangeAndPreventsReentry()
    {
        var confirmed = Program("A");
        var other = Program("B");
        var completion = Completion<UninstallLaunchResult>();
        var service = new FakeInventory { Launch = (_, _) => completion.Task };
        using var vm = new InstalledViewModel(service, new LocalizationService());
        vm.SelectedProgram = other; // 对话框期间用户选择已发生变化。

        var first = vm.UninstallConfirmedAsync(confirmed);
        await vm.UninstallConfirmedAsync(other);
        await vm.UninstallCommand.ExecuteAsync(null);
        Assert.Equal(confirmed, Assert.Single(service.Launched));
        completion.SetResult(UninstallLaunchResult.Success("fake"));
        await first;

        Assert.False(vm.IsBusy);
        Assert.Equal(confirmed, Assert.Single(service.Launched));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewModel_DisposeCancelsAndPreventsLateStateWrites(bool uninstall)
    {
        var scanCompletion = Completion<IReadOnlyList<InstalledProgram>>();
        var launchCompletion = Completion<UninstallLaunchResult>();
        CancellationToken operationToken = default;
        var service = new FakeInventory
        {
            Scan = ct => { operationToken = ct; return scanCompletion.Task; },
            Launch = (_, ct) => { operationToken = ct; return launchCompletion.Task; },
        };
        var vm = new InstalledViewModel(service, new LocalizationService());
        var operation = uninstall ? vm.UninstallConfirmedAsync(Program("A")) : vm.RescanCommand.ExecuteAsync(null);
        vm.Dispose();
        Assert.True(operationToken.IsCancellationRequested);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        launchCompletion.SetResult(UninstallLaunchResult.Success("fake"));
        scanCompletion.SetResult([Program("late")]);
        await operation;

        Assert.Empty(changes);
        Assert.Empty(vm.Programs);
    }

    [Fact]
    public async Task ViewModel_PreCancelledScanDoesNotRemainBusyOrInvokeService()
    {
        var calls = 0;
        var service = new FakeInventory { Scan = _ => { calls++; return Task.FromResult<IReadOnlyList<InstalledProgram>>([]); } };
        using var vm = new InstalledViewModel(service, new LocalizationService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await vm.InitializeAsync(cancellation.Token);

        Assert.False(vm.IsBusy);
        Assert.Equal(0, calls);
    }

    private static InstalledProgramService Service(Func<string?> root, IReadOnlyList<InstalledProgram> registry,
        IUninstallLauncher launcher) => new(root, registryReader: _ => registry, uninstallLauncher: launcher);

    private static InstalledProgram Program(string key, RegistryOrigin origin = RegistryOrigin.Hklm64) => new()
    {
        DisplayName = "Same name",
        Publisher = "Same publisher",
        DisplayVersion = "1.0",
        Origin = origin,
        SourceKeyPath = "Registry/" + key,
        UninstallString = "unused.exe",
    };

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeLauncher : IUninstallLauncher
    {
        public List<UninstallCommand> Commands { get; } = [];
        public Task<UninstallLaunchResult> LaunchAsync(UninstallCommand command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(UninstallLaunchResult.Success(command.CommandLine));
        }
    }

    private sealed class FakeInventory : IInstalledProgramService
    {
        public Func<CancellationToken, Task<IReadOnlyList<InstalledProgram>>> Scan { get; set; } =
            _ => Task.FromResult<IReadOnlyList<InstalledProgram>>([]);
        public Func<InstalledProgram, CancellationToken, Task<UninstallLaunchResult>> Launch { get; set; } =
            (_, _) => Task.FromResult(UninstallLaunchResult.Success("fake"));
        public List<InstalledProgram> Launched { get; } = [];
        public Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default) => Scan(ct);
        public Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default)
        {
            Launched.Add(program);
            return Launch(program, ct);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Root { get; } = CreateRoot();

        // 与共享夹具一致：系统临时目录不可写时回退到工作区，
        // 否则受限环境会让整组库存测试因为目录创建失败而误报。
        private static string CreateRoot()
        {
            foreach (var candidate in new[]
                     {
                         Path.GetTempPath(),
                         Path.Combine(System.IO.Directory.GetCurrentDirectory(), "test-temp"),
                     })
            {
                try
                {
                    var path = Path.Combine(candidate, "GitHubStore-inventory-" + Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(path);
                    return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 尝试下一个可写根目录。
                }
            }
            throw new InvalidOperationException("No writable temporary directory is available for this test run.");
        }
        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        public string File(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, string.Empty);
            return path;
        }
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
