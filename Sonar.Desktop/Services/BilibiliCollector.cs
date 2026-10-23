using System.Net.Http;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sonar.Services;

/// <summary>
/// B站采集器（WBI 签名、搜索、评论、视频详情），移植自 bilibili_collector.py。
/// </summary>
public sealed class BilibiliCollector : IDisposable
{
    public static readonly int[] WbiMixinTable =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        60, 6, 55, 56, 0, 43, 30, 37, 9, 20, 44, 62, 57, 38, 1, 45,
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        60, 6, 55, 56, 0, 43, 30, 37, 9, 20, 44, 62, 57, 38, 1, 45,
    };

    private static readonly string[] DefaultHeaders =
    {
        "Referer", "https://www.bilibili.com",
    };

    private const int WbiCacheSeconds = 12 * 3600;
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(0.6);
    private static readonly int[] RiskControlCodes = { -412, 412, -509, 509 };
    private static readonly int[] PageSizeLadder = { 40, 30, 20 };

    private readonly string _cookie;
    private readonly bool _systemProxyAvailable;
    private readonly object _sync = new();
    private HttpClient _client;
    private bool _proxyActive;
    private DateTime _lastRequest = DateTime.MinValue;
    private (string ImgKey, string SubKey)? _wbiKeys;
    private DateTime _wbiKeysTime = DateTime.MinValue;

    public string RequestRoute { get; private set; }
    public int LastPageSize { get; private set; }

    public BilibiliCollector(string cookie = "", bool? useProxy = null)
    {
        _cookie = cookie ?? "";
        _systemProxyAvailable = useProxy ?? HttpUtils.SystemProxyConfigured;
        _proxyActive = _systemProxyAvailable;
        RequestRoute = _proxyActive ? "系统代理" : "直连";
        _client = HttpUtils.CreateClient(_cookie, Headers(), _proxyActive);
    }

    private static List<KeyValuePair<string, string>> Headers() => new()
    {
        new("Referer", "https://www.bilibili.com"),
        new("Accept", "application/json, text/plain, */*"),
        new("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8"),
        new("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"),
    };

    public void Dispose() => _client.Dispose();

    // ----------------------------------------------------------------
    // 请求基础设施
    // ----------------------------------------------------------------

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_sync)
        {
            var next = _lastRequest + MinRequestInterval;
            wait = next - DateTime.UtcNow;
        }
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
        lock (_sync)
        {
            _lastRequest = DateTime.UtcNow;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? parameters,
        CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);
        var fullUrl = BuildUrl(url, parameters);
        try
        {
            return await _client.GetAsync(fullUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_proxyActive && HttpUtils.IsProxyError(ex))
        {
            // 系统代理失效 → 改用直连重试一次
            _client.Dispose();
            _client = HttpUtils.CreateClient(_cookie, Headers(), useProxy: false);
            RequestRoute = "直连（代理不可用已回落）";
            _proxyActive = false;
            return await _client.GetAsync(fullUrl, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildUrl(string url, IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        if (parameters is null) return url;
        var list = parameters.ToList();
        if (list.Count == 0) return url;
        return url + (url.Contains('?') ? "&" : "?") + HttpUtils.BuildQuery(list);
    }

    private async Task<JsonNode> GetJsonAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? parameters,
        int retries, CancellationToken cancellationToken)
    {
        JsonObject last = new() { ["code"] = -1, ["message"] = "未知错误" };
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            using var response = await GetAsync(url, parameters, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 412 or 509)
            {
                last = new JsonObject
                {
                    ["code"] = (int)response.StatusCode,
                    ["message"] = "HTTP状态码异常（疑似风控拦截）",
                };
            }
            else
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    last = JsonNode.Parse(text) as JsonObject
                           ?? new JsonObject { ["code"] = -1, ["message"] = "响应不是有效JSON" };
                }
                catch
                {
                    last = new JsonObject { ["code"] = -1, ["message"] = "响应不是有效JSON" };
                }
            }

            if (JsonUtil.ToInt(last["code"], -1) == 0) return last!;
            var code = JsonUtil.ToInt(last["code"], -1);
            if (RiskControlCodes.Contains(code) && attempt < retries)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            break;
        }
        return last!;
    }

    // ----------------------------------------------------------------
    // WBI 签名
    // ----------------------------------------------------------------

    private async Task<(string ImgKey, string SubKey)> GetWbiKeysAsync(CancellationToken cancellationToken)
    {
        if (_wbiKeys is { } cached &&
            (DateTime.UtcNow - _wbiKeysTime).TotalSeconds < WbiCacheSeconds)
        {
            return cached;
        }

        var data = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/nav", null, 1, cancellationToken)
            .ConfigureAwait(false);
        var wbiImg = data["data"]?["wbi_img"];
        var imgUrl = wbiImg?["img_url"]?.GetValue<string>();
        var subUrl = wbiImg?["sub_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(imgUrl) || string.IsNullOrEmpty(subUrl))
        {
            throw new InvalidOperationException(
                $"获取WBI密钥失败: code={JsonUtil.ToInt(data["code"], -1)} " +
                $"{JsonUtil.GetString(data["message"], "未知错误")}");
        }

        var imgKey = imgUrl.Split('/')[^1].Split('.')[0];
        var subKey = subUrl.Split('/')[^1].Split('.')[0];
        _wbiKeys = (imgKey, subKey);
        _wbiKeysTime = DateTime.UtcNow;
        return _wbiKeys.Value;
    }

    private void InvalidateWbiKeys()
    {
        _wbiKeys = null;
        _wbiKeysTime = DateTime.MinValue;
    }

    internal static string MixinKey(string rawKey)
    {
        var builder = new StringBuilder(32);
        foreach (var index in WbiMixinTable.Take(32))
        {
            if (index < rawKey.Length) builder.Append(rawKey[index]);
        }
        return builder.ToString();
    }

    private async Task<Dictionary<string, string>> SignWbiAsync(
        Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var (imgKey, subKey) = await GetWbiKeysAsync(cancellationToken).ConfigureAwait(false);
        var mixinKey = MixinKey(imgKey + subKey);
        parameters["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var query = HttpUtils.BuildQuery(parameters.OrderBy(p => p.Key, StringComparer.Ordinal));
        var rid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey)))
            .ToLowerInvariant();
        parameters["w_rid"] = rid;
        return parameters;
    }

    // ----------------------------------------------------------------
    // 搜索
    // ----------------------------------------------------------------

    public async Task<List<Dictionary<string, object?>>> SearchVideosAsync(
        string keyword, int page = 1, string order = "totalrank", int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        const string url = "https://api.bilibili.com/x/web-interface/wbi/search/type";
        var ladder = pageSize is > 0 ? new[] { pageSize.Value } : PageSizeLadder;
        JsonObject? data = null;
        var lastError = "";

        foreach (var size in ladder)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var parameters = await SignWbiAsync(new Dictionary<string, string>
                {
                    ["search_type"] = "video",
                    ["keyword"] = keyword,
                    ["page"] = page.ToString(CultureInfo.InvariantCulture),
                    ["order"] = order,
                    ["page_size"] = size.ToString(CultureInfo.InvariantCulture),
                }, cancellationToken).ConfigureAwait(false);

                var response = await GetJsonAsync(url, parameters, 2, cancellationToken)
                    .ConfigureAwait(false);
                var code = JsonUtil.ToInt(response["code"], -1);
                if (code == 0)
                {
                    data = response as JsonObject;
                    LastPageSize = size;
                    break;
                }
                lastError = $"code={code} {JsonUtil.GetString(response["message"], "")}";
                if (attempt == 0 && RiskControlCodes.Contains(code))
                {
                    InvalidateWbiKeys();
                    continue;
                }
                break;
            }
            if (data is not null) break;
        }

        if (data is null)
        {
            throw new InvalidOperationException($"B站搜索失败: {(lastError.Length > 0 ? lastError : "未知错误")}");
        }

        var results = new List<Dictionary<string, object?>>();
        if (data["data"]?["result"] is not JsonArray array) return results;

        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var title = JsonUtil.GetString(item["title"])
                .Replace("<em class=\"keyword\">", "", StringComparison.Ordinal)
                .Replace("</em>", "", StringComparison.Ordinal);
            var bvid = JsonUtil.GetString(item["bvid"]);
            var pubdate = JsonUtil.ToDouble(item["pubdate"]);
            results.Add(new Dictionary<string, object?>
            {
                ["platform"] = "bilibili",
                ["type"] = "video",
                ["id"] = bvid,
                ["aid"] = JsonUtil.ToLong(item["aid"]),
                ["title"] = title,
                ["description"] = JsonUtil.GetString(item["description"]),
                ["author"] = JsonUtil.GetString(item["author"]),
                ["mid"] = JsonUtil.ToLong(item["mid"]),
                ["play"] = JsonUtil.ToLong(item["play"]),
                ["danmaku"] = JsonUtil.ToLong(item["video_review"]),
                ["favorites"] = JsonUtil.ToLong(item["favorites"]),
                ["like"] = JsonUtil.ToLong(item["like"]),
                ["coin"] = JsonUtil.ToLong(item["coin"]),
                ["share"] = JsonUtil.ToLong(item["share"]),
                ["reply"] = JsonUtil.ToLong(item["review"]),
                ["pubdate"] = pubdate > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)pubdate).LocalDateTime
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "",
                ["tag"] = JsonUtil.GetString(item["tag"]),
                ["cover"] = JsonUtil.GetString(item["pic"]),
                ["url"] = $"https://www.bilibili.com/video/{bvid}",
            });
        }
        return results;
    }

    // ----------------------------------------------------------------
    // 评论
    // ----------------------------------------------------------------

    public async Task<List<Dictionary<string, object?>>> GetVideoCommentsAsync(
        long aid, int page = 1, int ps = 20, int sort = 0,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = aid.ToString(CultureInfo.InvariantCulture),
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = ps.ToString(CultureInfo.InvariantCulture),
            ["sort"] = sort.ToString(CultureInfo.InvariantCulture),
        };
        var data = await GetJsonAsync("https://api.bilibili.com/x/v2/reply", parameters, 2,
            cancellationToken).ConfigureAwait(false);
        var code = JsonUtil.ToInt(data["code"], -1);
        if (code != 0)
        {
            if (RiskControlCodes.Contains(code))
            {
                throw new InvalidOperationException(
                    $"获取评论被风控拦截(code {code})，请降低频率或配置Cookie后重试");
            }
            return new List<Dictionary<string, object?>>();
        }

        var results = new List<Dictionary<string, object?>>();
        if (data["data"]?["replies"] is not JsonArray replies) return results;
        foreach (var node in replies)
        {
            if (node is not JsonObject reply) continue;
            var ctime = JsonUtil.ToDouble(reply["ctime"]);
            results.Add(new Dictionary<string, object?>
            {
                ["platform"] = "bilibili",
                ["type"] = "comment",
                ["id"] = JsonUtil.ToLong(reply["rpid"]),
                ["user"] = JsonUtil.GetString(reply["member"]?["uname"]),
                ["content"] = JsonUtil.GetString(reply["content"]?["message"]),
                ["like"] = JsonUtil.ToLong(reply["like"]),
                ["reply_count"] = JsonUtil.ToLong(reply["rcount"]),
                ["ctime"] = ctime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)ctime).LocalDateTime
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "",
            });
        }
        return results;
    }

    // ----------------------------------------------------------------
    // 视频详情
    // ----------------------------------------------------------------

    public static string? ResolveBvid(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        var match = Regex.Match(text, @"(BV[0-9a-zA-Z]{10})");
        if (match.Success) return match.Groups[1].Value;
        var av = Regex.Match(text, @"av(\d+)", RegexOptions.IgnoreCase);
        return av.Success ? av.Value : null;
    }

    public async Task<Dictionary<string, object?>> GetVideoInfoAsync(
        string bvid, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> parameters;
        if (bvid.StartsWith("BV", StringComparison.Ordinal))
        {
            parameters = new Dictionary<string, string> { ["bvid"] = bvid };
        }
        else
        {
            parameters = new Dictionary<string, string>
            {
                ["aid"] = bvid.Replace("av", "", StringComparison.OrdinalIgnoreCase),
            };
        }

        var data = await GetJsonAsync("https://api.bilibili.com/x/web-interface/view",
            parameters, 2, cancellationToken).ConfigureAwait(false);
        if (JsonUtil.ToInt(data["code"], -1) != 0)
        {
            throw new InvalidOperationException(
                $"获取视频详情失败: {JsonUtil.GetString(data["message"], "未知错误")}");
        }

        var d = data["data"] as JsonObject ?? new JsonObject();
        var stat = d["stat"] as JsonObject ?? new JsonObject();
        var owner = d["owner"] as JsonObject ?? new JsonObject();

        var tags = new List<string>();
        try
        {
            var tagParameters = bvid.StartsWith("BV", StringComparison.Ordinal)
                ? new Dictionary<string, string> { ["bvid"] = bvid }
                : new Dictionary<string, string>
                {
                    ["aid"] = bvid.Replace("av", "", StringComparison.OrdinalIgnoreCase),
                };
            using var response = await GetAsync("https://api.bilibili.com/x/tag/archive/tags",
                tagParameters, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tagData = JsonNode.Parse(text);
            if (JsonUtil.ToInt(tagData?["code"], -1) == 0 && tagData?["data"] is JsonArray tagArray)
            {
                tags.AddRange(tagArray.Select(t => JsonUtil.GetString(t?["tag_name"]))
                    .Where(t => t.Length > 0));
            }
        }
        catch
        {
            // 标签获取失败不影响主流程
        }

        var pubdate = JsonUtil.ToDouble(d["pubdate"]);
        return new Dictionary<string, object?>
        {
            ["platform"] = "bilibili",
            ["type"] = "video_info",
            ["id"] = JsonUtil.GetString(d["bvid"]),
            ["aid"] = JsonUtil.ToLong(d["aid"]),
            ["title"] = JsonUtil.GetString(d["title"]),
            ["description"] = JsonUtil.GetString(d["desc"]),
            ["author"] = JsonUtil.GetString(owner["name"]),
            ["mid"] = JsonUtil.ToLong(owner["mid"]),
            ["play"] = JsonUtil.ToLong(stat["view"]),
            ["danmaku"] = JsonUtil.ToLong(stat["danmaku"]),
            ["like"] = JsonUtil.ToLong(stat["like"]),
            ["coin"] = JsonUtil.ToLong(stat["coin"]),
            ["favorites"] = JsonUtil.ToLong(stat["favorite"]),
            ["share"] = JsonUtil.ToLong(stat["share"]),
            ["reply"] = JsonUtil.ToLong(stat["reply"]),
            ["pubdate"] = pubdate > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)pubdate).LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "",
            ["duration"] = JsonUtil.ToLong(d["duration"]),
            ["tags"] = tags,
            ["url"] = $"https://www.bilibili.com/video/{JsonUtil.GetString(d["bvid"])}",
        };
    }

    public async Task<List<string>> GetHotSearchesAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetJsonAsync("https://api.bilibili.com/x/web-interface/search/square",
            new Dictionary<string, string> { ["limit"] = "20" }, 2, cancellationToken)
            .ConfigureAwait(false);
        if (JsonUtil.ToInt(data["code"], -1) != 0) return new List<string>();
        if (data["data"]?["trending"]?["list"] is not JsonArray list) return new List<string>();
        return list.Select(item => JsonUtil.GetString(item?["keyword"]))
            .Where(s => s.Length > 0).ToList();
    }

    // ----------------------------------------------------------------
    // 传统模式：综合采集
    // ----------------------------------------------------------------

    public async Task<Dictionary<string, object?>> CollectKeywordDataAsync(
        string keyword, int maxPages = 2, int commentsPerVideo = 30,
        CancellationToken cancellationToken = default)
    {
        var allVideos = new List<Dictionary<string, object?>>();
        var allComments = new List<Dictionary<string, object?>>();

        for (var page = 1; page <= maxPages; page++)
        {
            try
            {
                var videos = await SearchVideosAsync(keyword, page, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (videos.Count == 0) break;
                allVideos.AddRange(videos);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }

        var topVideos = allVideos.OrderByDescending(v => Item.Num(v, "play")).Take(5);
        var commentsNeeded = commentsPerVideo;
        foreach (var video in topVideos)
        {
            if (commentsNeeded <= 0) break;
            try
            {
                var comments = await GetVideoCommentsAsync(
                    Item.Long(video, "aid"), 1, Math.Min(commentsNeeded, 20), 0, cancellationToken)
                    .ConfigureAwait(false);
                allComments.AddRange(comments);
                commentsNeeded -= comments.Count;
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 单个视频失败继续
            }
        }

        return new Dictionary<string, object?>
        {
            ["videos"] = allVideos,
            ["comments"] = allComments,
            ["total_videos"] = allVideos.Count,
            ["total_comments"] = allComments.Count,
        };
    }
}
