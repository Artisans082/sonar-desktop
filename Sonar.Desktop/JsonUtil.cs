using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonar;

/// <summary>JsonNode 的容错读取工具：历史配置里数字可能是字符串、布尔可能是 "0"/"false"。</summary>
public static class JsonUtil
{
    public static JsonNode? Node(JsonNode? root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(key, out var next))
            {
                return null;
            }
            current = next;
        }
        return current;
    }

    public static string GetString(JsonNode? root, string fallback = "", params string[] path)
    {
        var node = Node(root, path);
        if (node is null) return fallback;
        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return node.ToJsonString().Trim('"');
        }
    }

    public static int GetInt(JsonNode? root, int fallback, params string[] path)
    {
        var node = Node(root, path);
        return ToInt(node, fallback);
    }

    public static double GetDouble(JsonNode? root, double fallback, params string[] path)
    {
        var node = Node(root, path);
        return ToDouble(node, fallback);
    }

    public static bool GetBool(JsonNode? root, bool fallback, params string[] path)
    {
        var node = Node(root, path);
        if (node is null) return fallback;
        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            var text = (node.ToJsonString().Trim('"') ?? "").Trim().ToLowerInvariant();
            if (text is "1" or "true" or "yes" or "on") return true;
            if (text is "0" or "false" or "no" or "off" or "") return false;
            return fallback;
        }
    }

    public static int ToInt(JsonNode? node, int fallback = 0)
    {
        if (node is null) return fallback;
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
                if (value.TryGetValue<double>(out var d)) return (int)Math.Round(d);
                if (value.TryGetValue<string>(out var s))
                {
                    if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                    if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
                        return (int)Math.Round(parsedDouble);
                }
            }
            var raw = node.ToJsonString().Trim('"');
            return int.TryParse(raw, out var result) ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static long ToLong(JsonNode? node, long fallback = 0)
    {
        if (node is null) return fallback;
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<double>(out var d)) return (long)d;
                if (value.TryGetValue<string>(out var s) &&
                    long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            var raw = node.ToJsonString().Trim('"');
            return long.TryParse(raw, out var result) ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static double ToDouble(JsonNode? node, double fallback = 0)
    {
        if (node is null) return fallback;
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<double>(out var d)) return d;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<string>(out var s) &&
                    double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            var raw = node.ToJsonString().Trim('"');
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>把 JsonNode 转成可写的 Dictionary/List 结构，供工具结果序列化使用。</summary>
    public static object? ToPlain(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, value) in obj)
                {
                    dict[key] = ToPlain(value);
                }
                return dict;
            case JsonArray array:
                var list = new List<object?>(array.Count);
                foreach (var item in array)
                {
                    list.Add(ToPlain(item));
                }
                return list;
            case JsonValue value:
                if (value.TryGetValue<bool>(out var b)) return b;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<double>(out var d)) return d;
                if (value.TryGetValue<string>(out var s)) return s;
                return value.ToJsonString();
            default:
                return node.ToJsonString();
        }
    }

    public static string Serialize(object? value, bool indented = false)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
