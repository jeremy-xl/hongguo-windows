using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SM3Digest = Org.BouncyCastle.Crypto.Digests.SM3Digest;

namespace HongguoTV.Core.Crypto;

internal static class Bytes
{
    public static byte[] Hex(string value)
    {
        if (value.Length % 2 != 0) throw new ArgumentException("hex length");
        var result = new byte[value.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return result;
    }

    public static string HexString(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();

    public static byte[] Decode64(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        if (normalized.Length % 4 == 1) throw new FormatException("Invalid Base64 length");
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        return Convert.FromBase64String(normalized);
    }

    public static string Base64(ReadOnlySpan<byte> data) => Convert.ToBase64String(data);

    public static byte[] Le32(int n) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, n); return b; }
    public static byte[] Be32(int n) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, n); return b; }
    public static byte[] Le64(long n) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, n); return b; }

    public static int I32(ReadOnlySpan<byte> data, int at, bool little = false) => little
        ? BinaryPrimitives.ReadInt32LittleEndian(data.Slice(at, 4))
        : BinaryPrimitives.ReadInt32BigEndian(data.Slice(at, 4));

    public static long U32(ReadOnlySpan<byte> data, int at) => (uint)I32(data, at);
    public static long I64(ReadOnlySpan<byte> data, int at, bool little = false) => little
        ? BinaryPrimitives.ReadInt64LittleEndian(data.Slice(at, 8))
        : BinaryPrimitives.ReadInt64BigEndian(data.Slice(at, 8));

    public static byte[] Md5(ReadOnlySpan<byte> data) => MD5.HashData(data);
    public static byte[] Sha512(ReadOnlySpan<byte> data) => SHA512.HashData(data);

    public static byte[] Sm3(ReadOnlySpan<byte> data)
    {
        var digest = new SM3Digest();
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[32];
        digest.DoFinal(result, 0);
        return result;
    }

    public static byte[] AesCbcEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
        => AesTransform(key, iv, data, encrypt: true, padding: false);

    public static byte[] AesCbcDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
        => AesTransform(key, iv, data, encrypt: false, padding: false);

    public static byte[] AesEcbEncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var transform = aes.CreateEncryptor();
        var output = new byte[16];
        transform.TransformBlock(block.ToArray(), 0, 16, output, 0);
        return output;
    }

    public static byte[] AesCbcEncryptPkcs7(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var transform = aes.CreateEncryptor();
        return transform.TransformFinalBlock(data.ToArray(), 0, data.Length);
    }

    private static byte[] AesTransform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data, bool encrypt, bool padding)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = padding ? PaddingMode.PKCS7 : PaddingMode.None;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data.ToArray(), 0, data.Length);
    }

    public static byte[] Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length];
        for (var i = 0; i < a.Length; i++) result[i] = (byte)(a[i] ^ b[i % b.Length]);
        return result;
    }

    public static int Ushr(int v, int n) => (int)((uint)v >> n);
    public static long Ushr64(long v, int n) => (long)((ulong)v >> n);
    public static int Rol(int v, int n) => unchecked((int)BitOperations.RotateLeft((uint)v, n));
    public static int Ror(int v, int n) => unchecked((int)BitOperations.RotateRight((uint)v, n));
    public static long Rol64(long v, int n) => unchecked((long)BitOperations.RotateLeft((ulong)v, n));
    public static long Ror64(long v, int n) => unchecked((long)BitOperations.RotateRight((ulong)v, n));
    public static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0f0f0f0fu) | ((v & 0x0f0f0f0fu) << 4);
        v = ((v >> 8) & 0x00ff00ffu) | ((v & 0x00ff00ffu) << 8);
        return (v >> 16) | (v << 16);
    }

    public static byte[] Join(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        var at = 0;
        foreach (var array in arrays) { array.CopyTo(result, at); at += array.Length; }
        return result;
    }

    public static string EncodeQuery(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '*' or '_')
                sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString().Replace("+", "%20").Replace("%7E", "~");
    }

    public static string EncodeValues(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("&", values.Select(pair => $"{EncodeQuery(pair.Key)}={EncodeQuery(pair.Value)}"));
}
