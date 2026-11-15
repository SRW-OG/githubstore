using System.Buffers.Binary;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>
/// 静态识别一个 Windows 可执行文件是否为控制台程序：只读取 PE 文件头里的 Subsystem 字段，
/// 绝不执行程序、不加载它，也不依赖扩展名或文件名。
/// Subsystem == IMAGE_SUBSYSTEM_WINDOWS_CUI (3) 即命令行/控制台程序。
/// 任何读取或格式错误都按“不是控制台程序”处理，让启动路径回落到普通的直接启动，
/// 而不是因为探测失败把桌面程序错误地塞进终端。
/// </summary>
public static class ConsoleProgramDetector
{
    /// <summary>IMAGE_SUBSYSTEM_WINDOWS_CUI：Windows 控制台字符界面程序。</summary>
    private const ushort SubsystemWindowsCui = 3;

    public static bool IsConsoleProgram(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            return IsConsoleProgram(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断流入的 PE 数据是否是控制台程序。只读取必要的头部字节，超出流长度或格式不符时返回 false。
    /// </summary>
    public static bool IsConsoleProgram(Stream stream)
    {
        try
        {
            if (stream.Length < 64) return false;
            stream.Position = 0;
            Span<byte> dos = stackalloc byte[64];
            if (stream.Read(dos) != 64 || dos[0] != 'M' || dos[1] != 'Z') return false;
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[60..]);
            // 至少要有 COFF 头(24) 可读。
            if (peOffset < 64 || peOffset > stream.Length - 24) return false;
            stream.Position = peOffset;
            Span<byte> pe = stackalloc byte[24];
            if (stream.Read(pe) != 24) return false;
            if (pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0) return false;
            var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(pe[20..]);
            // Subsystem 位于可选头偏移 68，需要可选头至少有 70 字节。
            if (optionalHeaderSize < 70) return false;
            var subsystemOffset = (long)peOffset + 24 + 68;
            if (subsystemOffset + 2 > stream.Length) return false;
            stream.Position = subsystemOffset;
            Span<byte> subsystem = stackalloc byte[2];
            if (stream.Read(subsystem) != 2) return false;
            return BinaryPrimitives.ReadUInt16LittleEndian(subsystem) == SubsystemWindowsCui;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 结合用户选择的启动方式决定条目是否在 PowerShell 中启动。
    /// Auto 需要读取控制台程序判定；Direct 永远 false；PowerShell 永远 true。
    /// </summary>
    public static bool ShouldLaunchInConsole(LaunchMode mode, string? executablePath) => mode switch
    {
        LaunchMode.PowerShell => true,
        LaunchMode.Direct => false,
        _ => IsConsoleProgram(executablePath),
    };
}
