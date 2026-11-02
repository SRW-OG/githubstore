using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitHubStore.Platform.Win32.Shell;

/// <summary>Reads the shell's icon (including shortcut icon locations) without launching or editing the item.</summary>
public static class ShellIconReader
{
    private static readonly SemaphoreSlim WorkerSlots = new(2);

    /// <summary>Returns a 32-bit alpha BMP, or null when no local shell icon is available.</summary>
    public static async Task<byte[]?> ReadIconAsync(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        await WorkerSlots.WaitAsync().ConfigureAwait(false);
        try { return await ReadOnStaThreadAsync(path).ConfigureAwait(false); }
        finally { WorkerSlots.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static Task<byte[]?> ReadOnStaThreadAsync(string path)
    {
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            byte[]? result = null;
            try { result = ReadShellIcon(path); }
            catch (Exception) { /* Shell extensions, invalid shortcuts and inaccessible files use the UI fallback. */ }
            finally { completion.TrySetResult(result); }
        }) { IsBackground = true, Name = "Launcher shell icon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? ReadShellIcon(string path)
    {
        var initialized = CoInitializeEx(0, 2); // COINIT_APARTMENTTHREADED; S_OK and S_FALSE both need balancing.
        if (initialized < 0) return null;
        IShellItemImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, ref iid, out factory) < 0 || factory is null) return null;
            // ICONONLY avoids thumbnail handlers; BIGGERSIZEOK allows the shell to retain a high resolution icon.
            if (factory.GetImage(new NativeSize { Width = 96, Height = 96 }, 0x4 | 0x1, out bitmap) < 0 || bitmap == 0)
                return null;
            return ReadBitmap(bitmap);
        }
        finally
        {
            try
            {
                if (bitmap != 0) DeleteObject(bitmap);
                if (factory is not null) Marshal.FinalReleaseComObject(factory);
            }
            finally { CoUninitialize(); }
        }
    }

    private static byte[]? ReadBitmap(nint bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<NativeBitmap>(), out var description) == 0
            || description.Width is <= 0 or > 1024 || description.Height is <= 0 or > 1024) return null;
        var width = description.Width;
        var height = description.Height;
        var pixels = new byte[checked(width * height * 4)];
        var info = new BitmapInfoHeader
        {
            Size = 40,
            Width = width,
            Height = -height, // Top-down BGRA; no row padding is needed at 32 bits per pixel.
            Planes = 1,
            BitCount = 32,
            SizeImage = (uint)pixels.Length,
        };
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return null;
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height) return null;
        }
        finally { DeleteDC(dc); }

        // Shell image factories return premultiplied alpha; the BMP decoder expects straight RGB.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4) hasAlpha |= pixels[i] != 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (!hasAlpha) pixels[i + 3] = 255; // Older shell handlers may return an opaque RGB bitmap.
            else if (alpha is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, (pixels[i] * 255 + alpha / 2) / alpha);
                pixels[i + 1] = (byte)Math.Min(255, (pixels[i + 1] * 255 + alpha / 2) / alpha);
                pixels[i + 2] = (byte)Math.Min(255, (pixels[i + 2] * 255 + alpha / 2) / alpha);
            }
        }

        const int pixelOffset = 14 + 108; // BITMAPFILEHEADER + BITMAPV4HEADER, including an explicit alpha mask.
        var bmp = new byte[pixelOffset + pixels.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        Write32(bmp, 2, (uint)bmp.Length);
        Write32(bmp, 10, pixelOffset);
        Write32(bmp, 14, 108);
        Write32(bmp, 18, (uint)width);
        Write32(bmp, 22, unchecked((uint)-height));
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28, 2), 32);
        Write32(bmp, 30, 3); // BI_BITFIELDS
        Write32(bmp, 34, (uint)pixels.Length);
        Write32(bmp, 54, 0x00ff0000); // Red, green, blue and alpha masks.
        Write32(bmp, 58, 0x0000ff00);
        Write32(bmp, 62, 0x000000ff);
        Write32(bmp, 66, 0xff000000);
        Write32(bmp, 70, 0x73524742); // LCS_sRGB
        pixels.CopyTo(bmp, pixelOffset);
        return bmp;
    }

    private static void Write32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out nint bitmap);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindingContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetObjectW(nint bitmap, int size, out NativeBitmap description);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint bitmap);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines,
        [Out] byte[] pixels, ref BitmapInfoHeader info, uint usage);
}
