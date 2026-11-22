using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HongguoTV.Core.Crypto;

internal static class DeviceRegister
{
    private const string Host = "https://log5-applog-lf.fqnovel.com";
    private const string Path = "/service/2/device_register/";
    private const string Query = "?tt_data=a&cronet_version=8d40f833_2026-03-03&ttnet_version=4.2.243.31-douyin&use_store_region_cookie=1";
    private static readonly byte[] Magic = { 0x74, 0x63, 0x05, 0x10, 0x00, 0x00 };
    private static readonly byte[] KdfSalt = Bytes.Hex(
        "4dd4c2e6b83162090e52b3c7a6733ba41cb2462b829ab58a196b39db57177524f49baf7f08e8d68d26a72e37c1a95a2f1f05a51892aef2949732b62a38aadd58");

    public sealed record Registered(string DeviceId, string InstallId, string DeviceToken, bool NewUser,
        Dictionary<string, string> Cookies);

    public static byte[] TtEncrypt(byte[] data)
    {
        var rnd = RandomNumberGenerator.GetBytes(32);
        var derived = Bytes.Sha512(Bytes.Join(Bytes.Sha512(rnd), KdfSalt));
        var body = Bytes.Join(Bytes.Sha512(data), data);
        var padding = 16 - body.Length % 16;
        var padded = Bytes.Join(body, Enumerable.Repeat((byte)padding, padding).ToArray());
        var encrypted = Bytes.AesCbcEncrypt(derived[..16], derived[16..32], padded);
        return Bytes.Join(Magic, rnd, encrypted);
    }

    public static byte[] Payload(Dictionary<string, string> device, long? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Value(string key, string fallback) => device.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        var header = new JsonObject
        {
            ["display_name"] = "红果免费短剧",
            ["update_version_code"] = 73932,
            ["manifest_version_code"] = 73932,
            ["app_version_minor"] = "7.3.9.32",
            ["aid"] = 8662,
            ["channel"] = Value("channel", "69310258a"),
            ["package"] = "com.phoenix.read",
            ["app_version"] = "7.3.9.32",
            ["version_code"] = 73932,
            ["sdk_version"] = "3.7.0-rc.34-fanqie-xiaoshuo",
            ["sdk_target_version"] = 29,
            ["git_hash"] = "3a88f4e",
            ["os"] = "Android",
            ["os_version"] = Value("os_version", "12"),
            ["os_api"] = int.TryParse(Value("os_api", "32"), out var api) ? api : 32,
            ["device_model"] = Value("device_type", "AOSP on Intel Platform"),
            ["device_brand"] = Value("device_brand", "SAMSUNG"),
            ["device_manufacturer"] = Value("device_brand", "Intel"),
            ["cpu_abi"] = Value("host_abi", "arm64-v8a"),
            ["release_build"] = "b5e187f",
            ["density_dpi"] = int.TryParse(Value("dpi", "360"), out var dpi) ? dpi : 360,
            ["display_density"] = "mdpi",
            ["resolution"] = Value("resolution", "1440x2560").Replace('*', 'x'),
            ["language"] = "zh",
            ["timezone"] = 8,
            ["access"] = "wifi",
            ["not_request_sender"] = 0,
            ["carrier"] = "CHINA MOBILE",
            ["mcc_mnc"] = "46000",
            ["rom"] = "eng.root.20231116.160340",
            ["rom_version"] = Value("rom_version", "W528JS release-keys"),
            ["cdid"] = Value("cdid", Guid.NewGuid().ToString()),
            ["sig_hash"] = "56a962410c494bbaf0b58dba20cae56f",
            ["openudid"] = Value("openudid", Guid.NewGuid().ToString("N")[..16]),
            ["clientudid"] = Guid.NewGuid().ToString(),
            ["ipv6_list"] = new JsonArray(),
            ["region"] = "CN",
            ["tz_name"] = "Asia/Shanghai",
            ["tz_offset"] = 28800,
            ["sim_region"] = "cn",
            ["sim_serial_number"] = new JsonArray(),
            ["oaid_may_support"] = false,
            ["req_id"] = Guid.NewGuid().ToString(),
            ["device_platform"] = "android",
            ["custom"] = new JsonObject
            {
                ["account_region_src"] = "fallback", ["host_bit"] = 64, ["account_region"] = "cn",
                ["dragon_device_type"] = Value("dragon_device_type", "pad"),
            },
            ["pre_installed_channel"] = "",
            ["apk_first_install_time"] = clock,
            ["is_system_app"] = 0,
            ["sdk_flavor"] = "china",
            ["guest_mode"] = 0,
        };
        var root = new JsonObject { ["magic_tag"] = "ss_app_log", ["header"] = header, ["_gen_time"] = clock };
        return TtEncrypt(Gzip(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(data);
        return output.ToArray();
    }

    public static Dictionary<string, string> Headers(byte[] body, Dictionary<string, string> device, long? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Dictionary<string, string>
        {
            ["User-Agent"] = device.TryGetValue("user_agent", out var ua) && !string.IsNullOrWhiteSpace(ua) ? ua : VendorConstants.AppUa,
            ["Content-Type"] = "application/octet-stream;tt-data=a",
            ["log-encode-type"] = "gzip",
            ["x-ss-dp"] = "8662",
            ["x-ss-req-ticket"] = clock.ToString(),
            ["x-vc-bdturing-sdk-version"] = "4.0.3.cn",
            ["lc"] = "101",
            ["sdk-version"] = "2",
            ["passport-sdk-version"] = "5051452",
            ["x-neptune"] = "-8|50:51:59:30:40:47:49:39",
            ["x-ss-stub"] = Bytes.HexString(Bytes.Md5(body)).ToUpperInvariant(),
        };
    }

    public static string Url() => Host + Path + Query;

    public static Registered? Parse(string body, IReadOnlyList<string> setCookies)
    {
        JsonObject json;
        try { json = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException(); }
        catch { return null; }
        var device = Json.Str(json, "device_id_str");
        if (string.IsNullOrWhiteSpace(device)) device = Json.Str(json, "device_id");
        var install = Json.Str(json, "install_id_str");
        if (string.IsNullOrWhiteSpace(install)) install = Json.Str(json, "install_id");
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(install) || device == "0" || install == "0") return null;
        var cookies = new Dictionary<string, string>();
        foreach (var header in setCookies)
        {
            var pair = header.Split(';')[0].Trim();
            if (!pair.Contains('=')) continue;
            var index = pair.IndexOf('=');
            var name = pair[..index].Trim();
            var value = pair[(index + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0) cookies[name] = value;
        }
        return new Registered(device, install, Json.Str(json, "device_token"), Json.Int(json, "new_user") == 1, cookies);
    }
}
