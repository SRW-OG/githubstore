using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 已安装程序契约。EffectiveUninstallString 决定点“卸载”时到底启动什么，
/// 选错会静默卸载或启动错误程序，因此逐种组合固定下来。
/// </summary>
[Trait("Category", "Unit")]
public sealed class InventoryContractTests
{
    [Fact]
    public void EffectiveUninstallString_PrefersTheNormalUninstallCommand()
    {
        var program = Program("\"C:\\App\\u.exe\"", "\"C:\\App\\u.exe\" /S");

        // 普通命令原样保留；绝不回退到静默命令。
        Assert.Equal("\"C:\\App\\u.exe\"", program.EffectiveUninstallString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EffectiveUninstallString_NeverFallsBackToQuietCommand(string? uninstall)
    {
        var program = Program(uninstall, "msiexec /x {GUID} /qn");

        Assert.Null(program.EffectiveUninstallString);
        Assert.False(program.CanUninstall);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("   ", "   ", false)]
    [InlineData("   ", null, false)]
    [InlineData(null, "   ", false)]
    [InlineData("\"C:\\App\\u.exe\"", null, true)]
    [InlineData(null, "msiexec /x {GUID} /qn", false)]
    [InlineData("\"C:\\App\\u.exe\"", "\"C:\\App\\u.exe\" /S", true)]
    public void CanUninstall_RequiresTheNormalCommand(string? uninstall, string? quiet, bool expected)
    {
        Assert.Equal(expected, Program(uninstall, quiet).CanUninstall);
    }

    [Fact]
    public void DuplicateCount_DefaultsToOne()
    {
        Assert.Equal(1, Program("a.exe", null).DuplicateCount);
    }

    [Fact]
    public void DuplicateCount_CanBeIncreasedByDeduplication()
    {
        var program = Program("a.exe", null) with { DuplicateCount = 3 };

        Assert.Equal(3, program.DuplicateCount);
    }

    [Fact]
    public void Success_CarriesCommandLineAndNoError()
    {
        var result = UninstallLaunchResult.Success("\"C:\\App\\u.exe\" /S");

        Assert.True(result.Started);
        Assert.Equal("\"C:\\App\\u.exe\" /S", result.CommandLine);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData(UninstallErrorCodes.NoUninstallString)]
    [InlineData(UninstallErrorCodes.InvalidUninstallCommand)]
    [InlineData(UninstallErrorCodes.ExecutableNotFound)]
    [InlineData(UninstallErrorCodes.LaunchFailed)]
    [InlineData(UninstallErrorCodes.AccessDenied)]
    [InlineData(UninstallErrorCodes.ScanFailed)]
    public void Failure_CarriesErrorCodeAndIsNotStarted(string errorCode)
    {
        var result = UninstallLaunchResult.Failure(errorCode);

        Assert.False(result.Started);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Null(result.CommandLine);
    }

    [Fact]
    public void ErrorCodes_UseStableSnakeCaseValues()
    {
        // 这些字符串会进入本地化映射表，改动词条会静默退化为原始码。
        Assert.Equal("no_uninstall_string", UninstallErrorCodes.NoUninstallString);
        Assert.Equal("invalid_uninstall_command", UninstallErrorCodes.InvalidUninstallCommand);
        Assert.Equal("executable_not_found", UninstallErrorCodes.ExecutableNotFound);
        Assert.Equal("launch_failed", UninstallErrorCodes.LaunchFailed);
        Assert.Equal("access_denied", UninstallErrorCodes.AccessDenied);
        Assert.Equal("scan_failed", UninstallErrorCodes.ScanFailed);
    }

    [Theory]
    [InlineData(RegistryOrigin.Hklm64)]
    [InlineData(RegistryOrigin.Hklm32)]
    [InlineData(RegistryOrigin.Hkcu)]
    public void RegistryOrigin_RoundTrips(RegistryOrigin origin)
    {
        Assert.Equal(origin, Program("a.exe", null, origin).Origin);
    }

    [Fact]
    public void SourceKeyPath_IsPreservedForDiagnostics()
    {
        const string keyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fixture";

        Assert.Equal(keyPath, Program("a.exe", null).SourceKeyPath);
    }

    [Fact]
    public void Id_IsStableAcrossMetadataChangesButDistinctAcrossSourceKeysAndViews()
    {
        var original = Program("a.exe", null);
        Assert.Equal(original.Id, (original with { DisplayName = "Renamed", DisplayVersion = "2.0" }).Id);
        Assert.Equal(original.Id, (original with { SourceKeyPath = original.SourceKeyPath.ToLowerInvariant() }).Id);
        Assert.NotEqual(original.Id, (original with { Origin = RegistryOrigin.Hklm32 }).Id);
        Assert.NotEqual(original.Id, (original with { SourceKeyPath = original.SourceKeyPath + "2" }).Id);
    }

    private static InstalledProgram Program(string? uninstall, string? quiet,
        RegistryOrigin origin = RegistryOrigin.Hklm64) => new()
    {
        DisplayName = "Fixture App",
        Publisher = "Fixture Publisher",
        DisplayVersion = "1.0.0",
        InstallLocation = @"C:\Apps\Fixture",
        UninstallString = uninstall,
        QuietUninstallString = quiet,
        ProductCode = "{12345678-1234-1234-1234-123456789012}",
        Origin = origin,
        SourceKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fixture",
    };
}
