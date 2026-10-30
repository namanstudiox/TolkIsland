using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TolkIsland.Services;

internal static class SecretProtection
{
    private const int UiForbidden = 0x1;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TolkIsland.Groq.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var cipher = Transform(bytes, protect: true);
            return cipher is null ? string.Empty : Convert.ToBase64String(cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(ciphertext);
        }
        catch (FormatException)
        {
            return null;
        }

        var plain = Transform(bytes, protect: false);
        if (plain is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var dataIn = new DataBlob
        {
            Data = inputHandle.AddrOfPinnedObject(),
            Size = input.Length
        };
        var entropy = new DataBlob
        {
            Data = entropyHandle.AddrOfPinnedObject(),
            Size = Entropy.Length
        };
        var dataOut = default(DataBlob);

        try
        {
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref dataOut);

            if (!ok || dataOut.Data == IntPtr.Zero || dataOut.Size <= 0)
            {
                return null;
            }

            var result = new byte[dataOut.Size];
            Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
            return result;
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Secrets", exception);
            return null;
        }
        finally
        {
            if (dataOut.Data != IntPtr.Zero)
            {
                LocalFree(dataOut.Data);
            }

            entropyHandle.Free();
            inputHandle.Free();
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
        string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
