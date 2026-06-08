using System.Runtime.InteropServices;
using System.Text;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Security;

/// <summary>
/// Encrypts/decrypts secrets (API keys) at rest using Windows DPAPI scoped to the
/// current user. Implemented via crypt32 P/Invoke to avoid an extra NuGet
/// dependency. Encrypted blobs are only decryptable by the same Windows user on
/// the same machine.
/// </summary>
public static class SecretProtector
{
    // Extra entropy ties the blob to this app; not a secret itself.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReadSelectedTextTts.secrets.v1");
    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(CryptProtect(data, encrypt: true));
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            var clear = CryptProtect(data, encrypt: false);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception ex)
        {
            Log.Wrn($"Failed to decrypt stored secret: {ex.Message}");
            return null;
        }
    }

    private static byte[] CryptProtect(byte[] input, bool encrypt)
    {
        var inBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outBlob = default(DataBlob);

        try
        {
            inBlob = Allocate(input);
            entropyBlob = Allocate(Entropy);

            var ok = encrypt
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob);

            if (!ok)
            {
                throw new InvalidOperationException($"DPAPI {(encrypt ? "encrypt" : "decrypt")} failed (Win32 {Marshal.GetLastWin32Error()}).");
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (entropyBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static DataBlob Allocate(byte[] data)
    {
        var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
