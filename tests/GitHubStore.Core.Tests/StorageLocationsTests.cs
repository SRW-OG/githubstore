using GitHubStore.App.Services.Preferences;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 生效目录的解析规则。目录选错会把安装包缓存或绿色安装写到用户没预期的位置，
/// 且覆盖默认值会让“恢复默认”失效，因此默认值与回退边界都要固定。
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageLocationsTests
{
    [Fact]
    public void DefaultInstallRoot_IsTheAgreedPath()
    {
        // 用户明确要求默认安装目录为 C:\githubapps。
        Assert.Equal(@"C:\githubapps", StorageLocations.DefaultInstallRoot);
    }

    [Fact]
    public void DefaultCacheDirectory_IsUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, StorageLocations.DefaultCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHubStore", StorageLocations.DefaultCacheDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveInstallRoot_FallsBackToDefaultWhenUnset(string? configured)
    {
        var preferences = new AppPreferences { InstallRootDirectory = configured };

        Assert.Equal(StorageLocations.DefaultInstallRoot, StorageLocations.ResolveInstallRoot(preferences));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveCacheDirectory_FallsBackToDefaultWhenUnset(string? configured)
    {
        var preferences = new AppPreferences { DownloadCacheDirectory = configured };

        Assert.Equal(StorageLocations.DefaultCacheDirectory, StorageLocations.ResolveCacheDirectory(preferences));
    }

    [Fact]
    public void ResolveInstallRoot_ReturnsConfiguredValueVerbatim()
    {
        var preferences = new AppPreferences { InstallRootDirectory = @"D:\CustomApps" };

        Assert.Equal(@"D:\CustomApps", StorageLocations.ResolveInstallRoot(preferences));
    }

    [Fact]
    public void ResolveCacheDirectory_ReturnsConfiguredValueVerbatim()
    {
        var preferences = new AppPreferences { DownloadCacheDirectory = @"D:\Cache\Store" };

        Assert.Equal(@"D:\Cache\Store", StorageLocations.ResolveCacheDirectory(preferences));
    }

    [Fact]
    public void ResolveInstallRoot_DoesNotTreatNonBlankAsMissing()
    {
        // 只有空/空白算“未设置”；存在的路径即便暂时不可用也必须原样返回，
        // 否则用户在设置页填的路径会被静默换掉。
        var configured = Path.Combine(Path.GetTempPath(), "ghs-not-created-" + Guid.NewGuid().ToString("N"));
        var preferences = new AppPreferences { InstallRootDirectory = configured };

        Assert.Equal(configured, StorageLocations.ResolveInstallRoot(preferences));
    }

    [Fact]
    public void TryEnsureDirectory_OnValidPath_CreatesItAndReportsSuccess()
    {
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.DirectoryPath, "nested", "cache");

        var ok = StorageLocations.TryEnsureDirectory(target, out var error);

        Assert.True(ok, $"创建目录失败：{error}");
        Assert.Null(error);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void TryEnsureDirectory_OnExistingDirectory_IsIdempotent()
    {
        using var directory = new TempDirectory();

        Assert.True(StorageLocations.TryEnsureDirectory(directory.DirectoryPath, out _));
        Assert.True(StorageLocations.TryEnsureDirectory(directory.DirectoryPath, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryEnsureDirectory_OnEmptyPath_ReportsFailureWithoutThrowing()
    {
        // 空路径必然无效。刻意不测纯空白字符串：它在 Windows 上可能被归一化后
        // 解析成当前目录而“创建成功”，断言不可靠。
        var ok = StorageLocations.TryEnsureDirectory(string.Empty, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryEnsureDirectory_OnInvalidPath_ReportsFailureInsteadOfThrowing()
    {
        // 含非法字符的路径必须转成错误信息，不能把异常抛到 UI 线程。
        // 注意：非法路径要直接构造，Path.Combine 自身就会先抛异常，测不到被测方法。
        var invalid = Path.GetTempPath() + "bad\u0000name\\cache";

        var ok = StorageLocations.TryEnsureDirectory(invalid, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
