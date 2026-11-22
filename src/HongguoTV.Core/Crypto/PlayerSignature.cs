using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HongguoTV.Core.Crypto;

internal sealed class PlayerSignature
{
    private static readonly byte[] GorgonKey = Bytes.Hex("44b9b9d9a4aef9fca493aa757ca3c2c4a496938f");
    private static readonly byte[] ArgusSecret = Bytes.Hex("ac1adaae95a7af94a5114ab3b3a97dd80050aa0a39314c40528caec95256c28c");
    private static readonly byte[] ArgusMask = Bytes.Hex("d04ffdffd04ffdff");

    public Dictionary<string, string> Headers(string query, byte[]? body, string deviceId,
        long? now = null, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(11);
        if (nonce.Length != 11) throw new ArgumentException("nonce length");
        var clock = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ts = clock / 1000;
        var input = Bytes.Join(
            Bytes.Md5(Encoding.UTF8.GetBytes(query))[..4],
            body is null ? new byte[4] : Bytes.Md5(body)[..4],
            new byte[4], Bytes.Hex("00060b1c"), Bytes.Be32(unchecked((int)ts)));
        for (var i = 0; i < input.Length; i++) input[i] = (byte)(input[i] ^ GorgonKey[i]);
        for (var i = 0; i < input.Length; i++)
        {
            var v = input[i];
            var swapped = (byte)(((v << 4) | (v >> 4)) & 255);
            input[i] = (byte)((Bytes.ReverseBits((uint)(swapped ^ input[(i + 1) % input.Length])) >> 24) ^ 0xeb);
        }
        var headers = new Dictionary<string, string>
        {
            ["X-Gorgon"] = Bytes.HexString(Bytes.Join(Bytes.Hex("8404401c0000"), input)),
            ["X-Khronos"] = ts.ToString(),
            ["X-SS-Req-Ticket"] = clock.ToString(),
            ["X-Argus"] = Argus(query, body, deviceId, ts, nonce),
            ["X-Ladon"] = Ladon(ts, nonce[2..6]),
        };
        if (body is not null) headers["X-SS-STUB"] = Bytes.HexString(Bytes.Md5(body)).ToUpperInvariant();
        return headers;
    }

    private static byte[] Pad(byte[] data)
    {
        var padding = 16 - data.Length % 16;
        return Bytes.Join(data, Enumerable.Repeat((byte)padding, padding).ToArray());
    }

    private static void Varint(List<byte> output, ulong value)
    {
        while ((value >> 7) != 0) { output.Add((byte)((value & 127) | 128)); value >>= 7; }
        output.Add((byte)value);
    }

    private static byte[] Proto(IEnumerable<KeyValuePair<int, object>> fields)
    {
        var output = new List<byte>();
        foreach (var (tag, value) in fields)
        {
            if (value is long or int or uint or ulong)
            {
                Varint(output, (ulong)(tag << 3));
                Varint(output, Convert.ToUInt64(value));
            }
            else
            {
                var bytes = value is byte[] raw ? raw : Encoding.UTF8.GetBytes(value.ToString()!);
                Varint(output, (ulong)((tag << 3) | 2));
                Varint(output, (ulong)bytes.Length);
                output.AddRange(bytes);
            }
        }
        return output.ToArray();
    }

    private static long Word(byte[] data, int at) => Bytes.I64(data, at, true);
    private static byte[] Le(long n) => Bytes.Le64(n);

    private static string Ladon(long ts, byte[] salt)
    {
        var ascii = Encoding.ASCII.GetBytes(Bytes.HexString(Bytes.Md5(Bytes.Join(salt, Encoding.UTF8.GetBytes("8662")))));
        var queue = new Queue<long>();
        queue.Enqueue(Word(ascii, 16));
        queue.Enqueue(Word(ascii, 24));
        var left = Word(ascii, 0);
        var right = Word(ascii, 8);
        var keys = new long[34];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = left;
            var next = unchecked(Bytes.Ror64(right, 8) + left) ^ (uint)i;
            queue.Enqueue(next);
            left = unchecked(next ^ Bytes.Rol64(left, 3));
            right = queue.Dequeue();
        }
        var data = Pad(Encoding.UTF8.GetBytes($"{ts}-1611921764-8662"));
        var result = new List<byte>(salt);
        for (var at = 0; at < data.Length; at += 16)
        {
            long a = Word(data, at);
            long b = Word(data, at + 8);
            foreach (var k in keys)
            {
                b = unchecked(k ^ (a + Bytes.Ror64(b, 8)));
                a = unchecked(b ^ Bytes.Rol64(a, 3));
            }
            result.AddRange(Le(a));
            result.AddRange(Le(b));
        }
        return Bytes.Base64(result.ToArray());
    }

    private static string Argus(string query, byte[]? body, string deviceId, long ts, byte[] nonce)
    {
        var random = (long)(uint)BitConverter.ToInt32(nonce, 6) & 0x7fffffffL;
        var nested = Proto(new[]
        {
            new KeyValuePair<int, object>(1, 1), new(2, 1), new(3, 1), new(7, 3348294860L),
        });
        var environment = Proto(new[]
        {
            new KeyValuePair<int, object>(1, "25053RT47C"), new(2, "16"), new(3, "update_64"), new(4, 2147483648L),
        });
        var message = Proto(new[]
        {
            new KeyValuePair<int, object>(1, 0x40401252), new(2, 2), new(3, random), new(4, "8662"),
            new(5, deviceId), new(6, "1611921764"), new(7, "7.3.5.32"), new(8, "v04.07.01-ml-android"),
            new(9, 135135744L), new(10, new byte[8]), new(11, 0L), new(12, ts * 2),
            new(13, Bytes.Sm3(body is null ? new byte[16] : Bytes.Md5(body))[..6]),
            new(14, Bytes.Sm3(Encoding.UTF8.GetBytes(query))[..6]), new(15, nested), new(16, Array.Empty<byte>()),
            new(20, "none"), new(21, 738), new(23, environment), new(25, 2),
        });
        var derived = Bytes.Sm3(Bytes.Join(ArgusSecret, Bytes.Hex("3ccc567b"), ArgusSecret));
        var keys = new long[72];
        for (var i = 0; i < 4; i++) keys[i] = Word(derived, i * 8);
        for (var i = 4; i < keys.Length; i++)
        {
            var x = unchecked(Bytes.Ror64(keys[i - 1], 3) ^ keys[i - 3]);
            keys[i] = unchecked(~keys[i - 4] ^ x ^ Bytes.Ror64(x, 1)
                ^ unchecked((long)((0x3dc94c3a046d678bUL >> ((i - 4) % 62)) & 1)) ^ 3);
        }
        var blocks = Pad(message);
        var encrypted = new List<byte>();
        for (var at = 0; at < blocks.Length; at += 16)
        {
            long a = Word(blocks, at);
            long b = Word(blocks, at + 8);
            foreach (var k in keys)
            {
                var next = unchecked(a ^ (Bytes.Rol64(b, 1) & Bytes.Rol64(b, 8))
                    ^ Bytes.Rol64(b, 2) ^ k);
                a = b;
                b = next;
            }
            encrypted.AddRange(Le(a));
            encrypted.AddRange(Le(b));
        }
        var scrambled = Bytes.Xor(encrypted.ToArray(), ArgusMask);
        var container = Bytes.Join(
            Bytes.Hex("a6e783ee7001100918"),
            Bytes.Join(ArgusMask, scrambled).Reverse().ToArray(),
            Bytes.Hex("567b"));
        var cipher = Bytes.AesCbcEncryptPkcs7(Bytes.Md5(ArgusSecret[..16]), Bytes.Md5(ArgusSecret[16..32]), container);
        return Bytes.Base64(Bytes.Join(Bytes.Hex("3ccc"), cipher));
    }
}
