using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GitHubStore.Platform.Win32.Security;

/// <summary>Current-user Windows DPAPI; has no dependency on App or Core.</summary>
public static class DpapiSecretProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is required to protect proxy credentials.");

        var source = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var result = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            var succeeded = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out result);
            if (!succeeded)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            if (result.Length < 0 || (result.Length > 0 && result.Data == IntPtr.Zero))
                throw new CryptographicException("DPAPI returned an invalid result.");
            var output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, result.Length);
            return output;
        }
        finally
        {
            Clear(source);
            Marshal.FreeHGlobal(source.Data);
            if (result.Data != IntPtr.Zero)
            {
                Clear(result);
                _ = LocalFree(result.Data);
            }
        }
    }

    private static void Clear(DataBlob blob)
    {
        // Avoid retaining native plaintext buffers after either operation.
        if (blob.Data == IntPtr.Zero) return;
        for (var index = 0; index < blob.Length; index++)
            Marshal.WriteByte(blob.Data, index, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
