using GitHubStore.App.Services.Installation;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

[Collection("AppInstallService")]
public sealed class ArchiveProcessSafetyTests
{
    [Theory]
    [InlineData("Tool.7z", false, false)]
    [InlineData("Tool.7Z", false, true)]
    [InlineData("Tool.7z", true, false)]
    [InlineData("Tool.7Z", true, true)]
    public async Task SevenZipFailsClosedBeforeCacheDownloadOrProcessStart(
        string name, bool explicitArchivePolicy, bool providerThrows)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GitHubStore-archive-safety-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "apps");
        var data = Path.Combine(directory, "data");
        var download = new ForbiddenDownload();
        var launcher = new RecordingLauncher();
        var extractorLookups = 0;
        var processStarts = 0;
        var service = new AppInstallService(download, launcher,
            new PreferencesService(Path.Combine(data, "preferences.json")), data,
            processRunner: (_, _) =>
            {
                processStarts++;
                throw new InvalidOperationException("No external process may start for a 7z asset.");
            },
            installRootProvider: () => root,
            bsdtarProvider: () =>
            {
                extractorLookups++;
                return providerThrows
                    ? throw new InvalidOperationException("Extractor lookup must not be needed.")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            });
        var app = new GitHubStore.Core.Abstractions.App
        {
            Id = new(new(Guid.NewGuid()), new("owner/tool")), SourceRevision = 1,
            DisplayName = "Tool", Owner = "owner", Repo = "tool",
        };
        var asset = new Asset
        {
            InstallerId = "archive", ReleaseKey = "release", Name = name,
            DownloadUrl = "https://example.com/" + name,
        };
        var request = new AppInstallRequest
        {
            App = app, Asset = asset,
            Release = new() { AppId = app.Id, Key = "release", TagName = "v1", Assets = new[] { asset } },
            PackagePolicy = explicitArchivePolicy
                ? new PackagePolicy { InstallFormat = PackageInstallFormat.PortableArchive }
                : null,
        };
        var phases = new List<AppInstallPhase>();

        var result = await service.InstallAsync(request, new Sink(phases));

        Assert.False(result.Succeeded);
        Assert.Equal("install_7z_unsupported", result.ErrorCode);
        Assert.False(result.FilesMayRemain);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(AppInstallPhase.Preparing, result.Diagnostic.Phase);
        Assert.False(result.Diagnostic.ExternalInstallerStarted);
        Assert.False(result.Diagnostic.ExternalInstallerFinished);
        Assert.Equal(0, extractorLookups);
        Assert.Equal(0, processStarts);
        Assert.Equal(0, download.Calls);
        Assert.Equal(0, launcher.Registrations);
        Assert.Equal(new[] { AppInstallPhase.Preparing }, phases);
        Assert.False(Directory.Exists(directory));
    }

    private sealed class Sink(List<AppInstallPhase> phases) : IProgress<AppInstallProgress>
    {
        public void Report(AppInstallProgress value) => phases.Add(value.Phase);
    }

    private sealed class ForbiddenDownload : IDownloadService
    {
        public int Calls { get; private set; }
        public IReadOnlyList<DownloadEngine> AvailableEngines => new[] { DownloadEngine.Http };
        public Task<bool> ProbeAria2Async(CancellationToken ct = default) => Task.FromResult(false);
        public Task<DownloadResult> DownloadAsync(DownloadRequest request,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Unsupported archives must not be downloaded.");
        }
    }

    private sealed class RecordingLauncher : IManagedAppLauncher
    {
        public event EventHandler? Changed { add { } remove { } }
        public int Registrations { get; private set; }
        public Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LauncherApp>>(Array.Empty<LauncherApp>());
        public Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default)
        {
            Registrations++;
            throw new InvalidOperationException("Unsupported archives must not be registered.");
        }
        public Task UnregisterAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
