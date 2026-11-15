using System.Buffers.Binary;
using System.Diagnostics;
using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>命令行工具第一版：PE 识别、PowerShell 环境解析、参数编码与启动路径选择。</summary>
public sealed class ConsoleLauncherTests
{
    private static byte[] MinimalPe(ushort subsystem, ushort optionalHeaderSize = 240)
    {
        // 足够长的 PE 头，包含 COFF 头与可选头的 Subsystem 字段。
        var bytes = new byte[1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x80 + 1] = (byte)'E';
        bytes[0x80 + 2] = 0;
        bytes[0x80 + 3] = 0;
        // COFF 头可选头大小位于 PE 头偏移 20（相对 0x80）。
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x80 + 20), optionalHeaderSize);
        // Subsystem 位于可选头偏移 68（相对 PE 头 0x80+24）。
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x80 + 24 + 68), subsystem);
        return bytes;
    }

    [Theory]
    [InlineData(3, true)]   // IMAGE_SUBSYSTEM_WINDOWS_CUI
    [InlineData(2, false)]  // IMAGE_SUBSYSTEM_WINDOWS_GUI
    public void ConsoleDetectionReadsPeSubsystem(ushort subsystem, bool expected)
    {
        using var stream = new MemoryStream(MinimalPe(subsystem));
        Assert.Equal(expected, ConsoleProgramDetector.IsConsoleProgram(stream));
    }

    [Fact]
    public void NonPeOrTruncatedDataIsNotConsole()
    {
        using var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        Assert.False(ConsoleProgramDetector.IsConsoleProgram(garbage));
        using var zeros = new MemoryStream(new byte[512]);
        Assert.False(ConsoleProgramDetector.IsConsoleProgram(zeros));
    }

    [Theory]
    [InlineData(LaunchMode.PowerShell, true)]   // 强制 PowerShell 即使路径不是控制台程序
    [InlineData(LaunchMode.Direct, false)]      // 强制直接启动
    public void LaunchModeOverrideWins(LaunchMode mode, bool expected)
    {
        // 用不存在的路径验证：显式模式不依赖文件探测。
        Assert.Equal(expected, ConsoleProgramDetector.ShouldLaunchInConsole(mode, "C:\\nope\\tool.exe"));
    }

    [Fact]
    public void AutoModeUsesConsoleDetection()
    {
        var temp = Path.Combine(Path.GetTempPath(), "console-detect-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(temp, MinimalPe(3));
            Assert.True(ConsoleProgramDetector.ShouldLaunchInConsole(LaunchMode.Auto, temp));
            File.WriteAllBytes(temp, MinimalPe(2));
            Assert.False(ConsoleProgramDetector.ShouldLaunchInConsole(LaunchMode.Auto, temp));
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    [Fact]
    public void UnconfiguredPwshFallsBackToSystemWindowsPowerShell()
    {
        var resolver = new PowerShellEnvironmentResolver(() => null);
        var resolution = resolver.Resolve();
        Assert.False(resolution.IsPowerShell7);
        Assert.Null(resolution.FallbackReason);
        Assert.Equal(PowerShellEnvironmentResolver.SystemPowerShellPath, resolution.ExecutablePath);
    }

    [Fact]
    public void ConfiguredButMissingPwshReportsFallbackReason()
    {
        var resolver = new PowerShellEnvironmentResolver(() => @"C:\missing\pwsh.exe");
        var resolution = resolver.Resolve();
        Assert.False(resolution.IsPowerShell7);
        Assert.Equal("configured_not_found", resolution.FallbackReason);
        Assert.Equal(PowerShellEnvironmentResolver.SystemPowerShellPath, resolution.ExecutablePath);
    }

    [Fact]
    public void ConfiguredExistingPwshIsUsed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pwsh-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "pwsh.exe");
        try
        {
            File.WriteAllBytes(temp, Array.Empty<byte>());
            var resolver = new PowerShellEnvironmentResolver(() => temp);
            var resolution = resolver.Resolve();
            Assert.True(resolution.IsPowerShell7);
            Assert.Null(resolution.FallbackReason);
            Assert.Equal(temp, resolution.ExecutablePath);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void QuoteForPowerShellEscapesSingleQuotes()
    {
        Assert.Equal("'it''s ok'", PowerShellEnvironmentResolver.QuoteForPowerShell("it's ok"));
        Assert.Equal("'C:\\Program Files\\x.exe'", PowerShellEnvironmentResolver.QuoteForPowerShell("C:\\Program Files\\x.exe"));
    }

    [Fact]
    public void EncodedCommandIsBase64Utf16()
    {
        var encoded = PowerShellEnvironmentResolver.EncodeCommand("Write-Host hi");
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal("Write-Host hi", decoded);
    }

    [Fact]
    public void CommandTextKeepsToolPathAsData()
    {
        // 含分号与 & 的参数不应把调用拆成额外命令；路径用单引号字面量。
        var invocation = ConsoleLauncher.BuildInvocation(@"C:\tools\cli.exe", "; & Remove-Item x");
        Assert.StartsWith("& 'C:\\tools\\cli.exe'", invocation);
        var keepOpen = PowerShellEnvironmentResolver.BuildCommandText(invocation, keepOpen: true);
        Assert.Contains("try {", keepOpen);
        Assert.Contains("exit to close", keepOpen);
        var closed = PowerShellEnvironmentResolver.BuildCommandText(invocation, keepOpen: false);
        Assert.DoesNotContain("exit to close", closed);
    }

    [Fact]
    public void ConsoleLauncherUsesResolvedPowerShellAndEncodedCommand()
    {
        var pwsh = Path.Combine(Path.GetTempPath(), "pwsh-" + Guid.NewGuid().ToString("N") + ".exe");
        var tool = Path.Combine(Path.GetTempPath(), "tool-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(pwsh, Array.Empty<byte>());
            File.WriteAllBytes(tool, MinimalPe(3));
            ProcessStartInfo? captured = null;
            // terminalPath = null：强制走直接 PowerShell 兜底，结果与机器是否安装 Windows Terminal 无关。
            var launcher = new ConsoleLauncher(new PowerShellEnvironmentResolver(() => pwsh),
                info => { captured = info; return true; }, terminalPath: () => null);
            var result = launcher.Launch(tool, Path.GetTempPath(), "--flag value", keepOpen: true);
            Assert.True(result.Started);
            Assert.NotNull(captured);
            Assert.Equal(pwsh, captured!.FileName);
            Assert.Contains("-EncodedCommand", captured.Arguments);
            Assert.True(captured.UseShellExecute);
            // 解码 -EncodedCommand 片段，确认调用的是工具绝对路径并与参数一起作为数据执行。
            var token = captured.Arguments.Split(' ').Last();
            var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(token));
            Assert.Contains(tool, script);
            Assert.Contains("--flag value", script);
        }
        finally
        {
            try { File.Delete(pwsh); } catch (IOException) { }
            try { File.Delete(tool); } catch (IOException) { }
        }
    }

    [Fact]
    public void ConsoleLauncherPrefersWindowsTerminalWhenAvailable()
    {
        var tool = Path.Combine(Path.GetTempPath(), "tool-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(tool, MinimalPe(3));
            ProcessStartInfo? terminal = null;
            ProcessStartInfo? direct = null;
            var launcher = new ConsoleLauncher(new PowerShellEnvironmentResolver(() => null),
                info => { direct = info; return true; },
                terminalPath: () => @"C:\fake\wt.exe",
                terminalStart: info => { terminal = info; return true; });
            var result = launcher.Launch(tool, Path.GetTempPath(), "--flag", keepOpen: true);
            Assert.True(result.Started);
            Assert.NotNull(terminal);        // 用 Windows Terminal 启动
            Assert.Null(direct);             // 直接 PowerShell 兜底未被调用
            Assert.Equal(@"C:\fake\wt.exe", terminal!.FileName);
            Assert.StartsWith("-- ", terminal.Arguments);   // wt -- pwsh ... 形式
            Assert.Contains("-EncodedCommand", terminal.Arguments);
        }
        finally { try { File.Delete(tool); } catch (IOException) { } }
    }

    [Fact]
    public void ConsoleLauncherFallsBackToPowerShellWhenTerminalFailsToStart()
    {
        var tool = Path.Combine(Path.GetTempPath(), "tool-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(tool, MinimalPe(3));
            ProcessStartInfo? direct = null;
            // wt 启动返回 false（失败）→ 应回退到直接 PowerShell 并成功。
            var launcher = new ConsoleLauncher(new PowerShellEnvironmentResolver(() => null),
                info => { direct = info; return true; },
                terminalPath: () => @"C:\fake\wt.exe",
                terminalStart: _ => false);
            var result = launcher.Launch(tool, Path.GetTempPath(), null, keepOpen: false);
            Assert.True(result.Started);
            Assert.NotNull(direct);
            Assert.Contains("-EncodedCommand", direct!.Arguments);
        }
        finally { try { File.Delete(tool); } catch (IOException) { } }
    }
}
