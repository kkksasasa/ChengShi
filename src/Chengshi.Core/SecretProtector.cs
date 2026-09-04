using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Chengshi.Core;

/// <summary>
/// 用 Windows DPAPI（机器范围）加解密本机敏感串，比如 SMTP 授权码。
/// 存储格式带前缀「dpapi:v1:」，读到旧版明文时原样返回，写回时自动升级成密文。
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = "Chengshi.Smtp.Password.v1"u8.ToArray();

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || plainText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plainText;
        }

        var buffer = ProtectedData(Entropy, Encoding.UTF8.GetBytes(plainText), protect: true);
        return Prefix + Convert.ToBase64String(buffer);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // 旧版本存的是明文：能用就先返回，下次保存时会被 Protect 包起来。
            return stored;
        }

        try
        {
            var buffer = ProtectedData(Entropy, Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("本机加密数据解不开（换机迁移或系统重装后需重新填写 SMTP 授权码）。", ex);
        }
    }

    public static bool IsProtected(string stored) =>
        stored.StartsWith(Prefix, StringComparison.Ordinal);

    private static byte[] ProtectedData(byte[] entropy, byte[] data, bool protect)
    {
        var input = new DataBuffer(data);
        var entropyBuffer = new DataBuffer(entropy);
        var output = new DataBuffer();
        try
        {
            var flags = CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE;
            var ok = protect
                ? CryptProtectData(
                    ref input, "Chengshi", ref entropyBuffer, IntPtr.Zero, IntPtr.Zero, flags, ref output)
                : CryptUnprotectData(
                    ref input, IntPtr.Zero, ref entropyBuffer, IntPtr.Zero, IntPtr.Zero, flags, ref output);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI 加解密失败。");
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            input.Dispose();
            entropyBuffer.Dispose();
            output.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBuffer : IDisposable
    {
        public int Size;
        public IntPtr Data;

        public DataBuffer(byte[] data)
        {
            Size = data.Length;
            Data = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, Data, data.Length);
        }

        public void Dispose()
        {
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBuffer dataIn,
        string dataDescr,
        ref DataBuffer optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBuffer dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBuffer dataIn,
        IntPtr ppszDataDescr,
        ref DataBuffer optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBuffer dataOut);
}
