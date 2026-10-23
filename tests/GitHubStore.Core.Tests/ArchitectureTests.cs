using GitHubStore.Core.Abstractions;
using GitHubStore.Platform.Win32.Runtime;
using NetArchTest.Rules;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Architecture")]
public sealed class ArchitectureTests
{
    [Fact]
    public void Core_DoesNotDependOnUiPlatformOrTests()
    {
        var result = Types.InAssembly(typeof(ICatalogService).Assembly)
            .ShouldNot().HaveDependencyOnAny("Microsoft.UI", "Windows.UI.Xaml", "GitHubStore.App",
                "GitHubStore.Platform.Win32", "GitHubStore.Core.Tests")
            .GetResult();
        Assert.True(result.IsSuccessful, $"Core has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Platform_DoesNotDependOnCoreAppOrTests()
    {
        var result = Types.InAssembly(typeof(WindowsRuntimeInfo).Assembly)
            .ShouldNot().HaveDependencyOnAny("GitHubStore.Core", "GitHubStore.App", "GitHubStore.Core.Tests")
            .GetResult();
        Assert.True(result.IsSuccessful, $"Platform has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
