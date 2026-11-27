using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace HongguoTV.Core.Crypto;

internal sealed class Signer
{
    public sealed record Request(string Url, Dictionary<string, string> Headers, byte[]? Body);

    private readonly Func<long> _clock;
    private readonly Dictionary<string, string> _device;
    private readonly Func<double> _random;
    private readonly string _processNonce;
    private readonly string _deviceToken;
    private readonly bool _mobileV5;
    private readonly long _processStarted;

    public Signer(Func<long>? clock = null, Dictionary<string, string>? device = null,
        Func<double>? random = null, string? processNonce = null, bool mobileV5 = false)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _device = device ?? VendorConstants.Device;
        _random = random ?? Random.Shared.NextDouble;
        _mobileV5 = mobileV5;
        _processStarted = _clock();
        if (processNonce is not null)
        {
            _processNonce = processNonce;
        }
        else
        {
            var nonce = new byte[18];
            RandomNumberGenerator.Fill(nonce);
            _processNonce = Convert.ToBase64String(nonce).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        _deviceToken = _processNonce.Length == 25 && _processNonce.StartsWith('A')
            ? _processNonce
            : "A" + (_processNonce + "AAAAAAAAAAAAAAAAAAAAAAAA").Substring(0, 24);
    }

    private int Rand() => unchecked((int)(long)(_random() * 4294967296.0));
    private static long Unsigned(int v) => (uint)v;

    private static byte[] Varint(long value)
    {
        var output = new List<byte>();
        var n = value;
        while (n > 127) { output.Add((byte)((n & 127) | 128)); n = (long)((ulong)n >> 7); }
        output.Add((byte)n);
        return output.ToArray();
    }

    private readonly record struct Field(int Tag, object Value, string Type);
    private static Field F(int tag, object value, string type = "string") => new(tag, value, type);

    private static byte[] Proto(params Field[] fields)
    {
        var output = new List<byte>();
        foreach (var (tag, value, type) in fields)
        {
            if (type == "sint")
            {
                var n = Convert.ToInt64(value);
                output.AddRange(Varint(tag << 3));
                output.AddRange(Varint(n < 0 ? -n * 2 - 1 : n * 2));
            }
            else if (type == "float")
            {
                output.AddRange(Varint((tag << 3) | 5));
                var bytes = BitConverter.GetBytes(Convert.ToSingle(value));
                output.AddRange(bytes);
            }
            else
            {
                var bytes = value is byte[] raw ? raw : System.Text.Encoding.UTF8.GetBytes(value.ToString()!);
                if (bytes.Length == 0) continue;
                output.AddRange(Varint((tag << 3) | 2));
                output.AddRange(Varint(bytes.Length));
                output.AddRange(bytes);
            }
        }
        return output.ToArray();
    }

    private static int GetIv(int initial, byte[] data)
    {
        var value = initial;
        for (var i = 0; i < data.Length; i++)
        {
            var d = data[i];
            value = (i & 1) == 0
                ? unchecked(Bytes.Ushr(value, 4) ^ value ^ (value << 6) ^ d)
                : unchecked(~((Bytes.Ushr(value, 7) ^ value ^ (d | (value << 12)))));
        }
        return value;
    }

    private static int Iv(byte[] query, byte[] body, int ts) =>
        GetIv(GetIv(GetIv(0x20230928, query), body), Bytes.Le32(ts));

    public int Branch(string query, byte[]? body, int ts)
    {
        var iv = Iv(Bytes.Sm3(System.Text.Encoding.UTF8.GetBytes(query)),
            body is null ? new byte[16] : Bytes.Md5(body), ts);
        var branch = iv & 15;
        return _mobileV5 ? (branch == 12 ? 0 : 1) : branch % 3;
    }

    private static int SumMd5(byte[] data)
    {
        var check = 0x20220420;
        for (var i = 0; i < 12; i++)
        {
            var temp = unchecked(Bytes.Ushr(check, (i & 1) == 0 ? 3 : 5) ^ check);
            check = (i & 1) == 0
                ? unchecked(data[i] ^ (check << 7))
                : unchecked(~(data[i] | (check << 11)));
            check = unchecked(check ^ temp);
        }
        return unchecked((check | 4) ^ 0x1000000);
    }

    public byte[] HashF13(byte[] query, byte[] body, int ts)
    {
        var ivValue = Iv(query, body, ts) & 15;
        if (_mobileV5 ? ivValue != 12 : ivValue % 3 != 0) throw new InvalidOperationException("Unsupported hash branch");
        var index = ivValue / 3;
        var ivV1 = _mobileV5 ? unchecked((int)0x9d4a97c2) : new[]
        {
            unchecked((int)0xc4a78580), unchecked((int)0xb3c0fd39), unchecked((int)0xc58c5686),
            unchecked((int)0xc9aa3ba7), unchecked((int)0xf5a7adf2), unchecked((int)0x963c2ed1),
        }[index];
        var count = unchecked(ivV1 + ts);
        var table = VendorConstants.HashTable.Select(v => Bytes.Ror(v, (count + 1) & 255)).ToArray();
        var shift = (count + 2) & 7;
        var pad = new byte[] { 0xfa, 0x45, 0x61, 0xd7 }
            .Select(v => (byte)(((v | (v << 8)) >> shift) & 255)).ToArray();
        var data = Bytes.Join(query, body, Bytes.Le32(ts), pad, Bytes.Hex("00000000000001a0"));
        var di = new List<int>();
        for (var i = 0; i + 4 <= data.Length; i += 4) di.Add(Bytes.I32(data, i));
        for (var i = 0; i < 112; i++)
        {
            var x = di[i + 1];
            var y = di[i + 14];
            di.Add(unchecked(di[i] + di[i + 9]
                + (Bytes.Rol(x, 14) ^ Bytes.Rol(x, 25) ^ Bytes.Ushr(x, 3))
                + (Bytes.Rol(y, 13) ^ Bytes.Rol(y, 15) ^ Bytes.Ushr(y, 10))));
        }
        var initial = new[]
        {
            0x7aba4fc8, 0x67166507, 0x6403fa00, 0x340f512f,
            984304912, unchecked((int)3005047866L), unchecked((int)2874125293L), unchecked((int)2152413264L),
        }.Select(v => Bytes.Ror(v, count)).ToArray();
        var chosen = _mobileV5 ? new[] { 96, 1, 3, 4, 0, 6, 5, 2, 0, 7, 7 } : new[]
        {
            new[] { 101, 5, 7, 6, 3, 2, 1, 0, 5, 4, 3 },
            new[] { 96, 0, 6, 7, 5, 3, 2, 1, 5, 4, 4 },
            new[] { 96, 7, 6, 2, 1, 4, 0, 5, 4, 3, 5 },
            new[] { 99, 3, 6, 2, 4, 5, 1, 0, 0, 7, 6 },
            new[] { 96, 0, 5, 6, 7, 3, 1, 2, 5, 4, 4 },
            new[] { 100, 2, 0, 3, 5, 4, 6, 7, 2, 1, 5 },
        }[index];
        var d = initial.ToList();
        for (var i = 0; i < chosen[0]; i++)
        {
            var b = unchecked(ivV1 + i);
            var n1 = unchecked(((d[chosen[3]] ^ d[chosen[4]]) & d[chosen[1]]) ^ d[chosen[3]]);
            var n2 = unchecked(Bytes.Rol(d[chosen[1]], 26) ^ Bytes.Rol(d[chosen[1]], 21) ^ Bytes.Rol(d[chosen[1]], 7));
            var n4 = unchecked(di[b & 127] + n1 + n2 + table[b & 63] + d[chosen[5]]);
            var n5 = unchecked(Bytes.Rol(d[chosen[2]], 30) ^ Bytes.Rol(d[chosen[2]], 19) ^ Bytes.Rol(d[chosen[2]], 10));
            var n6 = unchecked((d[chosen[2]] & d[chosen[6]]) | ((d[chosen[2]] | d[chosen[6]]) & d[chosen[7]]));
            var old = d[chosen[9]];
            d.Insert(0, d[7]);
            d.RemoveAt(8);
            d[chosen[10]] = unchecked(n5 + n6 + n4);
            d[chosen[8]] = unchecked(old + n4);
        }
        var ret = Enumerable.Range(0, 8).Aggregate(Array.Empty<byte>(), (a, i) => Bytes.Join(a, Bytes.Be32(unchecked(d[i] + initial[i]))));
        var folded = Bytes.Xor(ret.AsSpan(0, 16), ret.AsSpan(16, 16));
        return Bytes.Join(folded, Bytes.Le32(unchecked(SumMd5(folded) | (_mobileV5 ? 0x20 : 0))));
    }

    public string Gorgon(string query, byte[]? body, int ts, int random)
    {
        var input = Bytes.Join(
            Bytes.Md5(System.Text.Encoding.UTF8.GetBytes(query))[..4],
            body is null ? new byte[4] : Bytes.Md5(body)[..4],
            new byte[4], Bytes.Le32(67503104), Bytes.Be32(ts));
        var key = new[] { 0x4a, 0x40, 0x16, (random >> 8) & 255, 0x47, 0x6c, 1, random & 255 };
        var s = Enumerable.Range(0, 256).ToArray();
        var j = 0;
        for (var i = 0; i < 256; i++) { j = (j + s[i] + key[i % 8]) & 255; s[i] = s[j]; }
        j = 0;
        var output = new byte[input.Length];
        for (var n = 0; n < input.Length; n++)
        {
            var i = n + 1;
            j += s[i];
            var y = s[j & 255];
            s[i] = y;
            output[n] = (byte)(input[n] ^ s[(y + y) & 255]);
        }
        for (var i = 0; i < output.Length; i++)
        {
            var value = ((output[i] >> 4) | (output[i] << 4)) & 255;
            var next = output[(i + 1) % output.Length];
            var reversed = Bytes.ReverseBits((uint)(next ^ value)) >> 24;
            output[i] = (byte)~(reversed ^ 20);
        }
        return Bytes.HexString(Bytes.Join(
            Bytes.Hex("8404"), new[] { (byte)random, (byte)(random >> 8), (byte)0x40, (byte)1 }, output));
    }

    private byte[] DeviceProto(Dictionary<string, string>? identity = null)
    {
        var who = identity ?? _device;
        return Proto(
            F(1, 1, "sint"), F(2, 2, "sint"), F(3, "8662"), F(4, who["device_id"]),
            F(5, _processNonce), F(6, "!noperm!"), F(7, -888888, "sint"), F(8, -888888, "sint"),
            F(9, 3, "sint"), F(10, -888888, "sint"), F(11, "!notset!"), F(12, "Asia/Shanghai,8"),
            F(13, "zh_CN"), F(14, 4, "sint"), F(16, 0f, "float"), F(17, 0f, "float"),
            F(18, 0f, "float"), F(19, 0f, "float"), F(20, 0f, "float"), F(21, 0f, "float"),
            F(22, "16"), F(23, 41, "sint"), F(24, 36, "sint"), F(25, _processStarted, "sint"),
            F(26, _processStarted, "sint"), F(27, _processStarted, "sint"), F(28, _processStarted, "sint"),
            F(29, -1, "sint"), F(30, "25053RT47C"), F(31, "Redmi"), F(32, "25053RT47C"),
            F(33, "25053RT47C"), F(34, "Xiaomi"), F(35, "Redmi"), F(36, "Redmi"), F(38, 31, "sint"));
    }

    private static byte[] Xmxor(byte[] data, byte[] key)
    {
        var encoded = new int[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var at = (i * 4) & 28;
            var d0 = key[at];
            var d1 = key[at + 1];
            var v = data[i];
            var d2 = ((((v << 4) | (v >> 4)) & 255) + d0) & 255;
            d2 = ~d2 ^ d1;
            d2 = (((d2 & 255) << 3) | ((d2 & 255) >> 5)) & 255;
            d2 = (d2 + d1) & 255;
            d2 = (d2 ^ d0) & 255;
            encoded[data.Length - i - 1] = ~d2 & 255;
        }
        var last = encoded[^1] ^ encoded[^2];
        var first = encoded[0];
        encoded[0] = (~last + first) & 255;
        encoded[1] = ((encoded[0] ^ encoded[^1] ^ 254) + encoded[1]) & 255;
        encoded[2] = (encoded[2] + ((last - first) ^ (((encoded[1] << 3) | (encoded[1] >> 5)) & 255) ^ 2)) & 255;
        for (var i = 0; i < encoded.Length - 4; i++)
        {
            var temp = ((((encoded[i + 2] << 3) | (encoded[i + 2] >> 5)) & 255) ^ encoded[i + 1] ^ (i + 3));
            encoded[i + 3] = (~temp + encoded[i + 3]) & 255;
        }
        encoded[^1] = encoded[^1] ^ encoded[^2];
        encoded[0] = ((encoded[0] ^ encoded[1]) + encoded.Skip(1).Sum()) & 255;
        return encoded.Select(v => (byte)v).ToArray();
    }

    public string Medusa(string url, byte[]? body, int ts, Dictionary<string, string>? identity = null)
    {
        if (_mobileV5) return MedusaV5(url, body, ts, identity ?? _device);
        var who = identity ?? _device;
        var bodyMd5 = body is null ? new byte[16] : Bytes.Md5(body);
        var query = System.Text.Encoding.UTF8.GetBytes(url[(url.IndexOf('?') + 1)..]);
        var querySm3 = Bytes.Sm3(query);
        var queryBodyTs = HashF13(querySm3, bodyMd5, ts);
        var nested = Proto(F(1, 111, "sint"), F(2, 10, "sint"), F(3, 694367, "sint"), F(5, 586952199, "sint"));
        var messageRand = Unsigned(Rand());
        var envLaunch = (int)(_random() * 21) + 100;
        var envPid = (int)(_random() * 2000) + 10001;
        var version = who["version_name"];
        var env = Proto(
            F(1, envLaunch, "sint"), F(2, 146331399, "sint"), F(3, 146331396, "sint"), F(5, 7, "sint"),
            F(6, "v04.06.04.03-bugfix"), F(7, envPid, "sint"), F(12, DeviceProto(who)),
            F(13, Proto(F(1, _clock() / 1000, "sint"), F(2, -2, "sint"), F(4, 200, "sint"))), F(14, version));
        var queryHash = Bytes.Sm3(Bytes.Join(query, bodyMd5, System.Text.Encoding.UTF8.GetBytes("none")));
        var message = Proto(
            F(1, Bytes.Hex("f7e85ffad7d7dc3bd62ac87057cf6118")), F(2, 3, "sint"), F(3, messageRand, "sint"),
            F(4, "8662"), F(5, who["device_id"]), F(6, "1588093228"), F(7, version),
            F(8, "v04.06.04-ml-android"), F(9, 67503104, "sint"), F(10, Bytes.Hex("4001000000000000")),
            F(12, ts, "sint"), F(13, queryBodyTs), F(14, querySm3[..6]), F(15, nested), F(16, _processNonce),
            F(17, ts, "sint"), F(19, queryHash), F(20, "none"), F(21, V5Constants.Field21, "sint"), F(23, env),
            F(24, "{\"cmr\":16777216,\"cmr2\":16777216,\"un_h\":1879194040,\"vpn\":0,\"kd\":0,\"fkd\":3672518972,\"pd\":-1872573247,\"dyn\":\"\",\"do\":0,\"tk\":true}"));
        var r = Rand();
        var signKey = Bytes.Hex("8ebdfa3806ecc5cee79423e6029ed82540bc2218bb7eaef71cb691f7aa8aa2f5");
        var hash = Bytes.Sm3(Bytes.Join(signKey, Bytes.Le32(r), signKey));
        var d1 = (r >> 16) & 255;
        var seed = Bytes.Le32(unchecked(~(((d1 << 11) | Bytes.Ushr(r, 24)) ^ (d1 >> 5) ^ d1)));
        var transformed = Bytes.Join(Bytes.Hex("4001000000000000"), Xmxor(message, hash)).Reverse().ToArray();
        for (var i = 0; i < transformed.Length; i++) transformed[i] = (byte)(transformed[i] ^ seed[~i & 3]);
        var check = ((querySm3[0] & 63) << 14) | 0x18000001 | ((queryBodyTs[0] & 63) << 8);
        var packed = Bytes.Join(new byte[] { 0x35 }, Bytes.Le32(Rand()), Bytes.Le32(check), transformed,
            new[] { (byte)(r >> 16), (byte)(r >> 24) });
        var encrypted = new AesV3(Bytes.Hex("f1593376766ea98d34f31b057a9d5be4"), ts).Encrypt(packed, Bytes.Hex("1fe109a4125283f418de9e051a969e12"));
        var versionBytes = Bytes.Hex("03000000f7e85ffad7d7dc3bd62ac87057cf6118");
        var prefix = new byte[20];
        for (var i = 0; i < 20; i += 4) Array.Copy(Bytes.Le32(unchecked(Bytes.I32(versionBytes, i, true) ^ ts)), 0, prefix, i, 4);
        return Bytes.Base64(Bytes.Join(prefix, new[] { (byte)r, (byte)(r >> 8), (byte)0, (byte)1 }, encrypted));
    }

    private string MedusaV5(string url, byte[]? body, int ts, Dictionary<string, string> who)
    {
        var query = Bytes.Sm3(System.Text.Encoding.UTF8.GetBytes(url[(url.IndexOf('?') + 1)..]));
        var f13 = HashF13(query, body is null ? new byte[16] : Bytes.Md5(body), ts);
        var flags = Bytes.Hex("4480000000000000");
        var version = who["version_name"];
        var brand = who.TryGetValue("device_brand", out var value) && value.Length > 0 ? value : "HONOR";
        var type = who["device_type"];
        var envLaunch = (int)(_random() * 21) + 100;
        var envPid = (int)(_random() * 2000) + 10001;
        var deviceInfo = Proto(
            F(1, 1, "sint"), F(2, 2, "sint"), F(3, "8662"), F(4, who["device_id"]), F(5, _deviceToken),
            F(6, "!noperm!"), F(7, -888888, "sint"), F(8, -888888, "sint"), F(9, 3, "sint"),
            F(10, -888888, "sint"), F(11, "!notset!"), F(12, "Asia/Shanghai,8"), F(13, "zh_CN"),
            F(14, 4, "sint"), F(16, 0f, "float"), F(17, 0f, "float"), F(18, 0f, "float"),
            F(19, 0f, "float"), F(20, 0f, "float"), F(21, 0f, "float"), F(22, who["os_version"]),
            F(23, 41, "sint"), F(24, 36, "sint"), F(25, _processStarted, "sint"),
            F(26, _processStarted, "sint"), F(27, _processStarted, "sint"), F(28, _clock(), "sint"),
            F(29, -1, "sint"), F(30, type), F(31, brand), F(32, type), F(33, type), F(34, brand),
            F(35, brand), F(36, brand), F(38, 31, "sint"));
        var env = Proto(
            F(1, envLaunch, "sint"), F(2, 247235599, "sint"), F(3, 247235596, "sint"), F(5, 7, "sint"),
            F(6, "v04.10.03.04-bugfix"), F(7, envPid, "sint"), F(8, who["channel"]), F(12, deviceInfo),
            F(13, Proto(F(1, ts, "sint"), F(2, -2, "sint"), F(4, 200, "sint"))), F(14, version));
        var message = Proto(
            F(1, V5Constants.Magic), F(2, 5, "sint"), F(3, Unsigned(Rand()), "sint"), F(4, "8662"),
            F(5, who["device_id"]), F(6, V5Constants.License), F(7, version), F(8, "v04.10.03-ml-android"),
            F(9, 0x040a0300, "sint"), F(10, flags), F(12, ts, "sint"), F(13, f13), F(14, query[..6]),
            F(15, Proto(F(1, 305, "sint"), F(2, 4, "sint"), F(3, 694367, "sint"), F(5, 1621072674, "sint"))),
            F(16, _deviceToken), F(17, ts, "sint"), F(20, "none"), F(21, V5Constants.Field21, "sint"),
            F(23, env),
            F(24, "{\"cmr\":16777216,\"cmr2\":16777216,\"un_h\":11,\"vpn\":0,\"kd\":0,\"dyn\":\"\",\"do\":1,\"tk\":true}"));
        var nonce = Rand();
        var key = V5Constants.SignKey;
        var hash = Bytes.Sm3(Bytes.Join(key, Bytes.Le32(nonce), key));
        var high = Bytes.Ushr(nonce, 16) & 255;
        var seed = Bytes.Le32(unchecked(~(((high << 11) | Bytes.Ushr(nonce, 24)) ^ (high >> 5) ^ high)));
        var transformed = Bytes.Join(flags, Xmxor(message, hash)).Reverse().ToArray();
        for (var i = 0; i < transformed.Length; i++) transformed[i] = (byte)(transformed[i] ^ seed[~i & 3]);
        var check = 0x18000001 | ((query[0] & 63) << 14) | ((f13[0] & 63) << 8);
        var packed = Bytes.Join(new byte[] { 0xca }, Bytes.Le32(Rand()), Bytes.Le32(check), transformed,
            new[] { (byte)Bytes.Ushr(nonce, 16), (byte)Bytes.Ushr(nonce, 24) });
        var encrypted = new AesV3(Bytes.Md5(key[..16]), ts, v5: true).Encrypt(packed, Bytes.Md5(key[16..32]));
        var prefix = Bytes.Join(Bytes.Le32(5), V5Constants.Magic);
        var tsBytes = Bytes.Le32(ts);
        for (var i = 0; i < prefix.Length; i++) prefix[i] = (byte)(prefix[i] ^ tsBytes[i & 3]);
        return Bytes.Base64(Bytes.Join(prefix, new[] { (byte)nonce, (byte)(nonce >> 8), (byte)0, (byte)1 }, encrypted));
    }

    public string Helios(int ts)
    {
        var random = Rand();
        var ascii = Bytes.HexString(Bytes.Md5(Bytes.Join(Bytes.Le32(random), System.Text.Encoding.UTF8.GetBytes("8662"))));
        var words = Enumerable.Range(0, 4).Select(i => Bytes.I64(System.Text.Encoding.UTF8.GetBytes(ascii), i * 8, true)).ToList();
        var table = new List<long> { words[0] };
        var b0 = words[0];
        var b8 = words[1];
        words.RemoveAt(0);
        words.RemoveAt(0);
        for (var i = 0; i < 34; i++)
        {
            var x8 = unchecked(Bytes.Ror64(b8, 8) + b0) ^ i;
            words.Add(x8);
            x8 ^= Bytes.Ror64(b0, 61);
            table.Add(x8);
            b0 = x8;
            b8 = words[0];
            words.RemoveAt(0);
        }
        var license = _mobileV5 ? V5Constants.License : "1588093228";
        var text = System.Text.Encoding.UTF8.GetBytes($"{ts}-{license}-8662");
        var padding = 16 - text.Length % 16;
        var data = Bytes.Join(text, Enumerable.Repeat((byte)padding, padding).ToArray());
        var chunks = new List<byte[]>();
        chunks.Add(Bytes.Le32(random));
        for (var at = 0; at < data.Length; at += 16)
        {
            long a = Bytes.I64(data, at, true);
            long b = Bytes.I64(data, at + 8, true);
            for (var i = 0; i < 34; i++)
            {
                b = unchecked(table[i] ^ (a + Bytes.Ror64(b, 8)));
                a = unchecked(b ^ Bytes.Ror64(a, 61));
            }
            chunks.Add(Bytes.Le64(a));
            chunks.Add(Bytes.Le64(b));
        }
        return Bytes.Base64(chunks.SelectMany(c => c).ToArray());
    }

    public Request Sign(string baseUrl, Dictionary<string, string> values, byte[]? body = null,
        Dictionary<string, string>? identity = null, bool playback = false)
    {
        var now = _clock();
        var ts = unchecked((int)(now / 1000));
        for (var offset = 0; offset < 128; offset++)
        {
            var ticket = now + offset;
            var query = Bytes.EncodeValues(values.Append(new("ts", ts.ToString(CultureInfo.InvariantCulture)))
                .Append(new("_rticket", ticket.ToString(CultureInfo.InvariantCulture))));
            if (Branch(query, body, ts) != 0) continue;
            var url = $"{baseUrl}?{query}";
            var random = (int)(_random() * 65536);
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = playback ? VendorConstants.PlayerUa : body is null ? VendorConstants.AppUa : VendorConstants.VideoUa,
                ["Accept"] = "application/json; charset=utf-8,application/x-protobuf",
                ["x-ss-req-ticket"] = ticket.ToString(CultureInfo.InvariantCulture),
            };
            if (!playback)
            {
                headers["x-tt-request-tag"] = "t=0;n=0";
                headers["passport-sdk-version"] = "50561";
            }
            headers["sdk-version"] = "2";
            headers["x-xs-from-web"] = "0";
            headers["x-khronos"] = ts.ToString(CultureInfo.InvariantCulture);
            headers["x-ladon"] = Bytes.Base64(Bytes.Be32(ts));
            headers["x-argus"] = Bytes.Base64(Bytes.Le32(ts));
            headers["x-gorgon"] = Gorgon(query, body, ts, random);
            headers["x-helios"] = Helios(ts);
            headers["x-medusa"] = Medusa(url, body, ts, identity);
            headers["x-tt-dt"] = "";
            if (playback)
            {
                headers["x-tt-store-region"] = "cn";
                headers["x-tt-store-region-src"] = "did";
            }
            if (body is not null)
            {
                headers["Content-Type"] = "application/json; charset=UTF-8";
                if (!playback)
                {
                    headers["x-vc-bdturing-sdk-version"] = "3.7.2.cn";
                    headers["x-ss-stub"] = Bytes.HexString(Bytes.Md5(body)).ToUpperInvariant();
                }
            }
            return new Request(url, headers, body);
        }
        throw new InvalidOperationException("无法生成请求签名");
    }

    public Request SignPlayback(string baseUrl, Dictionary<string, string> values, byte[]? body = null, long? now = null)
    {
        var clock = now ?? _clock();
        var ts = unchecked((int)(clock / 1000));
        for (var offset = 0; offset < 1024; offset++)
        {
            var ticket = clock + offset;
            var signedValues = new Dictionary<string, string>(values)
            {
                ["_rticket"] = ticket.ToString(CultureInfo.InvariantCulture),
            };
            var query = Bytes.EncodeValues(signedValues);
            if (Branch(query, body, ts) != 0) continue;
            var url = $"{baseUrl}?{query}";
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = VendorConstants.PlayerUa,
                ["Accept"] = "application/json; charset=utf-8,application/x-protobuf",
                ["x-ss-req-ticket"] = ticket.ToString(CultureInfo.InvariantCulture),
                ["sdk-version"] = "2", ["x-xs-from-web"] = "0", ["x-khronos"] = ts.ToString(CultureInfo.InvariantCulture),
                ["x-ladon"] = Bytes.Base64(Bytes.Be32(ts)), ["x-argus"] = Bytes.Base64(Bytes.Le32(ts)),
                ["x-gorgon"] = Gorgon(query, body, ts, (int)(_random() * 65536)),
                ["x-helios"] = Helios(ts),
                ["x-medusa"] = Medusa(url, body, ts, _mobileV5 ? values : null),
            };
            if (_mobileV5)
            {
                var allowed = new HashSet<string> { "x-khronos", "x-helios", "x-medusa" };
                foreach (var key in headers.Keys.Where(k => !allowed.Contains(k)).ToList()) headers.Remove(key);
            }
            if (body is not null)
            {
                headers["Content-Type"] = "application/json; charset=utf-8";
                headers["x-ss-stub"] = Bytes.HexString(Bytes.Md5(body)).ToUpperInvariant();
            }
            return new Request(url, headers, body);
        }
        throw new InvalidOperationException("无法生成播放签名");
    }

    public static string Encode(string value) => Bytes.EncodeQuery(value);
    public static string EncodeValues(IEnumerable<KeyValuePair<string, string>> values) => Bytes.EncodeValues(values);
}

internal static class ByteArrayIntExtensions
{
    public static int ToInt(this byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public static long ToLongLittle(this byte[] bytes) => BitConverter.ToInt64(bytes, 0);
}
