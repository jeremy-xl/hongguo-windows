using System.Numerics;
using System.Text;
using HongguoTV.Core.Crypto;

namespace HongguoTV.Core;

internal static class MediaCrypto
{
    public sealed record Sample(long Start, long End, byte[] Iv, long EncryptedOffset = 0);
    public sealed record Prepared(byte[] Header, IReadOnlyList<Sample> Samples);
    public sealed record Box(int Start, int End, int Body, string Type);

    private const string KdfConstants =
        "4dd4c2e6b83162090e52b3c7a6733ba41cb2462b829ab58a196b39db57177524f49baf7f08e8d68d26a72e37c1a95a2f1f05a51892aef2949732b62a38aadd58";

    public static byte[] DeriveKey(string value)
    {
        var raw = Bytes.Decode64(value);
        if (raw.Length < 3) throw new IOException("内容密钥格式异常");
        var length = Math.Min(raw.Length - (raw[0] ^ raw[1] ^ raw[2]) + 47, raw.Length - 1);
        if (length < 33) throw new IOException("内容密钥格式异常");
        var work = raw.Skip(1).Take(length).ToArray();
        var a = 85;
        var b = 246;
        for (var i = 0; i < work.Length; i++)
        {
            var old = work[i];
            var previous = (i & 1) != 0 ? a : b;
            if ((i & 1) != 0) a = old; else b = old;
            work[i] = unchecked((byte)(-21 - BitOperations.PopCount((uint)i) + (previous ^ old)));
        }
        var key = Bytes.Hex(Encoding.ASCII.GetString(work.Skip(1).Take(32).ToArray()));
        if (key.Length != 16) throw new IOException("内容密钥格式异常");
        return key;
    }

    public static string DecryptUrl(string value, byte[] seed)
    {
        var raw = Bytes.Decode64(value);
        if (raw.Length < 20 || raw[0] != 0xa8 || raw[2] != 1 || raw[3] != 0) throw new IOException("视频地址格式异常");
        var h = Bytes.Sha512(Bytes.Join(Bytes.Sha512(seed), Bytes.Hex(KdfConstants)));
        var bytes = Bytes.AesCbcDecrypt(h[..16], h[16..32], raw.Skip(4).Take((raw.Length - 4) / 16 * 16).ToArray());
        var end = bytes.Length;
        var padding = bytes[^1];
        if (padding is >= 1 and <= 16 && padding <= end) end -= padding;
        while (end > 0 && bytes[end - 1] == 0) end--;
        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    public static Box? GetBox(byte[] data, int at, int? limit = null)
    {
        var max = limit ?? data.Length;
        if (at < 0 || at + 8 > max) return null;
        var size = Bytes.U32(data, at);
        var header = 8;
        if (size == 1)
        {
            if (at + 16 > max) return null;
            size = Bytes.I64(data, at + 8);
            header = 16;
        }
        if (size == 0) size = max - at;
        if (size < header || size > max - at) return null;
        var type = Encoding.ASCII.GetString(data, at + 4, 4);
        return new Box(at, at + (int)size, at + header, type);
    }

    public static Box? Find(byte[] data, string type, int start, int limit)
    {
        for (var at = start; at <= limit - 8; at++)
            if (data[at + 4] == type[0] && GetBox(data, at, limit) is { } box && box.Type == type) return box;
        return null;
    }

    public static int HeaderEnd(byte[] first, long total)
    {
        if (first.Length < 16 || Encoding.ASCII.GetString(first, 4, 4) != "ftyp") throw new IOException("不支持的视频容器");
        var ftyp = Bytes.U32(first, 0);
        if (ftyp < 8 || ftyp + 8 > first.Length || Encoding.ASCII.GetString(first, (int)ftyp + 4, 4) != "moov")
            throw new IOException("视频索引不在文件开头");
        var size = Bytes.U32(first, (int)ftyp);
        var end = ftyp + size;
        if (size < 8 || end > total || end > 8 * 1024 * 1024) throw new IOException("视频索引大小异常");
        return (int)end;
    }

    public static Prepared Prepare(byte[] data, byte[]? key)
    {
        if (key is null) return new Prepared(data.ToArray(), Array.Empty<Sample>());
        var ftyp = GetBox(data, 0) ?? throw new IOException("视频容器格式异常");
        var moov = GetBox(data, (int)Bytes.U32(data, 0)) ?? throw new IOException("视频索引不完整");
        if (moov.Type != "moov") throw new IOException("视频索引不完整");
        var samples = new List<Sample>();
        var header = (byte[])data.Clone();
        var cursor = moov.Body;
        while (cursor < moov.End)
        {
            var track = GetBox(data, cursor, moov.End) ?? throw new IOException("视频轨道格式异常");
            cursor = track.End;
            if (track.Type != "trak") continue;
            var stbl = Find(data, "stbl", track.Body, track.End);
            if (stbl is null) continue;
            var saiz = Find(data, "saiz", stbl.Body, stbl.End);
            if (saiz is null) continue;
            var saio = Find(data, "saio", stbl.Body, stbl.End) ?? throw new IOException("视频加密索引缺失");
            var stsz = Find(data, "stsz", stbl.Body, stbl.End) ?? throw new IOException("样本索引缺失");
            var stsc = Find(data, "stsc", stbl.Body, stbl.End) ?? throw new IOException("块索引缺失");
            var chunks = Find(data, "stco", stbl.Body, stbl.End) ?? Find(data, "co64", stbl.Body, stbl.End)
                ?? throw new IOException("块偏移缺失");

            int Count(int at, int limit)
            {
                if (at + 4 > limit) throw new IOException("索引越界");
                var n = Bytes.U32(data, at);
                if (n > 200000) throw new IOException("索引数量过大");
                return (int)n;
            }

            var n = Count(stsz.Body + 8, stsz.End);
            var defaultSize = Bytes.U32(data, stsz.Body + 4);
            if (defaultSize == 0 && stsz.Body + 12 + n * 4L > stsz.End) throw new IOException("样本索引截断");
            var sizes = Enumerable.Range(0, n).Select(i => defaultSize > 0 ? defaultSize : Bytes.U32(data, stsz.Body + 12 + i * 4)).ToArray();
            var chunkCount = Count(chunks.Body + 4, chunks.End);
            var width = chunks.Type == "co64" ? 8 : 4;
            if (chunks.Body + 8 + chunkCount * (long)width > chunks.End) throw new IOException("块偏移截断");
            var offsets = Enumerable.Range(0, chunkCount)
                .Select(i => width == 8 ? Bytes.I64(data, chunks.Body + 8 + i * width) : Bytes.U32(data, chunks.Body + 8 + i * width)).ToArray();
            var entryCount = Count(stsc.Body + 4, stsc.End);
            if (stsc.Body + 8 + entryCount * 12L > stsc.End || entryCount == 0) throw new IOException("块索引截断");
            var entries = Enumerable.Range(0, entryCount)
                .Select(i => (First: Bytes.U32(data, stsc.Body + 8 + i * 12), Count: Bytes.U32(data, stsc.Body + 12 + i * 12))).ToArray();

            var sizeBase = saiz.Body + 4 + ((Bytes.I32(data, saiz.Body) & 1) != 0 ? 8 : 0);
            if (sizeBase + 5 > saiz.End) throw new IOException("辅助索引截断");
            var auxDefault = data[sizeBase];
            var auxCount = Count(sizeBase + 1, saiz.End);
            if (auxCount != n || (auxDefault == 0 && sizeBase + 5 + n > saiz.End)) throw new IOException("辅助索引数量不一致");
            var auxSizes = Enumerable.Range(0, n).Select(i => auxDefault > 0 ? auxDefault : data[sizeBase + 5 + i]).ToArray();
            var offsetBase = saio.Body + 4 + ((Bytes.I32(data, saio.Body) & 1) != 0 ? 8 : 0);
            var offsetWidth = data[saio.Body] == 1 ? 8 : 4;
            if (Count(offsetBase, saio.End) != 1 || offsetBase + 4 + offsetWidth > saio.End) throw new IOException("不支持的辅助索引布局");
            long aux = offsetWidth == 8 ? Bytes.I64(data, offsetBase + 4) : Bytes.U32(data, offsetBase + 4);
            var auxEnd = aux + auxSizes.Sum(v => (long)v);
            if (aux < 0 || auxEnd > data.Length) throw new IOException("辅助数据超出索引范围");

            var sampleIndex = 0;
            var entry = 0;
            for (var chunk = 0; chunk < offsets.Length; chunk++)
            {
                while (entry + 1 < entries.Length && entries[entry + 1].First <= chunk + 1) entry++;
                var countInChunk = entries[entry].Count;
                if (countInChunk > n - sampleIndex || entries[entry].First > chunk + 1) throw new IOException("样本与块数量不一致");
                var position = offsets[chunk];
                for (var i = 0; i < countInChunk; i++)
                {
                    var length = sizes[sampleIndex];
                    var auxSize = auxSizes[sampleIndex];
                    if (position < 0 || length <= 0 || position > long.MaxValue - length || auxSize < 8) throw new IOException("样本索引异常");
                    var iv = Bytes.Join(data.Skip((int)aux).Take(8).ToArray(), new byte[8]);
                    if (auxSize == 8) samples.Add(new Sample(position, position + length, iv));
                    else
                    {
                        if (auxSize < 10) throw new IOException("不支持的加密样本格式");
                        var subs = (data[(int)aux + 8] << 8) | data[(int)aux + 9];
                        if (auxSize != 10 + subs * 6) throw new IOException("加密子样本索引异常");
                        var offset = position;
                        long encrypted = 0;
                        for (var sub = 0; sub < subs; sub++)
                        {
                            var at = (int)aux + 10 + sub * 6;
                            var clear = (data[at] << 8) | data[at + 1];
                            var size = Bytes.U32(data, at + 2);
                            offset += clear;
                            if (offset + size > position + length) throw new IOException("加密子样本越界");
                            if (size > 0) samples.Add(new Sample(offset, offset + size, iv, encrypted));
                            encrypted += size;
                            offset += size;
                        }
                        if (offset != position + length) throw new IOException("加密子样本长度异常");
                    }
                    position += length;
                    aux += auxSize;
                    sampleIndex++;
                }
            }
            if (sampleIndex != n) throw new IOException("视频样本不完整");

            var atSin = stbl.Body;
            while (atSin + 8 <= stbl.End)
            {
                var sinf = Find(data, "sinf", atSin, stbl.End);
                if (sinf is null) break;
                var frma = Find(data, "frma", sinf.Body, sinf.End) ?? throw new IOException("原始编码格式缺失");
                if (frma.Body + 4 > frma.End) throw new IOException("原始编码格式无效");
                var original = data.Skip(frma.Body).Take(4).ToArray();
                var entryAt = sinf.Start - 1;
                while (entryAt >= stbl.Body)
                {
                    var type = Encoding.ASCII.GetString(data, entryAt, 4);
                    if (type is "encv" or "enca") { original.CopyTo(header, entryAt); break; }
                    entryAt--;
                }
                Encoding.ASCII.GetBytes("free").CopyTo(header, sinf.Start + 4);
                Array.Clear(header, sinf.Body, sinf.End - sinf.Body);
                atSin = sinf.End;
            }
        }
        if (samples.Count == 0) throw new IOException("视频加密索引不可用");
        var sorted = samples.OrderBy(s => s.Start).ToArray();
        for (var i = 0; i + 1 < sorted.Length; i++)
            if (sorted[i].End > sorted[i + 1].Start) throw new IOException("视频样本重叠");
        return new Prepared(header, sorted);
    }

    public static byte[] Decrypt(byte[] data, long start, byte[]? key, Prepared prepared)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        var result = (byte[])data.Clone();
        var end = start + data.Length;
        if (start < prepared.Header.Length)
        {
            var from = (int)start;
            var to = (int)Math.Min(end, prepared.Header.Length);
            Array.Copy(prepared.Header, from, result, 0, to - from);
        }
        if (key is null) return result;
        var low = 0;
        var high = prepared.Samples.Count;
        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (prepared.Samples[middle].End <= start) low = middle + 1; else high = middle;
        }
        for (var i = low; i < prepared.Samples.Count; i++)
        {
            var sample = prepared.Samples[i];
            if (sample.Start >= end) break;
            var begin = Math.Max(start, sample.Start);
            var stop = Math.Min(end, sample.End);
            if (begin >= stop) continue;
            var relative = begin - sample.Start + sample.EncryptedOffset;
            var skip = (int)(relative % 16);
            var counter = (new BigInteger(sample.Iv, isUnsigned: true, isBigEndian: true) + relative / 16)
                & ((BigInteger.One << 128) - 1);
            var counterBytes = counter.ToByteArray(isUnsigned: true, isBigEndian: true);
            var iv = new byte[16];
            Array.Copy(counterBytes, 0, iv, 16 - counterBytes.Length, counterBytes.Length);
            var slice = result.Skip((int)(begin - start)).Take((int)(stop - begin)).ToArray();
            var decrypted = AesCtr(key, iv, Bytes.Join(new byte[skip], slice));
            Array.Copy(decrypted, skip, result, (int)(begin - start), slice.Length);
        }
        return result;
    }

    private static byte[] AesCtr(byte[] key, byte[] iv, byte[] data)
    {
        var output = new byte[data.Length];
        var counter = (byte[])iv.Clone();
        for (var at = 0; at < data.Length; at += 16)
        {
            var stream = Bytes.AesEcbEncryptBlock(key, counter);
            var count = Math.Min(16, data.Length - at);
            for (var i = 0; i < count; i++) output[at + i] = (byte)(data[at + i] ^ stream[i]);
            for (var i = 15; i >= 0; i--)
            {
                if (++counter[i] != 0) break;
            }
        }
        return output;
    }
}
