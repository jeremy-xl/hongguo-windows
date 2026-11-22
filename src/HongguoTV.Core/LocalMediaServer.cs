using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace HongguoTV.Core;

/// <summary>
/// Loopback-only Range proxy. VLC cannot accept the app's custom encrypted DataSource,
/// so the desktop build exposes decrypted bytes as a local MP4 resource.
/// </summary>
public sealed class LocalMediaServer : IDisposable
{
    // 单次 Range 响应的最大字节数：块越小，播放器等待下一块的往返次数越多，容易卡顿。
    private const int MaxBlock = 4 * 1024 * 1024;

    private readonly HttpListener _listener = new();
    private readonly ContentRepository _repository;
    private readonly Dictionary<string, RemoteVideo> _slots = new();
    private readonly object _slotsLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _prefix;
    private Task? _loop;

    public LocalMediaServer(ContentRepository repository)
    {
        _repository = repository;
        var port = ReservePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
    }

    public string BaseUrl => _prefix;

    public string Mount(StreamInfo info, string? key = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var video = _repository.OpenStream(info);
        lock (_slotsLock) _slots[id] = video;
        return $"{_prefix}media/{id}/stream.mp4";
    }

    public void Unmount(string url)
    {
        var id = ExtractId(url);
        if (id is null) return;
        lock (_slotsLock)
        {
            if (!_slots.Remove(id, out var video)) return;
            video.Dispose();
        }
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Cache-Control"] = "no-store";
        try
        {
            var id = ExtractId(context.Request.Url?.AbsolutePath ?? "");
            if (id is null || !context.Request.Url!.AbsolutePath.EndsWith("/stream.mp4"))
            {
                response.StatusCode = 404;
                return;
            }
            RemoteVideo? video;
            lock (_slotsLock) _slots.TryGetValue(id, out video);
            if (video is null) { response.StatusCode = 404; return; }

            var rangeHeader = context.Request.Headers["Range"];
            long start;
            long? end = null;
            if (!string.IsNullOrWhiteSpace(rangeHeader) && TryParseRange(rangeHeader, out var parsedStart, out var parsedEnd))
            {
                start = parsedStart;
                end = parsedEnd;
                response.StatusCode = 206;
            }
            else start = 0;

            if (video.Total <= 0) await video.PrepareAsync(_shutdown.Token);
            var length = (end ?? Math.Min(start + MaxBlock - 1, video.Total - 1)) - start + 1;
            if (start >= video.Total || length <= 0)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{video.Total}";
                return;
            }
            response.ContentType = "video/mp4";
            response.ContentLength64 = length;
            if (response.StatusCode == 206)
                response.Headers["Content-Range"] = $"bytes {start}-{start + length - 1}/{video.Total}";
            // 边下边发：不再等整段数据到齐才响应，播放器的缓冲不会被"块下载时间"掏空。
            long written = 0;
            await foreach (var chunk in video.ReadChunksAsync(start, length, _shutdown.Token))
            {
                await response.OutputStream.WriteAsync(chunk, _shutdown.Token);
                await response.OutputStream.FlushAsync(_shutdown.Token);
                written += chunk.Length;
            }
            if (written < length) throw new IOException("上游视频数据中断");
        }
        catch (Exception ex)
        {
            try
            {
                response.StatusCode = 502;
                var message = Encoding.UTF8.GetBytes(ex.Message);
                response.ContentLength64 = message.Length;
                await response.OutputStream.WriteAsync(message);
            }
            catch { }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private static string? ExtractId(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "media" ? parts[1] : null;
    }

    private static bool TryParseRange(string header, out long start, out long? end)
    {
        start = 0;
        end = null;
        var spec = header.Replace("bytes=", "").Trim();
        var pieces = spec.Split('-', 2);
        if (pieces.Length != 2) return false;
        if (pieces[0].Length == 0)
        {
            if (!long.TryParse(pieces[1], out var suffix)) return false;
            start = 0;
            end = suffix > 0 ? suffix - 1 : 0;
            return true;
        }
        if (!long.TryParse(pieces[0], out start)) return false;
        if (pieces[1].Length > 0 && long.TryParse(pieces[1], out var last)) end = last;
        return true;
    }

    private static int ReservePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        lock (_slotsLock)
        {
            foreach (var video in _slots.Values) video.Dispose();
            _slots.Clear();
        }
        _shutdown.Dispose();
    }
}
