using System.Text.Json;
using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.Core.Tests;

public sealed class InstallationAiViewModelTests
{
    [Fact]
    public async Task Diagnosis_PassesStructuredFactsWithoutLocalPaths()
    {
        var service = new FakeInstall { Result = new(false, "install_installer_failed",
            TargetDirectory: "C:/private-user/application", ExecutablePath: "C:/private-user/app.exe",
            FilesMayRemain: true, Diagnostic: Diagnostic()) };
        var ai = new FakeAi();
        using var vm = new InstallationViewModel(service, new LocalizationService("en-US"), ai);
        await vm.InstallAsync(Request());

        await vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal(AiAssistantTask.InstallDiagnosis, ai.LastRequest!.Task);
        using var json = JsonDocument.Parse(ai.LastRequest.Context);
        Assert.Equal("RunningInstaller", json.RootElement.GetProperty("Phase").GetString());
        Assert.Equal(1603, json.RootElement.GetProperty("InstallerExitCode").GetInt32());
        Assert.True(json.RootElement.GetProperty("ExternalInstallerStarted").GetBoolean());
        Assert.DoesNotContain("private-user", ai.LastRequest.Context);
        Assert.DoesNotContain("TargetDirectory", ai.LastRequest.Context);
        Assert.Equal("Diagnosis", vm.DiagnosisMarkdown);
    }

    [Fact]
    public async Task NewInstallation_CancelsDiagnosisAndRejectsLateResult()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAi { Handler = _ => pending.Task };
        var service = new FakeInstall { Result = new(false, "install_installer_failed", Diagnostic: Diagnostic()) };
        using var vm = new InstallationViewModel(service, new LocalizationService("en-US"), ai);
        await vm.InstallAsync(Request());
        var analysis = vm.DiagnoseCommand.ExecuteAsync(null);
        Assert.True(vm.IsDiagnosisBusy);

        service.Result = new(true);
        await vm.InstallAsync(Request());
        pending.SetResult("Old installation analysis");
        await analysis;

        Assert.True(ai.Token.IsCancellationRequested);
        Assert.Empty(vm.DiagnosisMarkdown);
        Assert.Null(vm.LastDiagnostic);
        Assert.False(vm.IsDiagnosisBusy);
    }

    [Fact]
    public async Task CancellationCallbackFailure_DoesNotBlockNextInstallation()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAi { Handler = token =>
        {
            token.Register(() => throw new InvalidOperationException("Cancellation callback failure"));
            return pending.Task;
        }};
        var service = new FakeInstall { Result = new(false, "install_installer_failed", Diagnostic: Diagnostic()) };
        using var vm = new InstallationViewModel(service, new LocalizationService("en-US"), ai);
        await vm.InstallAsync(Request());
        var analysis = vm.DiagnoseCommand.ExecuteAsync(null);

        service.Result = new(true);
        await vm.InstallAsync(Request());
        pending.SetResult("Late result");
        await analysis;
        await vm.InstallAsync(Request());

        Assert.Equal(3, service.InstallCalls);
        Assert.Empty(vm.DiagnosisMarkdown);
        Assert.False(vm.IsInstalling);
    }

    [Fact]
    public async Task UnknownErrorFallback_DoesNotSendRawPathToAi()
    {
        var service = new FakeInstall { Result = new(false, "Unexpected C:/private-user/token") };
        var ai = new FakeAi();
        using var vm = new InstallationViewModel(service, new LocalizationService("en-US"), ai);
        await vm.InstallAsync(Request());
        await vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.DoesNotContain("private-user", ai.LastRequest!.Context);
        Assert.Contains("InstallFailed", ai.LastRequest.Context);
        Assert.DoesNotContain("private-user", vm.ErrorMessage);
    }

    [Fact]
    public async Task AnalysisFailure_DoesNotLeakProviderBody()
    {
        var service = new FakeInstall { Result = new(false, "install_installer_failed", Diagnostic: Diagnostic()) };
        var ai = new FakeAi { Handler = _ => throw new InvalidOperationException("Secret body C:/private-user/token") };
        using var vm = new InstallationViewModel(service, new LocalizationService("en-US"), ai);
        await vm.InstallAsync(Request());
        await vm.DiagnoseCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.DiagnosisError);
        Assert.DoesNotContain("Secret", vm.DiagnosisError);
        Assert.DoesNotContain("private-user", vm.DiagnosisError);
        Assert.False(vm.IsDiagnosisBusy);
    }

    private static InstallDiagnostic Diagnostic() => new()
    {
        OperationId = "operation-1", AppName = "Fixture", Version = "v1", AssetName = "fixture.exe",
        Phase = AppInstallPhase.RunningInstaller, ErrorCode = "install_installer_failed",
        InstallerExitCode = 1603, ExternalInstallerStarted = true, ExternalInstallerFinished = true,
        FilesMayRemain = true, StartedAt = DateTimeOffset.UtcNow, FailedAt = DateTimeOffset.UtcNow,
    };

    private static AppInstallRequest Request()
    {
        var id = new AppId(new(Guid.NewGuid()), new("owner/fixture"));
        var app = new CatalogApp { Id = id, SourceRevision = 1, DisplayName = "Fixture", Owner = "owner", Repo = "fixture" };
        var asset = new Asset { InstallerId = "asset", ReleaseKey = "v1", Name = "fixture.exe", DownloadUrl = "https://example.invalid/fixture.exe" };
        return new() { App = app, Asset = asset, Release = new() { AppId = id, Key = "v1", TagName = "v1", Assets = [asset] } };
    }

    private sealed class FakeAi : IAiAssistantService
    {
        public bool IsConfigured => true;
        public Func<CancellationToken, Task<string>>? Handler { get; init; }
        public AiAssistantRequest? LastRequest { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<string> AnalyzeAsync(AiAssistantRequest request, string language, CancellationToken ct = default)
        {
            LastRequest = request;
            Token = ct;
            return Handler?.Invoke(ct) ?? Task.FromResult("Diagnosis");
        }
        public Task<IReadOnlyList<CatalogApp>> SearchAsync(string query, string language, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogApp>>([]);
    }

    private sealed class FakeInstall : IAppInstallService
    {
        public AppInstallResult Result { get; set; } = new(true);
        public int InstallCalls { get; private set; }
        public string GetTargetDirectory(CatalogApp app) => "C:/private-user/application";
        public Task<AppInstallResult> InstallAsync(AppInstallRequest request, IProgress<AppInstallProgress>? progress = null,
            Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null, CancellationToken ct = default)
        {
            InstallCalls++;
            return Task.FromResult(Result);
        }
    }
}
