using System.Net;
using System.Net.Http.Headers;

namespace HongguoTV.Core;

internal sealed class HttpTransport : IDisposable
{
    private readonly HttpClient _client;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClientHandler _handler;

    public HttpTransport(TimeSpan? timeout = null)
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _client = new HttpClient(_handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public async Task<(string Text, HttpStatusCode Status, IReadOnlyList<string> SetCookies)> GetTextAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? headers = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, headers);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await ReadLimitedAsync(response, ct);
        return (text, response.StatusCode, response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToList() : new List<string>());
    }

    public async Task<(string Text, HttpStatusCode Status, IReadOnlyList<string> SetCookies)> SendAsync(
        HttpMethod method, string url, IEnumerable<KeyValuePair<string, string>>? headers = null,
        byte[]? body = null, string? contentType = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        AddHeaders(request, headers);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json; charset=utf-8");
        }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await ReadLimitedAsync(response, ct);
        return (text, response.StatusCode, response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToList() : new List<string>());
    }

    public async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct = default)
        => await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    public async Task<byte[]> ReadBytesLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length < limit)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length >= limit) throw new IOException("响应体过大");
        return buffer.ToArray();
    }

    private static void AddHeaders(HttpRequestMessage request, IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        using var result = new StringWriter();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            result.Write(buffer, 0, read);
            if (result.ToString().Length > 4 * 1024 * 1024) throw new IOException("响应体过大");
        }
        return result.ToString();
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
