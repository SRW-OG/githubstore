using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class InstalledAiViewModelTests
{
    [Fact]
    public async Task Question_UsesInstalledQuestionTaskAndPreservesFiltering()
    {
        var ai = new FakeAi { Response = "Alpha edits images." };
        using var vm = await CreateAsync(ai);
        vm.Filter = "Alpha";
        vm.AiQuestion = "Which app edits images?";

        await vm.AskInstalledAiCommand.ExecuteAsync(null);

        Assert.Equal(AiAssistantTask.InstalledQuestion, ai.LastRequest!.Task);
        Assert.Equal("Which app edits images?", ai.LastRequest.Question);
        Assert.Equal("Alpha edits images.", vm.AiResponse);
        Assert.Single(vm.Programs);
        Assert.Equal("Alpha", vm.Programs[0].DisplayName);
        Assert.Equal("en-US", ai.Language);
    }

    [Fact]
    public async Task SelectedImpact_UsesReceiptIdentityWithoutLaunchingUninstall()
    {
        var ai = new FakeAi();
        var inventory = new FakeInventory();
        using var vm = await CreateAsync(ai, inventory);
        vm.SelectedProgram = vm.Programs[1];

        await vm.ExplainUninstallAiCommand.ExecuteAsync(null);

        Assert.Equal(AiAssistantTask.UninstallExplanation, ai.LastRequest!.Task);
        Assert.Equal("receipt-beta", ai.LastRequest.InstallId);
        Assert.Contains("\"VerifiedUninstallMetadata\":false", ai.LastRequest.Context);
        Assert.False(vm.CanUninstall);
        Assert.Equal(0, inventory.UninstallCalls);
    }

    [Fact]
    public async Task SelectionChange_CancelsAndRejectsLateImpactResult()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAi { Handler = (_, _) => pending.Task };
        using var vm = await CreateAsync(ai);
        vm.SelectedProgram = vm.Programs[0];
        var task = vm.ExplainUninstallAiCommand.ExecuteAsync(null);
        Assert.True(vm.IsAiBusy);

        vm.SelectedProgram = vm.Programs[1];
        pending.SetResult("Outdated Alpha analysis");
        await task;

        Assert.True(ai.Token.IsCancellationRequested);
        Assert.Empty(vm.AiResponse);
        Assert.False(vm.IsAiBusy);
    }

    [Fact]
    public async Task ChangedQuestion_RejectsLateAnswer()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAi { Handler = (_, _) => pending.Task };
        using var vm = await CreateAsync(ai);
        vm.AiQuestion = "First question";
        var task = vm.AskInstalledAiCommand.ExecuteAsync(null);

        vm.AiQuestion = "Second question";
        pending.SetResult("Outdated answer");
        await task;

        Assert.Empty(vm.AiResponse);
        Assert.True(ai.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task UnloadCancellation_RejectsLateComparison()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAi { Handler = (_, _) => pending.Task };
        using var vm = await CreateAsync(ai);
        var task = vm.CompareInstalledAiCommand.ExecuteAsync(null);

        vm.CancelAiAnalysis(clearResult: true);
        pending.SetResult("Late result");
        await task;

        Assert.Empty(vm.AiResponse);
        Assert.True(ai.Token.IsCancellationRequested);
        Assert.False(vm.IsAiBusy);
    }

    [Fact]
    public async Task ServiceFailure_IsSafeAndLabelsFollowLanguage()
    {
        var ai = new FakeAi { Handler = (_, _) => throw new InvalidOperationException("secret-provider-body") };
        using var vm = await CreateAsync(ai);
        vm.AiQuestion = "Compare my apps";
        await vm.AskInstalledAiCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.AiErrorMessage);
        Assert.DoesNotContain("secret-provider-body", vm.AiErrorMessage);
        Assert.Equal("Ask", vm.AiAskLabel);
        vm.Text.ApplyLanguage("zh-CN");
        Assert.Equal("提问", vm.AiAskLabel);
        Assert.Empty(vm.AiErrorMessage);
    }

    private static async Task<InstalledViewModel> CreateAsync(FakeAi ai, FakeInventory? inventory = null)
    {
        var vm = new InstalledViewModel(inventory ?? new FakeInventory(), new LocalizationService("en-US"), ai);
        await vm.InitializeAsync();
        return vm;
    }

    private sealed class FakeAi : IAiAssistantService
    {
        public bool IsConfigured => true;
        public string Response { get; init; } = "Receipt information only; uninstall metadata is unavailable.";
        public Func<AiAssistantRequest, CancellationToken, Task<string>>? Handler { get; init; }
        public AiAssistantRequest? LastRequest { get; private set; }
        public string? Language { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<string> AnalyzeAsync(AiAssistantRequest request, string language, CancellationToken ct = default)
        {
            LastRequest = request;
            Language = language;
            Token = ct;
            return Handler?.Invoke(request, ct) ?? Task.FromResult(Response);
        }
        public Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> SearchAsync(string query, string language, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitHubStore.Core.Abstractions.App>>([]);
    }

    private sealed class FakeInventory : IInstalledProgramService
    {
        public int UninstallCalls { get; private set; }
        public Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstalledProgram>>(
        [
            new() { DisplayName = "Alpha", SourceKeyPath = "receipt-alpha", Origin = RegistryOrigin.StoreInstall },
            new() { DisplayName = "Beta", SourceKeyPath = "receipt-beta", Origin = RegistryOrigin.StoreInstall },
        ]);
        public Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default)
        {
            UninstallCalls++;
            return Task.FromResult(UninstallLaunchResult.Failure(UninstallErrorCodes.NoUninstallString));
        }
    }
}
