using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using HongguoTV.Core.Crypto;

namespace HongguoTV.Core;

/// <summary>Provides decrypted bytes from a remote MP4 without a local HTTP service.</summary>
public sealed class RemoteVideo : IDisposable
{
    private const int Chunk = 512 * 1024;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _prepareLock = new(1, 1);
    private StreamInfo _info;
    private MediaCrypto.Prepared? _prepared;
    private long _total;
    private bool _disposed;

    public RemoteVideo(HttpClient http, StreamInfo info)
    {
        _http = http;
        _info = info;
    }

    public StreamInfo Info => _info;
    public long Total => _total;

    public async Task PrepareAsync(CancellationToken ct = default)
    {
        if (_prepared is not null) return;
        await _prepareLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_prepared is not null) return;
            var first = await ReadRangeAsync(_info.Url, 0, Chunk - 1, ct).ConfigureAwait(false);
            var total = first.Total;
            var headerEnd = MediaCrypto.HeaderEnd(first.Bytes, total);
            var headerBytes = first.Bytes;
            if (headerEnd > headerBytes.Length)
                headerBytes = (await ReadRangeAsync(_info.Url, 0, headerEnd - 1, ct).ConfigureAwait(false)).Bytes;
            _prepared = MediaCrypto.Prepare(headerBytes[..headerEnd], _info.Key);
            _total = total;
        }
        finally { _prepareLock.Release(); }
    }

    public async Task<byte[]> ReadAsync(long start, long length, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteVideo));
        if (_prepared is null) await PrepareAsync(ct).ConfigureAwait(false);
        var end = Math.Min(start + length - 1, _total - 1);
        if (end < start) return Array.Empty<byte>();
        var raw = await ReadRangeWithFallbackAsync(start, end, ct).ConfigureAwait(false);
        return MediaCrypto.Decrypt(raw.Bytes, start, _info.Key, _prepared!);
    }

    /// <summary>
    /// 边下边解密的流式读取：首块就绪即可返回，调用方可以边写响应边发送，
    /// 避免"整段下载完才响应"造成的播放器缓冲耗尽。
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReadChunksAsync(long start, long length,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteVideo));
        if (_prepared is null) await PrepareAsync(ct).ConfigureAwait(false);
        var end = Math.Min(start + length, _total);
        if (end <= start) yield break;

        HttpResponseMessage? response = null;
        Stream? stream = null;
        try
        {
            (response, stream) = await OpenRangeAsync(start, end - 1, ct).ConfigureAwait(false);
            var buffer = new byte[Chunk];
            var at = start;
            while (at < end)
            {
                var want = (int)Math.Min(buffer.Length, end - at);
                var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) break;
                var plain = MediaCrypto.Decrypt(buffer.AsSpan(0, read).ToArray(), at, _info.Key, _prepared!);
                at += read;
                yield return plain;
            }
        }
        finally
        {
            stream?.Dispose();
            response?.Dispose();
        }
    }

    private async Task<(HttpResponseMessage Response, Stream Stream)> OpenRangeAsync(long start, long end, CancellationToken ct)
    {
        try { return await SendRangeAsync(_info.Url, start, end, ct).ConfigureAwait(false); }
        catch when (!string.IsNullOrWhiteSpace(_info.BackupUrl) && _info.BackupUrl != _info.Url)
        {
            _info = _info with { Url = _info.BackupUrl!, BackupUrl = _info.Url };
            return await SendRangeAsync(_info.Url, start, end, ct).ConfigureAwait(false);
        }
    }

    private async Task<(HttpResponseMessage Response, Stream Stream)> SendRangeAsync(string url, long start, long end, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.TryAddWithoutValidation("User-Agent", VendorConstants.VideoUa);
        request.Headers.TryAddWithoutValidation("Referer", "https://novel.snssdk.com/");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new IOException($"视频服务器拒绝分段读取（HTTP {code}）");
        }
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (response, stream);
    }

    private async Task<(byte[] Bytes, long Total)> ReadRangeWithFallbackAsync(long start, long end, CancellationToken ct)
    {
        try { return await ReadRangeAsync(_info.Url, start, end, ct); }
        catch when (!string.IsNullOrWhiteSpace(_info.BackupUrl) && _info.BackupUrl != _info.Url)
        {
            _info = _info with { Url = _info.BackupUrl!, BackupUrl = _info.Url };
            return await ReadRangeAsync(_info.Url, start, end, ct);
        }
    }

    private async Task<(byte[] Bytes, long Total)> ReadRangeAsync(string url, long start, long end, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.TryAddWithoutValidation("User-Agent", VendorConstants.VideoUa);
        request.Headers.TryAddWithoutValidation("Referer", "https://novel.snssdk.com/");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"视频服务器拒绝分段读取（HTTP {(int)response.StatusCode}）");
        var bytes = await ReadExactlyAsync(await response.Content.ReadAsStreamAsync(ct), (int)(end - start + 1), ct);
        long total = response.Content.Headers.ContentRange?.Length ?? start + bytes.Length;
        return (bytes, total);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var result = new byte[count];
        var at = 0;
        while (at < count)
        {
            var read = await stream.ReadAsync(result.AsMemory(at, count - at), ct);
            if (read == 0) throw new IOException("视频数据不完整");
            at += read;
        }
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        _prepareLock.Dispose();
    }
}
