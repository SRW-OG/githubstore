using System.Diagnostics;
using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LauncherLaunchOptionsTests
{
    [Fact]
    public async Task OptionsRoundTripWithLaunchCountPreserved()
    {
        using var fixture = new Fixture();
        var store = new LauncherCustomizationStore(fixture.Root);
        await store.RecordLaunchAsync(false, "install-1");
        var options = new LauncherEntryOptions
        {
            IconPath = Path.Combine(fixture.Root, "icon.ico"),
            Arguments = "--silent \"two words\"",
            WorkingDirectory = fixture.Root,
            RunAsAdministrator = true,
            Color = "#FF3B82F6",
            Hotkey = "Ctrl+Alt+K",
            Notes = "line one\nline two",
        };
        var saved = await store.SavePropertiesAsync(false, "install-1", "Renamed", new CategoryId("tools"), options);
        Assert.Equal(1, saved.LaunchCount);
        Assert.Equal(options, saved.Options);
        var reloaded = Assert.Single(await new LauncherCustomizationStore(fixture.Root).ListAsync());
        Assert.Equal(options, reloaded.Options);
        Assert.Equal("Renamed", reloaded.DisplayName);
    }

    [Theory]
    [InlineData("#12345", "launcher_customization_invalid_color")]
    [InlineData("#GGGGGGG", "launcher_customization_invalid_color")]
    public async Task InvalidColorIsRejectedWithoutWriting(string color, string code)
    {
        using var fixture = new Fixture();
        var store = new LauncherCustomizationStore(fixture.Root);
        var error = await Assert.ThrowsAsync<LauncherException>(() => store.SavePropertiesAsync(false, "entry",
            "Name", null, new LauncherEntryOptions { Color = color }));
        Assert.Equal(code, error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "launcher-customizations.json")));
    }

    [Fact]
    public async Task OverlongArgumentsAreRejected()
    {
        using var fixture = new Fixture();
        var store = new LauncherCustomizationStore(fixture.Root);
        var error = await Assert.ThrowsAsync<LauncherException>(() => store.SavePropertiesAsync(false, "entry",
            "Name", null, new LauncherEntryOptions { Arguments = new string('a', 9000) }));
        Assert.Equal("launcher_customization_invalid_options", error.Code);
    }

    [Fact]
    public async Task ManagedLaunchUsesReceiptExecutableWithUserOptions()
    {
        using var fixture = new Fixture();
        var manifest = await fixture.WriteManagedInstallAsync();
        ProcessStartInfo? captured = null;
        var launcher = new ManagedAppLauncher(fixture.Root, fixture.InstallRoot,
            optionProcessStarter: info => { captured = info; return true; });
        var options = new LauncherEntryOptions { Arguments = "--flag", WorkingDirectory = fixture.Root, RunAsAdministrator = true };
        var result = await launcher.LaunchWithOptionsAsync(manifest.InstallId, options);
        Assert.True(result.Started);
        Assert.NotNull(captured);
        Assert.Equal(manifest.ExePath, captured!.FileName);
        Assert.Equal("--flag", captured.Arguments);
        Assert.Equal(fixture.Root, captured.WorkingDirectory);
        Assert.Equal("runas", captured.Verb);
        Assert.True(captured.UseShellExecute);
    }

    [Fact]
    public async Task ManagedOptionsCannotRedirectTheExecutable()
    {
        using var fixture = new Fixture();
        var manifest = await fixture.WriteManagedInstallAsync();
        ProcessStartInfo? captured = null;
        var launcher = new ManagedAppLauncher(fixture.Root, fixture.InstallRoot,
            optionProcessStarter: info => { captured = info; return true; });
        // The receipt remains the only executable source even when options carry extra paths.
        var result = await launcher.LaunchWithOptionsAsync(manifest.InstallId,
            new LauncherEntryOptions { Arguments = "\"C:\\evil.exe\"", WorkingDirectory = fixture.Root });
        Assert.True(result.Started);
        Assert.Equal(manifest.ExePath, captured!.FileName);
    }

    [Fact]
    public async Task EmptyOptionsFallBackToTheLegacyLaunchPath()
    {
        using var fixture = new Fixture();
        var manifest = await fixture.WriteManagedInstallAsync();
        var legacy = 0;
        var launcher = new ManagedAppLauncher(fixture.Root, fixture.InstallRoot,
            processStarter: (_, _) => { legacy++; return true; });
        var result = await launcher.LaunchWithOptionsAsync(manifest.InstallId, new LauncherEntryOptions());
        Assert.True(result.Started);
        Assert.Equal(1, legacy);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "launcher-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string InstallRoot { get; private set; } = string.Empty;

        public async Task<ManagedInstall> WriteManagedInstallAsync()
        {
            InstallRoot = Path.Combine(Root, "apps");
            var installRoot = InstallRoot;
            var target = Path.Combine(installRoot, "Demo");
            Directory.CreateDirectory(target);
            var exe = Path.Combine(target, "Demo.exe");
            await File.WriteAllBytesAsync(exe, MinimalPe());
            var sourceId = new SourceInstanceId(Guid.NewGuid());
            var packageId = new PackageId("demo/app");
            var install = new InstallRecord
            {
                InstallId = "install-demo",
                AppId = new AppId(sourceId, packageId),
                Source = new SourceBinding
                {
                    SourceId = sourceId,
                    SourceEntryUrl = new Uri("https://github.com/demo/app"),
                    SourceRevision = 1,
                    PackageId = packageId,
                    InstallerId = "1",
                    ArtifactUrl = new Uri("https://example.invalid/app.zip"),
                    ArtifactSha256 = new string('a', 64),
                },
                ReleaseKey = "v1",
                Origin = Origin.Store,
                Channel = Channel.Http,
                Strategy = Strategy.Green,
                Installer = InstallerKind.Portable,
                Version = new GitHubStore.Core.Abstractions.Version { Raw = "1.0.0", Scheme = VersionScheme.Opaque },
                TargetDir = target,
                ExePath = exe,
                InstalledAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = InstallState.Done,
            };
            var managed = new ManagedAppLauncher(Root, installRoot, processStarter: (_, _) => true);
            await managed.RegisterCompletedInstallAsync(install, "Demo");
            return new ManagedInstall(install.InstallId, exe);
        }

        private static byte[] MinimalPe()
        {
            var bytes = new byte[512];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BitConverter.GetBytes(128).CopyTo(bytes, 60);
            bytes[128] = (byte)'P';
            bytes[129] = (byte)'E';
            // Characteristics: executable image, not a DLL.
            BitConverter.GetBytes((ushort)0x0002).CopyTo(bytes, 128 + 22);
            return bytes;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ManagedInstall(string InstallId, string ExePath);
}
