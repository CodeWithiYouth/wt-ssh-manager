using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WtSshManager;

internal static class SecretProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("WtSshManager/profile-password/v1");

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
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string secret)
    {
        var clearBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(Transform(clearBytes, protect: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public static string Unprotect(string protectedSecret)
    {
        var clearBytes = UnprotectBytes(protectedSecret);
        try
        {
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    internal static byte[] UnprotectBytes(string protectedSecret)
    {
        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(protectedSecret);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("连接档案中的加密密码格式无效。", exception);
        }

        try
        {
            return Transform(encryptedBytes, protect: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    private static byte[] Transform(byte[] inputBytes, bool protect)
    {
        var input = CreateBlob(inputBytes);
        var entropy = CreateBlob(OptionalEntropy);
        DataBlob output = default;

        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref input,
                    "WtSshManager SSH password",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);

            if (!succeeded)
            {
                throw new InvalidDataException(
                    protect
                        ? $"无法加密 SSH 密码：{new Win32Exception(Marshal.GetLastWin32Error()).Message}"
                        : "无法解密连接档案中的密码。请使用保存密码时的 Windows 用户账户。");
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                ZeroNativeBuffer(input.Data, input.Size);
                Marshal.FreeHGlobal(input.Data);
            }

            if (entropy.Data != IntPtr.Zero)
            {
                ZeroNativeBuffer(entropy.Data, entropy.Size);
                Marshal.FreeHGlobal(entropy.Data);
            }

            if (output.Data != IntPtr.Zero)
            {
                ZeroNativeBuffer(output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob
        {
            Size = bytes.Length,
            Data = pointer
        };
    }

    private static void ZeroNativeBuffer(IntPtr buffer, int length)
    {
        if (buffer == IntPtr.Zero || length <= 0)
        {
            return;
        }

        for (var offset = 0; offset < length; offset++)
        {
            Marshal.WriteByte(buffer, offset, 0);
        }
    }
}

