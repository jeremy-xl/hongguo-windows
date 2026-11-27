using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HongguoTV.Core.Crypto;

namespace HongguoTV.Core;

public sealed class ContentRepository : IDisposable
{
    private const string Site = "https://hongguoduanju.com";
    private const string CurrentAppModelHost = "reading.snssdk.com";
    private static readonly string[] PlaybackHosts =
    {
        "api5-normal-sinfonlineb.fqnovel.com",
        "api5-normal-hl.fqnovel.com",
        "api5-normal-lf.fqnovel.com",
    };

    private readonly HttpTransport _transport = new(TimeSpan.FromSeconds(25));
    private readonly HttpTransport _playbackTransport = new(TimeSpan.FromSeconds(25), useCookies: false);
    private readonly HttpClient _mediaClient = new();
    private readonly Signer _signer = new();
    private readonly Signer _mobileV5Signer = new(mobileV5: true);
    private readonly Signer _playerV5Signer = new(mobileV5: true);
    private readonly PlayerSignature _playerSignature = new();
    private readonly string _suggestionWebId = Guid.NewGuid().ToString();
    private PlaybackProfile? _profile;
    private readonly Dictionary<string, JsonObject> _modelCache = new();
    private readonly object _profileLock = new();

    private sealed record PlaybackProfile(Dictionary<string, string> Values, string Cookie);

    public async Task<CatalogPage> HomeAsync(int page = 1, CancellationToken ct = default)
    {
        if (page is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        var url = $"{Site}/category?tab=1&sort_type=1" + (page > 1 ? $"&page={page}" : "");
        var html = (await _transport.GetTextAsync(url, ct: ct)).Text;
        var data = ParseRouter(html)["loaderData"]?.AsObject();
        var section = Json.Obj(data, "category_page") ?? Json.Obj(data, "category_$")
            ?? throw new ContentException("首页数据结构已变化");
        var rows = Json.Arr(section, "recommendList")
            ?? Json.Obj(section, "categoryData")?["recommendList"]?.AsArray()
            ?? throw new ContentException("首页数据暂不可用");
        var items = Valid(Json.Objects(rows).Select(Card));
        var lastPage = Json.Int(Json.Obj(section, "pagination"), "totalPages", 100);
        return new CatalogPage(items, items.Count > 0 && page < Math.Min(lastPage, 100));
    }

    public async Task<CatalogPage> SearchAsync(string keyword, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 80 || page is < 1 or > 100)
            throw new ArgumentException("搜索词或页码不合法");
        try
        {
            var app = await AppSearchAsync(keyword, page, ct);
            if (app.Items.Count > 0 || !app.HasMore) return app;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return await WebsiteSearchAsync(keyword, page, ct);
    }

    public async Task<IReadOnlyList<string>> SuggestionsAsync(string keyword, CancellationToken ct = default)
    {
        var clean = keyword.Trim();
        if (clean.Length == 0) return Array.Empty<string>();
        var url = $"{Site}/incent_resource/suggestion?app_id=8662&web_id={_suggestionWebId}&query={Signer.Encode(clean)}&count=8";
        var root = Json.Parse((await _transport.GetTextAsync(url, ct: ct)).Text) ?? throw new ContentException("搜索联想暂不可用");
        return Json.Objects(Json.Arr(root, "suggest_list"))
            .Select(item =>
            {
                var name = Json.Str(item, "name");
                if (name.Length == 0) name = Json.Arr(item, "display_words")?.FirstOrDefault()?.ToString().Trim() ?? "";
                return name;
            })
            .Where(v => v.Length is > 0 and <= 80)
            .Distinct().Take(8).ToList();
    }

    public async Task<Detail> DetailAsync(string id, CancellationToken ct = default)
    {
        if (!IsId(id)) throw new ArgumentException("剧集 ID 不合法");
        var data = ParseRouter((await _transport.GetTextAsync($"{Site}/detail?series_id={id}", ct: ct)).Text)["loaderData"]?.AsObject();
        var o = Json.Obj(Json.Obj(data, "detail_page"), "seriesDetail") ?? throw new ContentException("剧集详情暂不可用");
        var webEpisodes = EpisodeDirectory.FromWeb(o);
        var episodes = webEpisodes is not null
            ? webEpisodes
            : await DirectoryEpisodesAsync(id, EpisodeDirectory.ExpectedCount(o), ct);
        return new Detail(Card(o) with { Id = id }, episodes);
    }

    private async Task<IReadOnlyList<string>> DirectoryEpisodesAsync(string id, int expectedCount, CancellationToken ct)
    {
        var hosts = new[] { "api5-normal-sinfonlinea.fqnovel.com", "api5-normal-sinfonlineb.fqnovel.com" };
        foreach (var host in hosts)
        {
            var values = new Dictionary<string, string>(VendorConstants.Device)
            {
                ["book_id"] = id, ["need_version"] = "true",
            };
            var signed = _signer.Sign($"https://{host}/reading/bookapi/directory/all_items/v/", values);
            try
            {
                var reply = await SendSignedAsync(signed, ct);
                return EpisodeDirectory.Parse(reply.Text, expectedCount);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or FormatException) { }
        }
        throw new ContentException("完整分集暂不可用，请稍后重试");
    }

    public async Task<StreamLadder> StreamLadderAsync(string episodeId, CancellationToken ct = default)
    {
        if (!IsId(episodeId)) throw new ArgumentException("分集 ID 不合法");
        var profile = await GetProfileAsync(ct);
        try
        {
            return await MobileLadderAsync(episodeId, profile, CurrentAppModelHost, ct);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or FormatException or KeyNotFoundException)
        {
            try
            {
                var model = await TabVideoModelAsync(episodeId, profile, ct);
                if (model is not null)
                {
                    var rows = PlaybackModels.Renditions(model);
                    if (rows.Count > 0) return new StreamLadder(rows);
                }
            }
            catch (Exception e2) when (e2 is IOException or HttpRequestException or FormatException or KeyNotFoundException) { }
            try
            {
                return await MobileLadderAsync(episodeId, profile, "api5-normal-sinfonlineb.fqnovel.com", ct);
            }
            catch (Exception e3) when (e3 is IOException or HttpRequestException or FormatException or KeyNotFoundException) { }
            return await SingleVideoModelAsync(episodeId, profile, ct);
        }
    }

    public RemoteVideo OpenStream(StreamInfo info) => new(_mediaClient, info);

    private async Task<CatalogPage> AppSearchAsync(string keyword, int page, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(VendorConstants.Device)
        {
            ["version_code"] = "72232", ["manifest_version_code"] = "72232",
            ["update_version_code"] = "72232", ["pv_player"] = "72232", ["version_name"] = "7.2.2.32",
            ["query"] = keyword, ["count"] = "20", ["offset"] = ((page - 1) * 20).ToString(),
            ["tab_type"] = "11", ["bookshelf_search_plan"] = "4", ["use_correct"] = "true", ["user_is_login"] = "0",
        };
        var signed = _signer.Sign("https://api5-normal-sinfonlinea.fqnovel.com/reading/bookapi/search/tab/v", values);
        var root = Json.Parse((await SendSignedAsync(signed, ct)).Text) ?? throw new ContentException("搜索响应格式异常");
        if (Json.Int(root, "code", -1) != 0) throw new ContentException("搜索接口暂不可用");
        var tabs = Json.Arr(root, "search_tabs") ?? Json.Arr(Json.Obj(root, "data"), "search_tabs");
        var tab = Json.Objects(tabs).FirstOrDefault(t => Json.Int(t, "tab_type") == 11)
            ?? throw new ContentException("短剧搜索暂不可用");
        var items = new List<Series>();
        foreach (var cell in Json.Objects(Json.Arr(tab, "data")))
        {
            var cells = new List<JsonObject> { cell };
            if (Json.Parse(Json.Str(cell, "lynx_data"))?["cell_data"]?.AsArray() is { } embedded)
                cells.AddRange(Json.Objects(embedded));
            foreach (var row in cells)
            {
                var video = Json.Objects(Json.Arr(row, "video_data")).FirstOrDefault();
                if (video is null) continue;
                var id = FirstNonEmpty(Json.Str(video, "series_id"), Json.Str(row, "book_id"), Json.Str(row, "search_result_id"));
                var title = FirstNonEmpty(
                    Json.Str(Json.Obj(Json.Obj(row, "search_high_light"), "title"), "text"),
                    Json.Str(video, "title"), Json.Str(row, "cell_name"));
                var count = Json.Int(video, "episode_cnt");
                items.Add(new Series(id, title,
                    Json.CleanUrl(FirstNonEmpty(Json.Str(video, "cover"), Json.Str(video, "cover_url"))),
                    Json.Str(video, "video_desc"),
                    count > 0 ? $"全 {count} 集" : Json.Str(video, "rec_text"), Json.Str(video, "sub_title")));
            }
        }
        return new CatalogPage(Valid(items), Json.Bool(tab, "has_more") && page < 100);
    }

    private async Task<CatalogPage> WebsiteSearchAsync(string keyword, int page, CancellationToken ct)
    {
        var url = $"{Site}/api/search/result?query={Signer.Encode(keyword)}&page={page}";
        var root = Json.Parse((await _transport.GetTextAsync(url, ct: ct)).Text) ?? throw new ContentException("搜索响应格式已变化");
        if (root["pageNum"] is not null && Json.Int(root, "pageNum") != page) throw new ContentException("搜索页码不匹配");
        var rows = Json.Arr(root, "searchList") ?? throw new ContentException("搜索数据结构已变化");
        var items = Valid(Json.Objects(rows).Select(Card)).ToList();
        var more = root["hasMore"] is not null ? Json.Bool(root, "hasMore")
            : Json.Int(root, "totalPages") > 0 ? page < Json.Int(root, "totalPages") : page < 100;
        return new CatalogPage(items, rows.Count > 0 && page < 100 && more);
    }

    private async Task<StreamLadder> MobileLadderAsync(string id, PlaybackProfile profile, string host, CancellationToken ct)
    {
        var values = new Dictionary<string, string>
        {
            ["aid"] = "8662", ["app_name"] = "novelread", ["version_code"] = "74532", ["version_name"] = "7.4.5.32",
            ["update_version_code"] = "74532", ["manifest_version_code"] = "74532", ["os"] = "android", ["ssmix"] = "a",
            ["channel"] = "update_64", ["device_platform"] = "android", ["device_type"] = "25053RT47C",
            ["device_brand"] = "Redmi", ["os_version"] = "16", ["os_api"] = "36", ["ac"] = "wifi", ["language"] = "zh",
            ["host_abi"] = "arm64-v8a",
            ["iid"] = profile.Values["iid"], ["cdid"] = profile.Values["cdid"], ["device_id"] = profile.Values["device_id"],
            ["_rticket"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
        };
        var url = $"https://{host}/novel/player/multi_video_model/preload/v1";
        var payload = $"{{\"biz_param\":{{\"detail_page_version\":0,\"device_level\":4,\"disable_digg_stat\":false,\"disable_video_relate_book\":false,\"need_all_video_definition\":true,\"need_mp4_align\":false,\"use_os_player\":false,\"use_server_dns\":false,\"video_platform\":1024}},\"mixed_video_id_map\":{{\"1004\":[\"{id}\"]}},\"dr_scene\":\"preload\"}}";
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
            gzip.Write(Encoding.UTF8.GetBytes(payload));
        var body = compressed.ToArray();
        var signed = _mobileV5Signer.SignPlayback(url, values, body);
        var headers = new List<KeyValuePair<string, string>>(signed.Headers)
        {
            new("User-Agent", "okhttp/4.12.0"),
            new("Accept", "application/json"),
            new("Content-Encoding", "gzip"),
            new("x-ss-stub", Bytes.HexString(Bytes.Md5(body)).ToUpperInvariant()),
        };
        var reply = await SendSignedAsync(signed, ct, headers, hasBody: true, _playbackTransport);
        if (string.IsNullOrWhiteSpace(reply.Text)) throw new ContentException("播放信息为空（mobile-model=200/0B）");
        var root = Json.Parse(reply.Text) ?? throw new ContentException("播放信息格式异常");
        CheckStatus(root);
        var data = Json.Obj(root, "data") ?? throw new ContentException("播放信息为空");
        var entries = Json.Obj(data, "1004") ?? data;
        var raw = Json.Obj(entries, id)?["video_model"];
        var rawText = raw is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var model = Json.Obj(raw) ?? Json.Parse(rawText)
            ?? throw new ContentException("没有可播放的视频");
        var rows = PlaybackModels.Renditions(model);
        if (rows.Count == 0)
        {
            var fallback = PlaybackModels.Fallback(model);
            if (fallback is not null) return await CompatibleLadderAsync(fallback, ct);
        }
        if (rows.Count == 0) throw new ContentException("没有可播放的视频");
        return new StreamLadder(rows);
    }

    private async Task<JsonObject?> TabVideoModelAsync(string id, PlaybackProfile profile, CancellationToken ct)
    {
        var values = new Dictionary<string, string>
        {
            ["aid"] = "8662", ["app_name"] = "novelread", ["version_code"] = "74532", ["version_name"] = "7.4.5.32",
            ["update_version_code"] = "74532", ["manifest_version_code"] = "74532", ["os"] = "android", ["ssmix"] = "a",
            ["channel"] = "update_64", ["device_platform"] = "android", ["device_type"] = "25053RT47C",
            ["device_brand"] = "Redmi", ["os_version"] = "16", ["os_api"] = "36", ["ac"] = "wifi", ["language"] = "zh",
            ["host_abi"] = "arm64-v8a",
            ["iid"] = profile.Values["iid"], ["cdid"] = profile.Values["cdid"], ["device_id"] = profile.Values["device_id"],
            ["video_id"] = id, ["video_id_type"] = "0", ["device_level"] = "1",
            ["need_all_video_definition"] = "1", ["need_mp4_align"] = "0", ["use_os_player"] = "0",
            ["video_platform"] = "1024", ["source"] = "0", ["from_video_id"] = "",
            ["_rticket"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
        };
        var signed = _playerV5Signer.SignPlayback(
            $"https://{PlaybackHosts[0]}/reading/bookapi/video_tab/multi_video_model/v1/", values, Array.Empty<byte>());
        var headers = new List<KeyValuePair<string, string>>(signed.Headers
            .Where(h => h.Key is not ("x-ss-stub" or "Content-Type" or "Content-Encoding")))
        {
            new("User-Agent", "com.phoenix.read/73932 (Linux; U; Android 16; zh_CN; 25053RT47C; Build/BP2A.250605.031.A3; Cronet/TTNetVersion:04657795 2026-01-23 QuicVersion:c67e9834 2025-09-08)"),
            new("Accept", "application/json; charset=utf-8"),
            new("Accept-Language", "zh-CN,zh;q=0.9"),
        };
        var reply = await SendSignedAsync(signed, ct, headers, transport: _playbackTransport);
        var root = Json.Parse(reply.Text);
        if (Json.Int(root, "Code", Json.Int(root, "code", 0)) != 0) return null;
        var data = Json.Obj(root, "data");
        var rows = Json.Obj(data, "video_model_data") ?? data;
        var row = Json.Obj(rows, id) ?? rows?.OfType<KeyValuePair<string, JsonNode?>>()
            .Select(p => Json.Obj(p.Value)).FirstOrDefault(v => v is not null);
        var raw = row?["video_model"];
        var text = raw is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        return Json.Obj(raw) ?? Json.Parse(text);
    }

    private async Task<StreamLadder> SingleVideoModelAsync(string id, PlaybackProfile profile, CancellationToken ct)
    {
        var allowed = new HashSet<string>
        {
            "ac", "channel", "aid", "app_name", "version_code", "version_name", "device_platform", "os",
            "ssmix", "device_type", "device_brand", "language", "os_api", "os_version",
            "manifest_version_code", "resolution", "dpi", "update_version_code", "device_id", "iid",
        };
        var values = profile.Values.Where(p => allowed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        values["version_code"] = "73532"; values["version_name"] = "7.3.5.32";
        values["manifest_version_code"] = "73532"; values["update_version_code"] = "73532";
        values["_rticket"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var query = Bytes.EncodeValues(values);
        var payload = Encoding.UTF8.GetBytes($"{{\"biz_param\":{{\"need_all_video_definition\":true,\"video_platform\":3}},\"content_type\":1,\"video_id\":\"{id}\"}}");
        var url = $"https://{PlaybackHosts[0]}/novel/player/video_model/v1/?{query}";
        var headers = _playerSignature.Headers(query, payload, profile.Values["device_id"]);
        var all = new List<KeyValuePair<string, string>>(headers)
        {
            new("User-Agent", VendorConstants.VideoUa.Replace("/71332", "/73532")),
            new("Accept", "application/json"), new("Sdk-Version", "2"), new("X-XS-From-Web", "0"),
        };
        var reply = await SendSignedAsync(new Signer.Request(url, headers.ToDictionary(h => h.Key, h => h.Value), payload),
            ct, all, hasBody: true);
        var root = Json.Parse(reply.Text) ?? throw new ContentException("播放信息格式异常");
        CheckStatus(root);
        var data = Json.Obj(root, "data") ?? throw new ContentException("播放信息为空");
        var model = Json.Obj(data, "video_model") ?? Json.Parse(Json.Str(data, "video_model"));
        if (model is null) throw new ContentException("播放信息格式异常");
        var rows = PlaybackModels.Renditions(model);
        if (rows.Count == 0) throw new ContentException("没有可播放的视频");
        return new StreamLadder(rows);
    }

    private async Task<(string Text, HttpStatusCode Status)> SendSignedAsync(Signer.Request request, CancellationToken ct,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null, bool hasBody = false, HttpTransport? transport = null)
    {
        var client = transport ?? _transport;
        var headers = request.Headers.ToList();
        if (extraHeaders is not null)
        {
            foreach (var h in extraHeaders)
            {
                headers.RemoveAll(p => string.Equals(p.Key, h.Key, StringComparison.OrdinalIgnoreCase));
                headers.Add(h);
            }
        }
        var result = hasBody
            ? await client.SendAsync(request.Body is null ? HttpMethod.Get : HttpMethod.Post, request.Url,
                headers, request.Body, ct: ct)
            : await client.GetTextAsync(request.Url, headers, ct);
        if (result.Status == HttpStatusCode.ServiceUnavailable) throw new ContentException("播放服务繁忙，请稍后重试");
        return (result.Text, result.Status);
    }

    private async Task<StreamLadder> CompatibleLadderAsync(string endpoint, CancellationToken ct)
    {
        foreach (var codec in new[] { "1", "3" })
        {
            var separator = endpoint.Contains('?') ? '&' : '?';
            var url = $"{endpoint}{separator}codec_type={codec}";
            var reply = await _playbackTransport.GetTextAsync(url,
                new[] { new KeyValuePair<string, string>("Accept", "application/json") }, ct);
            if (string.IsNullOrWhiteSpace(reply.Text)) continue;
            var root = Json.Parse(reply.Text);
            var data = Json.Obj(Json.Obj(root, "video_info"), "data");
            if (data is null) continue;
            var rows = PlaybackModels.Renditions(data);
            if (rows.Count > 0) return new StreamLadder(rows);
        }
        throw new ContentException("没有可播放的视频");
    }

    private async Task<PlaybackProfile> GetProfileAsync(CancellationToken ct)
    {
        lock (_profileLock)
            if (_profile is not null) return _profile;
        var body = DeviceRegister.Payload(VendorConstants.Device);
        var reply = await _transport.SendAsync(HttpMethod.Post, DeviceRegister.Url(),
            DeviceRegister.Headers(body, VendorConstants.Device), body,
            "application/octet-stream", ct);
        var registered = DeviceRegister.Parse(reply.Text, reply.SetCookies)
            ?? throw new ContentException("播放设备注册失败，请稍后重试");
        var values = new Dictionary<string, string>(VendorConstants.Device)
        {
            ["device_id"] = registered.DeviceId,
            ["iid"] = registered.InstallId,
            ["cdid"] = Guid.NewGuid().ToString(),
        };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "install_id", "ttreq", "store-region", "store-region-src" };
        var cookie = string.Join("; ", registered.Cookies.Where(p => allowed.Contains(p.Key)).Select(p => $"{p.Key}={p.Value}"));
        lock (_profileLock) return _profile ??= new PlaybackProfile(values, cookie);
    }

    private static void CheckStatus(JsonObject root)
    {
        var code = Json.Int(root, "code", Json.Int(root, "Code", 0));
        if (code != 0) throw new ContentException($"播放接口暂不可用（错误码 {code}）");
    }

    private static JsonObject ParseRouter(string html)
    {
        var marker = html.IndexOf("_ROUTER_DATA", StringComparison.Ordinal);
        if (marker < 0) throw new ContentException("页面没有内容数据");
        var first = html.IndexOf('{', marker);
        if (first < 0) throw new ContentException("页面数据格式错误");
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var i = first; i < html.Length; i++)
        {
            var ch = html[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') quoted = false;
                continue;
            }
            if (ch == '"') { quoted = true; continue; }
            if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']')
            {
                depth--;
                if (depth == 0) return Json.Parse(html.Substring(first, i - first + 1)) ?? throw new ContentException("页面数据不完整");
            }
        }
        throw new ContentException("页面数据不完整");
    }

    private static Series Card(JsonObject item)
    {
        var o = Json.Obj(item, "video_data") ?? item;
        var count = Json.Int(o, "episode_cnt", Json.Int(Json.Obj(o, "series_episode_info"), "episode_cnt"));
        var tags = Json.Arr(o, "tags") ?? Json.Arr(o, "category_list");
        var tagText = Json.Objects(tags).Take(5).Select(t =>
        {
            var name = Json.Str(t, "name");
            return name.Length > 0 ? name : t.ToString().Trim('"');
        });
        return new Series(
            Json.Str(o, "series_id"),
            FirstNonEmpty(Json.Str(o, "series_name"), Json.Str(o, "series_title"), Json.Str(item, "name")),
            Json.CleanUrl(Json.Str(o, "series_cover")),
            Json.Str(o, "series_intro"),
            FirstNonEmpty(Json.Str(o, "episode_right_text"), count > 0 ? $"全 {count} 集" : ""),
            string.Join(" · ", tagText));
    }

    private static IReadOnlyList<Series> Valid(IEnumerable<Series> rows) => rows
        .Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.Id, "^[0-9]{1,30}$") && r.Title.Length > 0)
        .GroupBy(r => r.Id).Select(g => g.First()).ToList();

    private static bool IsId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9]{1,30}$");
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    public void Dispose()
    {
        _transport.Dispose();
        _playbackTransport.Dispose();
        _mediaClient.Dispose();
    }
}

internal static class EpisodeDirectory
{
    private static readonly System.Text.RegularExpressions.Regex NumericId =
        new("^[0-9]{1,30}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static int ExpectedCount(JsonObject detail) => Math.Max(0,
        Math.Max(Json.Int(detail, "episode_cnt"), Json.Int(Json.Obj(detail, "series_episode_info"), "episode_cnt")));

    public static IReadOnlyList<string>? FromWeb(JsonObject detail)
    {
        var rows = Json.Arr(detail, "vid_list");
        if (rows is null) return null;
        var ids = rows.Select(v => v?.ToString() ?? "").ToList();
        return Valid(ids, ExpectedCount(detail)) ? ids : null;
    }

    public static IReadOnlyList<string> Parse(string body, int expectedCount)
    {
        var root = Json.Parse(body) ?? throw new ContentException("完整分集响应格式异常");
        var success = root["code"] is not null ? Json.Int(root, "code", -1) == 0
            : root["Code"] is not null ? Json.Int(root, "Code", -1) == 0
            : string.Equals(Json.Str(root, "message"), "SUCCESS", StringComparison.OrdinalIgnoreCase);
        if (!success) throw new ContentException("完整分集接口暂不可用");
        var rows = Json.Arr(Json.Obj(root, "data"), "item_data_list");
        var ids = Json.Objects(rows).Select(row => Json.Str(row, "item_id")).ToList();
        if (!Valid(ids, expectedCount)) throw new ContentException("完整分集数据缺失或异常");
        return ids;
    }

    private static bool Valid(IReadOnlyList<string> ids, int expectedCount) =>
        ids.Count > 0 && ids.Count >= expectedCount && ids.All(v => NumericId.IsMatch(v)) &&
        ids.Distinct().Count() == ids.Count;
}

internal static class PlaybackModels
{
    public static string? Fallback(JsonObject model)
    {
        var candidates = new List<string>();
        var node = model["fallback_api"];
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) candidates.Add(text);
        if (node is JsonObject obj)
        {
            candidates.Add(Json.Str(obj, "url"));
            candidates.Add(Json.Str(obj, "api"));
        }
        if (node is JsonArray array)
            foreach (var item in array)
                if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText)) candidates.Add(itemText);
        foreach (var raw in candidates)
        {
            var url = Json.CleanUrl(raw);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0) return url;
        }
        return null;
    }

    public static IReadOnlyList<Rendition> Renditions(JsonObject model)
    {
        var list = model["video_list"];
        var entries = list is JsonObject obj
            ? obj.Select(pair => (pair.Key, Value: pair.Value as JsonObject)).Where(p => p.Value is not null).ToList()
            : list is JsonArray array ? array.OfType<JsonObject>().Select(v => ("", (JsonObject?)v)).ToList()
            : new List<(string, JsonObject?)>();

        var result = new List<Rendition>();
        foreach (var (name, entry) in entries)
        {
            if (entry is null) continue;
            try
            {
                var codec = string.Join(" ", Json.Str(entry, "codec_type"), Json.Str(entry, "gear_des_key"),
                    Json.Str(Json.Obj(entry, "video_meta"), "codec_type")).ToLowerInvariant();
                if (codec.Contains("bytevc2") || System.Text.RegularExpressions.Regex.IsMatch(codec, @"(^|[^a-z0-9])bvc2([^a-z0-9]|$)")) continue;
                var seedText = Json.Str(model, "key_seed");
                var seed = seedText.Length > 0 ? Bytes.Decode64(seedText) : null;
                var encryption = Json.Obj(entry, "encrypt_info");
                var spade = FirstNonEmpty(Json.Str(encryption, "spade_a"), Json.Str(entry, "spade_a"));
                var key = spade.Length > 0 ? MediaCrypto.DeriveKey(spade) : null;
                if (encryption is not null && key is null && Json.Str(encryption, "encrypt") is not ("false" or "0" or "")) continue;
                string? Decoded(string field)
                {
                    var raw = Json.CleanUrl(Json.Str(entry, field));
                    if (raw.Length == 0) return null;
                    if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == "https") return raw;
                    var decrypted = seed is not null ? MediaCrypto.DecryptUrl(raw, seed) : Encoding.UTF8.GetString(Bytes.Decode64(raw));
                    return Uri.TryCreate(decrypted, UriKind.Absolute, out var d) && d.Scheme == "https" ? decrypted : null;
                }
                var backup = Decoded("backup_url_1");
                var url = Decoded("main_url") ?? Decoded("play_url") ?? backup;
                if (url is null) continue;
                var meta = Json.Obj(entry, "video_meta");
                var width = Json.Int(entry, "vwidth", Json.Int(meta, "vwidth"));
                var height = Json.Int(entry, "vheight", Json.Int(meta, "vheight"));
                var qualityText = FirstNonEmpty(Json.Str(entry, "quality_desc"), Json.Str(meta, "definition"),
                    width > 0 && height > 0 ? Math.Min(width, height).ToString() : "", Json.Str(entry, "height"),
                    Json.Str(entry, "vheight"), Json.Str(entry, "quality"), name);
                result.Add(new Rendition(QualityNumber(qualityText), codec,
                    width > 0 ? width : null, height > 0 ? height : null, url, key, backup == url ? null : backup));
            }
            catch { }
        }
        return result;
    }

    private static int QualityNumber(string value)
    {
        foreach (var marker in new[] { 2160, 1440, 1080, 720, 576, 540, 480, 360 })
            if (value.Contains(marker.ToString())) return marker;
        return new Dictionary<string, int> { ["1920"] = 1080, ["1280"] = 720, ["1024"] = 576, ["854"] = 480, ["640"] = 360 }
            .TryGetValue(value, out var mapped) ? mapped : int.TryParse(value, out var n) ? n : 0;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
