using System.Text.Json.Nodes;

namespace HongguoTV.Core;

internal static class Json
{
    public static string Str(JsonObject? o, string key)
        => o is null || !o.TryGetPropertyValue(key, out var node) || node is null ? "" : node.ToString().Trim();

    public static int Int(JsonObject? o, string key, int fallback = 0)
        => o is not null && o.TryGetPropertyValue(key, out var node) && node is not null && int.TryParse(node.ToString(), out var n) ? n : fallback;

    public static long Long(JsonObject? o, string key, long fallback = 0)
        => o is not null && o.TryGetPropertyValue(key, out var node) && node is not null && long.TryParse(node.ToString(), out var n) ? n : fallback;

    public static bool Bool(JsonObject? o, string key, bool fallback = false)
        => o is not null && o.TryGetPropertyValue(key, out var node) && node is not null && bool.TryParse(node.ToString(), out var b) ? b : fallback;

    public static JsonObject? Obj(JsonNode? node) => node as JsonObject;
    public static JsonArray? Arr(JsonNode? node) => node as JsonArray;

    public static JsonObject? Obj(JsonObject? o, string key) => o is null ? null : Obj(o[key]);
    public static JsonArray? Arr(JsonObject? o, string key) => o is null ? null : Arr(o[key]);

    public static IEnumerable<JsonObject> Objects(JsonArray? array)
        => array is null ? Enumerable.Empty<JsonObject>() : array.OfType<JsonObject>();

    public static JsonObject? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text.TrimStart('\uFEFF'))?.AsObject(); }
        catch { return null; }
    }

    public static string CleanUrl(string value) => value
        .Replace("\\/", "/")
        .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u0026", "&")
        .Replace("&amp;", "&");
}
