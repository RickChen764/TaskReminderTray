using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageTray.Services;

internal static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsageTray.Settings.v1");

    public static string Protect(string plainText)
    {
        var inputBytes = Encoding.UTF8.GetBytes(plainText);
        var input = ToBlob(inputBytes);
        var entropy = ToBlob(Entropy);

        try
        {
            if (!CryptProtectData(ref input, "UsageTray API Key", ref entropy, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Convert.ToBase64String(FromBlob(output));
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    public static string Unprotect(string protectedText)
    {
        var input = ToBlob(Convert.FromBase64String(protectedText));
        var entropy = ToBlob(Entropy);

        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Encoding.UTF8.GetString(FromBlob(output));
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var data = new byte[blob.Size];
        Marshal.Copy(blob.Data, data, 0, blob.Size);
        return data;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
