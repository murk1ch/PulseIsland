using System.Runtime.InteropServices;
using System.Text;

namespace PulseIsland.Services;

/// <summary>
/// Encrypts and decrypts sensitive strings (API keys) using Windows DPAPI (crypt32.dll)
/// scoped to the current Windows user without requiring external NuGet packages.
/// </summary>
public static class SecurityHelper
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string ProtectString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var inBlob = CreateBlob(plainBytes);
        try
        {
            if (!CryptProtectData(ref inBlob, "PulseIsland", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outBlob))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var cipherBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
                return Convert.ToBase64String(cipherBytes);
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero)
                    LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    public static string UnprotectString(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
            return string.Empty;

        try
        {
            var cipherBytes = Convert.FromBase64String(protectedBase64);
            var inBlob = CreateBlob(cipherBytes);
            try
            {
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outBlob))
                    return string.Empty;

                try
                {
                    var plainBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    if (outBlob.pbData != IntPtr.Zero)
                        LocalFree(outBlob.pbData);
                }
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero)
                    Marshal.FreeHGlobal(inBlob.pbData);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { cbData = data.Length, pbData = ptr };
    }
}
