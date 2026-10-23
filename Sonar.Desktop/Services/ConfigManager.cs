using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sonar.Models;

namespace Sonar.Services;

/// <summary>
/// 配置持久化（~/.sonar/config.json），字段与 Python 版完全兼容，
/// 包括 cookie 容错清洗、旧配置迁移、时间窗口旧字段兼容。
/// </summary>
public sealed class ConfigManager
{
    private const string DefaultJson = """
    {
      "bilibili": { "cookies": { "SESSDATA": "", "bili_jct": "", "DedeUserID": "" } },
      "tieba": { "cookies": { "BDUSS": "", "STOKEN": "" } },
      "llm": {
        "api_base": "https://api.deepseek.com/v1",
        "api_key": "",
        "model": "deepseek-chat"
      },
      "collect": {
        "bilibili_max_pages": 2,
        "bilibili_comments_per_video": 30,
        "tieba_max_pages": 3,
        "tieba_replies_per_post": 20,
        "tieba_request_interval": 3.0,
        "tieba_interval_jitter": 2.0,
        "tieba_block_cooldown": 60.0,
        "tieba_search_sort": 2,
        "tieba_hot_first": true,
        "tieba_hot_topics": 2,
        "time_window": { "years": 0, "months": 0, "days": 90, "hours": 0 },
        "deep_bilibili": 4,
        "deep_tieba": 4,
        "max_variants": 3
      }
    }
    """;

    private readonly object _sync = new();
    private JsonObject _root;

    public ConfigManager()
    {
        _root = new JsonObject();
        Load();
    }

    public JsonObject Root
    {
        get
        {
            lock (_sync) return _root;
        }
    }

    public void Load()
    {
        MigrateLegacyConfig();
        JsonObject root;
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var text = File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8);
                root = JsonNode.Parse(text) as JsonObject ?? DefaultRoot();
            }
            else
            {
                root = DefaultRoot();
            }
        }
        catch
        {
            root = DefaultRoot();
        }

        lock (_sync)
        {
            _root = root;
            SanitizeCookies();
        }
    }

    private static JsonObject DefaultRoot() =>
        (JsonNode.Parse(DefaultJson) as JsonObject)!;

    private static void MigrateLegacyConfig()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile) || !File.Exists(AppPaths.LegacyConfigFile))
                return;
            AppPaths.EnsureDataDir();
            File.Copy(AppPaths.LegacyConfigFile, AppPaths.ConfigFile, overwrite: false);
        }
        catch
        {
            // 迁移失败不阻断启动
        }
    }

    private void SanitizeCookies()
    {
        foreach (var section in new[] { "bilibili", "tieba" })
        {
            if (_root[section]?["cookies"] is not JsonObject cookies) continue;
            foreach (var key in cookies.Select(kv => kv.Key).ToList())
            {
                var raw = cookies[key]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(raw))
                {
                    cookies[key] = CleanCookieValue(raw, key);
                }
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            AppPaths.EnsureDataDir();
            var text = _root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(AppPaths.ConfigFile, text, new UTF8Encoding(false));
        }
    }

    public string GetString(string section, string key, string fallback = "")
    {
        lock (_sync) return JsonUtil.GetString(_root, fallback, section, key);
    }

    public int GetInt(string section, string key, int fallback = 0)
    {
        lock (_sync) return JsonUtil.GetInt(_root, fallback, section, key);
    }

    public double GetDouble(string section, string key, double fallback = 0)
    {
        lock (_sync) return JsonUtil.GetDouble(_root, fallback, section, key);
    }

    public bool GetBool(string section, string key, bool fallback = false)
    {
        lock (_sync) return JsonUtil.GetBool(_root, fallback, section, key);
    }

    public void Set(string section, string key, JsonNode? value)
    {
        lock (_sync)
        {
            if (_root[section] is not JsonObject obj)
            {
                obj = new JsonObject();
                _root[section] = obj;
            }
            obj[key] = value;
        }
    }

    public void Set(string section, string key, string value) =>
        Set(section, key, JsonValue.Create(value));

    public void Set(string section, string key, int value) =>
        Set(section, key, JsonValue.Create(value));

    public void Set(string section, string key, double value) =>
        Set(section, key, JsonValue.Create(double.IsFinite(value) ? value : 0));

    public void Set(string section, string key, bool value) =>
        Set(section, key, JsonValue.Create(value));

    public string GetCookie(string section, string name)
    {
        lock (_sync) return JsonUtil.GetString(_root, "", section, "cookies", name);
    }

    public void SetCookie(string section, string name, string value)
    {
        lock (_sync)
        {
            if (_root[section]?["cookies"] is not JsonObject cookies)
            {
                cookies = new JsonObject();
                if (_root[section] is not JsonObject sectionObj)
                {
                    sectionObj = new JsonObject();
                    _root[section] = sectionObj;
                }
                sectionObj["cookies"] = cookies;
            }
            cookies[name] = CleanCookieValue(value, name);
        }
    }

    /// <summary>清洗用户粘贴的整段 Cookie；与 Python 版 _clean_cookie_value 等价。</summary>
    public static string CleanCookieValue(string? raw, string name)
    {
        if (raw is null) return "";
        var text = raw.Trim().Trim('"').Trim('\'').Trim();
        if (text.Length == 0) return "";
        if (text.Contains('=') || text.Contains(';'))
        {
            foreach (var partRaw in text.Split(';'))
            {
                var part = partRaw.Trim();
                if (part.Length == 0 || !part.Contains('=')) continue;
                var index = part.IndexOf('=');
                var key = part[..index].Trim();
                var value = part[(index + 1)..].Trim();
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value.Trim('"').Trim('\'').Trim();
                }
            }
            if (!text.Contains(';'))
            {
                var index = text.IndexOf('=');
                return text[(index + 1)..].Trim('"').Trim('\'').Trim();
            }
            return "";
        }
        return text;
    }

    public string BilibiliCookieString =>
        BuildCookieString("bilibili", new[] { "SESSDATA", "bili_jct", "DedeUserID" });

    public string TiebaCookieString =>
        BuildCookieString("tieba", new[] { "BDUSS", "STOKEN" });

    private string BuildCookieString(string section, params string[] names)
    {
        var parts = new List<string>();
        foreach (var name in names)
        {
            var value = CleanCookieValue(GetCookie(section, name), name);
            if (!string.IsNullOrEmpty(value)) parts.Add($"{name}={value}");
        }
        return string.Join("; ", parts);
    }

    public bool IsBilibiliConfigured => !string.IsNullOrWhiteSpace(GetCookie("bilibili", "SESSDATA"));

    public bool IsTiebaConfigured => !string.IsNullOrWhiteSpace(GetCookie("tieba", "BDUSS"));

    public bool IsLlmConfigured => !string.IsNullOrWhiteSpace(GetString("llm", "api_key"));

    public (double Interval, double Jitter, double Cooldown) TiebaPacing()
    {
        return (
            GetDouble("collect", "tieba_request_interval", 3.0),
            GetDouble("collect", "tieba_interval_jitter", 2.0),
            GetDouble("collect", "tieba_block_cooldown", 60.0));
    }

    public int TiebaSearchSort()
    {
        var value = GetInt("collect", "tieba_search_sort", 2);
        return value is 0 or 1 or 2 ? value : 2;
    }

    public bool TiebaHotFirst()
    {
        lock (_sync)
        {
            var node = JsonUtil.Node(_root, "collect", "tieba_hot_first");
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                return text.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off" or "");
            }
            return JsonUtil.GetBool(node, true);
        }
    }

    public int TiebaHotTopics()
    {
        var count = GetInt("collect", "tieba_hot_topics", 2);
        return Math.Clamp(count, 0, 3);
    }

    public WindowSpec TimeWindow()
    {
        lock (_sync)
        {
            var node = JsonUtil.Node(_root, "collect", "time_window")
                       ?? JsonUtil.Node(_root, "collect", "time_window_days");
            return node is null ? WindowSpec.Default : WindowSpec.Parse(node);
        }
    }

    public int TimeWindowHours() => Relevance.WindowHours(TimeWindow());

    public (int Bilibili, int Tieba) DeepCounts()
    {
        int Read(string key, int fallback) =>
            Math.Clamp(GetInt("collect", key, fallback), 0, 10);
        return (Read("deep_bilibili", 4), Read("deep_tieba", 4));
    }

    public int MaxVariants() =>
        Math.Clamp(GetInt("collect", "max_variants", 3), 1, Relevance.MaxVariants);
}
