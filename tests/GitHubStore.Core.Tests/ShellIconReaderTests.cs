using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using GitHubStore.Platform.Win32.Shell;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ShellIconReaderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData("https://example.invalid/application.exe")]
    public async Task NonlocalOrRelativePaths_ReturnNoImage(string path)
    {
        Assert.Null(await ShellIconReader.ReadIconAsync(path));
    }

    [Fact]
    public async Task MissingLocalFile_ReturnsNoImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-icon-{Guid.NewGuid():N}.lnk");
        Assert.Null(await ShellIconReader.ReadIconAsync(path));
    }

    [Fact]
    public async Task WindowsExecutable_ReturnsBitmapWithExplicitAlpha()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        AssertBitmap(await ShellIconReader.ReadIconAsync(path));
    }

    [Fact]
    public async Task ShortcutWithExplicitIcon_ReturnsBitmapWithoutModifyingShortcut()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"launcher-icon-{Guid.NewGuid():N}.lnk");
        try
        {
            await CreateShortcutAsync(path, Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            var before = await File.ReadAllBytesAsync(path);
            AssertBitmap(await ShellIconReader.ReadIconAsync(path));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void AssertBitmap(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 122);
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.Equal(108u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14, 4)));
        Assert.Equal(0xff000000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(66, 4)));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        Assert.InRange(width, 1, 1024);
        Assert.InRange(-height, 1, 1024);
        Assert.Equal(offset + width * -height * 4, bytes.Length);
        Assert.Contains(Enumerable.Range(0, width * -height), i => bytes[offset + i * 4 + 3] > 0);
    }

    [SupportedOSPlatform("windows")]
    private static Task CreateShortcutAsync(string path, string target)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? instance = null;
            var initialized = CoInitializeEx(0, 2);
            try
            {
                Marshal.ThrowExceptionForHR(initialized);
                var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
                instance = Activator.CreateInstance(type)!;
                var link = (IShellLinkW)instance;
                link.SetPath(target);
                link.SetIconLocation(target, 0);
                ((IPersistFile)instance).Save(path, true);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally
            {
                if (instance is not null) Marshal.FinalReleaseComObject(instance);
                if (initialized >= 0) CoUninitialize();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint path, int length, nint findData, uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription(nint description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(nint directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(nint arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation(nint path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
