using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PlatformPublisher.Persistence;

public interface IDataProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}

public sealed class WindowsDataProtector : IDataProtector
{
    private const int CryptprotectUiForbidden = 0x1;

    public byte[] Protect(byte[] value) => Transform(value, protect: true);
    public byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("安全凭据存储仅支持 Windows。");

        var input = ToBlob(value);
        try
        {
            DATA_BLOB output;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally { if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData); }
    }

    private static DATA_BLOB ToBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DATA_BLOB { cbData = value.Length, pbData = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
