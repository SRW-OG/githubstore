using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GitHubStore.App.Services.Launcher;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LauncherProgramResolverTests
{
    [Fact]
    public async Task ActualWindowsShortcutResolvesUnicodePathAndQuotedArgumentsWithoutExecutingIt()
    {
        using var fixture = new Fixture();
        var exe = fixture.File("中文 程序.exe");
        var shortcut = Path.Combine(fixture.Root, "我的快捷方式.lnk");
        const string arguments = "--profile \"中文 配置\" --port=1234";
        await CreateShortcutAsync(shortcut, exe, arguments);
        var result = await new LauncherProgramResolver().ResolveAsync(shortcut);
        Assert.Equal(exe, result.Path);
        Assert.Equal(arguments, result.Arguments);
        Assert.Equal("fixture", await System.IO.File.ReadAllTextAsync(exe));
    }

    [Fact]
    public async Task NestedShortcutsResolveWithInnerThenOuterArguments()
    {
        using var fixture = new Fixture();
        var exe = fixture.File("tool.exe");
        var outer = fixture.File("outer.lnk");
        var inner = fixture.File("inner.lnk");
        var resolver = new LauncherProgramResolver(path => path == outer
            ? new(inner, "--outer") : new(exe, "--inner"));
        Assert.Equal(new LauncherProgramTarget(exe, "--inner --outer"), await resolver.ResolveAsync(outer));
    }

    [Fact]
    public async Task CyclesAndBrokenShortcutsAreRejected()
    {
        using var fixture = new Fixture();
        var shortcut = fixture.File("cycle.lnk");
        var cycle = new LauncherProgramResolver(path => new(path, string.Empty));
        Assert.Equal("manual_launcher_shortcut_invalid", (await Assert.ThrowsAsync<LauncherException>(
            () => cycle.ResolveAsync(shortcut))).Code);
        Assert.Equal("manual_launcher_shortcut_invalid", (await Assert.ThrowsAsync<LauncherException>(
            () => new LauncherProgramResolver().ResolveAsync(shortcut))).Code);
    }

    [Theory]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData(@"C:\apps\..\tool.exe")]
    [InlineData(@"C:\apps\tool.cmd")]
    public async Task UnsafeOrUnsupportedShortcutTargetsAreRejected(string target)
    {
        using var fixture = new Fixture();
        var shortcut = fixture.File("tool.lnk");
        var resolver = new LauncherProgramResolver(_ => new(target, string.Empty));
        Assert.Equal("manual_launcher_invalid_path", (await Assert.ThrowsAsync<LauncherException>(
            () => resolver.ResolveAsync(shortcut))).Code);
    }

    [Fact]
    public async Task MissingShortcutTargetIsRejectedAndCancellationIsObserved()
    {
        using var fixture = new Fixture();
        var shortcut = fixture.File("missing.lnk");
        var resolver = new LauncherProgramResolver(_ => new(Path.Combine(fixture.Root, "gone.exe"), string.Empty));
        Assert.Equal("manual_launcher_entry_missing", (await Assert.ThrowsAsync<LauncherException>(
            () => resolver.ResolveAsync(shortcut))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(shortcut, cancellation.Token));
    }

    [Fact]
    public async Task AddingShortcutPersistsExecutableAndArgumentsAndLaunchesThemAfterReload()
    {
        using var fixture = new Fixture();
        var exe = fixture.File("actual.exe");
        var shortcut = fixture.File("shortcut.lnk");
        var resolver = new LauncherProgramResolver(_ => new(exe, "--from-shortcut"));
        using (var vm = fixture.ViewModel(resolver))
        {
            Assert.True(await vm.AddManualEntryAsync(shortcut, "Original name", new CategoryId("tools"), "--extra"));
            var item = Assert.Single(vm.Apps);
            Assert.Equal(exe, item.ExecutablePath);
            Assert.Equal("--from-shortcut --extra", item.Options.Arguments);
        }
        ProcessStartInfo? captured = null;
        var launcher = new ManualAppLauncher(fixture.Root, optionProcessStarter: info => { captured = info; return true; });
        using var reopened = fixture.ViewModel(resolver, launcher);
        await reopened.RefreshEntriesAsync();
        await reopened.LaunchEntryAsync(Assert.Single(reopened.Apps));
        Assert.Equal(exe, captured!.FileName);
        Assert.Equal("--from-shortcut --extra", captured.Arguments);
        Assert.Equal(1L, Assert.Single(reopened.Apps).LaunchCount);
    }

    [Fact]
    public async Task SimplifiedEditConvertsLegacyShortcutAndRetainsIdentityNameCategoryAndCount()
    {
        using var fixture = new Fixture();
        var shortcut = fixture.File("legacy.lnk");
        var exe = fixture.File("actual.exe");
        var original = await new ManualAppLauncher(fixture.Root).AddAsync(shortcut, "Keep this name", new CategoryId("tools"));
        await new LauncherCustomizationStore(fixture.Root).RecordLaunchAsync(true, original.Id);
        using var vm = fixture.ViewModel(new LauncherProgramResolver(_ => new(exe, "--link")));
        await vm.RefreshEntriesAsync();
        Assert.True(await vm.SaveLaunchPropertiesAsync(Assert.Single(vm.Apps), shortcut, "--edited"));
        var saved = Assert.Single(await new ManualAppLauncher(fixture.Root).ListAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.AddedAt, saved.AddedAt);
        Assert.Equal(original.DisplayName, saved.DisplayName);
        Assert.Equal(original.Category, saved.Category);
        Assert.Equal(exe, saved.Path);
        Assert.Equal(1L, Assert.Single(vm.Apps).LaunchCount);
        Assert.Equal("--link --edited", Assert.Single(vm.Apps).Options.Arguments);
    }

    [Fact]
    public async Task InvalidArgumentsCannotPartiallyChangeTheProgramPath()
    {
        using var fixture = new Fixture();
        var original = await new ManualAppLauncher(fixture.Root).AddAsync(fixture.File("old.exe"), "Keep");
        using var vm = fixture.ViewModel(new LauncherProgramResolver());
        await vm.RefreshEntriesAsync();
        Assert.False(await vm.SaveLaunchPropertiesAsync(Assert.Single(vm.Apps), fixture.File("new.exe"), new string('x', 8193)));
        Assert.Equal(original.Path, Assert.Single(await new ManualAppLauncher(fixture.Root).ListAsync()).Path);
    }

    [Fact]
    public async Task AddingShortcutToExistingExecutableDoesNotReplaceSavedArguments()
    {
        using var fixture = new Fixture();
        var exe = fixture.File("existing.exe");
        using var vm = fixture.ViewModel(new LauncherProgramResolver(_ => new(exe, "--from-link")));
        Assert.True(await vm.AddManualEntryAsync(exe, "Original", null, "--keep"));
        Assert.True(await vm.AddManualEntryAsync(fixture.File("duplicate.lnk"), "Other name", null, "--new"));
        var item = Assert.Single(vm.Apps);
        Assert.Equal("Original", item.DisplayName);
        Assert.Equal("--keep", item.Options.Arguments);
    }

    [Fact]
    public async Task ClearingArgumentsOverridesLegacyCustomizationAfterReload()
    {
        using var fixture = new Fixture();
        var exe = fixture.File("clear.exe");
        var entry = await new ManualAppLauncher(fixture.Root).AddAsync(exe, "Keep");
        var store = new LauncherCustomizationStore(fixture.Root);
        await store.SavePropertiesAsync(true, entry.Id, "Keep", null,
            new LauncherEntryOptions { Arguments = "--legacy", Notes = "keep this note" });
        using (var vm = fixture.ViewModel(new LauncherProgramResolver()))
        {
            await vm.RefreshEntriesAsync();
            Assert.Equal("--legacy", Assert.Single(vm.Apps).Options.Arguments);
            Assert.True(await vm.SaveLaunchPropertiesAsync(Assert.Single(vm.Apps), exe, string.Empty));
        }
        using var reopened = fixture.ViewModel(new LauncherProgramResolver());
        await reopened.RefreshEntriesAsync();
        Assert.Empty(Assert.Single(reopened.Apps).Options.Arguments);
        Assert.Equal("keep this note", Assert.Single(reopened.Apps).Options.Notes);
        Assert.Equal(string.Empty, Assert.Single(await new ManualAppLauncher(fixture.Root).ListAsync()).Arguments);
    }

    [Fact]
    public async Task FailedAtomicWriteLeavesBothPathAndArgumentsUnchanged()
    {
        using var fixture = new Fixture();
        var launcher = new ManualAppLauncher(fixture.Root);
        var original = await launcher.AddProgramAsync(fixture.File("before.exe"), "Keep", null, "--before");
        var path = fixture.File("after.exe");
        var manifest = Path.Combine(fixture.Root, "manual-launcher.json");
        var before = await System.IO.File.ReadAllBytesAsync(manifest);
        var attributes = System.IO.File.GetAttributes(manifest);
        System.IO.File.SetAttributes(manifest, attributes | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<LauncherException>(() => launcher.UpdateProgramAsync(original.Id, path, "--after"));
            Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(manifest));
            Assert.Equal(original, Assert.Single(await launcher.ListAsync()));
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        }
        finally { System.IO.File.SetAttributes(manifest, attributes); }
    }

    [Fact]
    public async Task ManualProgramEditsDoNotRequireASecondCustomizationWrite()
    {
        using var fixture = new Fixture();
        var exe = fixture.File("actual.exe");
        var shortcut = fixture.File("original.lnk");
        var store = new SaveFailingStore(new LauncherCustomizationStore(fixture.Root));
        using var vm = new LauncherViewModel(new ManagedAppLauncher(fixture.Root, Path.Combine(fixture.Root, "managed")),
            new LocalizationService("zh-CN"), manualLauncher: new ManualAppLauncher(fixture.Root), customizationStore: store,
            programResolver: new LauncherProgramResolver(_ => new(exe, "--shortcut")));
        Assert.True(await vm.AddManualEntryAsync(shortcut, "Keep", null, "--extra"));
        Assert.True(await vm.SaveLaunchPropertiesAsync(Assert.Single(vm.Apps), exe, "--edited"));
        Assert.Equal("--edited", Assert.Single(await new ManualAppLauncher(fixture.Root).ListAsync()).Arguments);
        Assert.Equal(0, store.SaveCalls);
    }

    private sealed class SaveFailingStore(ILauncherCustomizationStore inner) : ILauncherCustomizationStore
    {
        public int SaveCalls;
        public Task<IReadOnlyList<LauncherCustomization>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<LauncherCustomization> RecordLaunchAsync(bool isManual, string entryId, CancellationToken ct = default) =>
            inner.RecordLaunchAsync(isManual, entryId, ct);
        public Task<LauncherCustomization> UpdatePropertiesAsync(bool isManual, string entryId, string name,
            CategoryId? category, CancellationToken ct = default) => throw new InvalidOperationException("Must not save manual arguments here.");
        public Task<LauncherCustomization> SavePropertiesAsync(bool isManual, string entryId, string name,
            CategoryId? category, LauncherEntryOptions options, CancellationToken ct = default)
        {
            SaveCalls++;
            throw new LauncherException("launcher_customization_write_failed");
        }
    }

    private static Task CreateShortcutAsync(string shortcut, string exe, string arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                done.TrySetException(new PlatformNotSupportedException());
                return;
            }
            object? instance = null;
            try
            {
                // WScript.Shell.TargetPath can reject Unicode paths on English Windows/Server.
                // Create the real fixture with the Unicode Shell interface, preserving the full test input.
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!);
                var link = (ITestShellLinkW)instance!;
                link.SetPath(exe);
                link.SetArguments(arguments);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
                ((IPersistFile)instance!).Save(shortcut, true);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                if (instance is not null) Marshal.FinalReleaseComObject(instance);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITestShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "launcher-resolver-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string File(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, "fixture");
            return path;
        }
        public LauncherViewModel ViewModel(ILauncherProgramResolver resolver, ManualAppLauncher? launcher = null) =>
            new(new ManagedAppLauncher(Root, Path.Combine(Root, "managed")), new LocalizationService("zh-CN"),
                manualLauncher: launcher ?? new ManualAppLauncher(Root),
                customizationStore: new LauncherCustomizationStore(Root), programResolver: resolver);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
