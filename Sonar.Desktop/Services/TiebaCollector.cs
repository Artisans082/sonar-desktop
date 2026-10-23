using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sonar.Services;

public sealed class TiebaBlockedException : Exception
{
    public TiebaBlockedException(string message) : base(message) { }
}

/// <summary>吧名解析结果：主吧 + 相关吧族。</summary>
public sealed class ResolvedForums
{
    public string Word { get; set; } = "";
    public string WordNorm { get; set; } = "";
    public Dictionary<string, object?>? Primary { get; set; }
    public List<Dictionary<string, object?>> Candidates { get; set; } = new();
    public HashSet<string> AcceptedNames { get; set; } = new(StringComparer.Ordinal);
    public string MatchedBy { get; set; } = "无结果";
}

/// <summary>
/// 贴吧采集器：移动端搜索 / 吧名搜索 / App protobuf / 热议榜 / 楼层。
/// 完整移植 tieba_collector.py，并保留「风控按 IP 计时」的节奏控制。
/// </summary>
public sealed class TiebaCollector : IDisposable
{
    public const string MobileUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) " +
        "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    public const string AppUa = "aiotieba/1.6.0";

    public const string PcUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public const string AppBaseHost = "tiebac.baidu.com";
    public const string AppProtoBoundary = "-*_r1999";
    public const string AppClientVersion = "12.64.1.1";
    public const string AppClientVersionNew = "22.10.1.0";
    public const string AppClientId = "wappc_1750000000000_000";
    public const string AppCuid = "0123456789ABCDEF0123456789ABCDEF|VERSION=1";
    public const string PcSalt = "36770b1f34c9bbf2e7d1a99d2b82fa9e";

    public const string MoSearchUrl = "https://tieba.baidu.com/mo/q/search/thread";
    public const string MoForumSearchUrl = "https://tieba.baidu.com/mo/q/search/forum";
    public const string HotTopicListUrl = "https://jump.bdimg.com/hottopic/browse/topicList";
    public const string HotTopicPageUrl = "https://tieba.baidu.com/hottopic/browse/hottopic";

    public const int ForumSearchPageSize = 10;
    public const int HotTopicListSize = 30;
    public const int DefaultHotTopicLimit = 2;
    public const int MaxHotTopicLimit = 3;
    public const int MaxForumCandidates = 6;
    public const int SearchSortRelevance = 0;
    public const int SearchSortTime = 1;
    public const int SearchSortHot = 2;
    public const int DefaultSearchSort = SearchSortHot;
    public const int BlockRetries = 1;
    public const int DefaultPageSize = 40;

    public static readonly (int Value, string Label)[] SearchSortChoices =
    {
        (SearchSortHot, "按热度（推荐，取高互动且相关的帖子）"),
        (SearchSortRelevance, "按相关度（字面匹配，可能返回老帖）"),
        (SearchSortTime, "按时间（最新，但关键词命中率较低）"),
    };

    private static readonly string[] SecurityMarkers =
    {
        "安全验证", "百度安全验证", "请完成安全验证", "网络不给力",
    };

    private static readonly Regex StripEmoticonRegex = new(@"#?\(?image_emoticon\d*\)?", RegexOptions.Compiled);
    private static readonly Regex SpaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex NormSpaceRegex = new(@"[\s\u3000]+", RegexOptions.Compiled);

    private readonly CancellationToken _cancellationToken;
    private readonly HttpClient _directClient;
    private HttpClient? _proxyClient;
    private readonly object _sync = new();
    private DateTime _lastRequest = DateTime.MinValue;
    private DateTime _blockedUntil = DateTime.MinValue;

    public string Cookie { get; }
    public double RequestInterval { get; }
    public double IntervalJitter { get; }
    public double BlockCooldown { get; }
    public int SearchSort { get; }
    public string RequestRoute { get; private set; } = "直连";
    public string LastSource { get; private set; } = "";
    public int LastPageSize { get; private set; }
    public string LastSearchSort { get; private set; } = "";

    public TiebaCollector(
        string cookie = "",
        double? requestInterval = null,
        double? intervalJitter = null,
        double? blockCooldown = null,
        int? searchSort = null,
        CancellationToken cancellationToken = default)
    {
        Cookie = cookie ?? "";
        _cancellationToken = cancellationToken;
        RequestInterval = ClampInterval(requestInterval ?? 3.0);
        IntervalJitter = ClampJitter(intervalJitter ?? 2.0);
        BlockCooldown = ClampCooldown(blockCooldown ?? 60.0);
        SearchSort = ClampSearchSort(searchSort ?? DefaultSearchSort);
        _directClient = HttpUtils.CreateClient(Cookie, null, useProxy: false, TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        _directClient.Dispose();
        _proxyClient?.Dispose();
    }

    // ----------------------------------------------------------------
    // 常量钳制
    // ----------------------------------------------------------------

    public static double ClampInterval(double value) =>
        Math.Min(60.0, Math.Max(1.0, double.IsFinite(value) ? value : 3.0));

    public static double ClampJitter(double value) =>
        Math.Min(10.0, Math.Max(0.0, double.IsFinite(value) ? value : 2.0));

    public static double ClampCooldown(double value) =>
        Math.Min(600.0, Math.Max(10.0, double.IsFinite(value) ? value : 60.0));

    public static int ClampSearchSort(int value) => value is 0 or 1 or 2 ? value : DefaultSearchSort;

    public static string FmtSeconds(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0001
            ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);

    public string PacingSummary() => IntervalJitter > 0
        ? $"间隔 {FmtSeconds(RequestInterval)}~{FmtSeconds(RequestInterval + IntervalJitter)} 秒"
        : $"间隔 {FmtSeconds(RequestInterval)} 秒（无抖动）";

    public double BlockedSecondsLeft()
    {
        lock (_sync)
        {
            return Math.Max(0, (_blockedUntil - DateTime.UtcNow).TotalSeconds);
        }
    }

    // ----------------------------------------------------------------
    // 请求基础设施
    // ----------------------------------------------------------------

    private async Task SleepAsync(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            if (_cancellationToken.IsCancellationRequested) return;
            var remaining = deadline - DateTime.UtcNow;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(500)
                ? remaining
                : TimeSpan.FromMilliseconds(500), _cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ThrottleAsync()
    {
        TimeSpan wait;
        lock (_sync)
        {
            var jittered = RequestInterval + Random.Shared.NextDouble() * IntervalJitter;
            var next = _lastRequest + TimeSpan.FromSeconds(jittered);
            wait = next - DateTime.UtcNow;
        }
        if (wait > TimeSpan.Zero)
        {
            await SleepAsync(wait).ConfigureAwait(false);
        }
        lock (_sync)
        {
            _lastRequest = DateTime.UtcNow;
        }
    }

    private Dictionary<string, string> DefaultHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = MobileUa,
        ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8",
        ["Referer"] = "https://tieba.baidu.com/",
        ["Upgrade-Insecure-Requests"] = "1",
    };

    private async Task<HttpResponseMessage> DoRequestAsync(
        HttpMethod method, string url, HttpContent? content,
        IDictionary<string, string>? headers)
    {
        var hasProxy = HttpUtils.SystemProxyConfigured;
        var attempts = hasProxy
            ? new[] { false, true }
            : new[] { false };
        var labels = hasProxy ? new[] { "直连", "系统代理" } : new[] { "直连" };
        Exception? lastException = null;

        for (var i = 0; i < attempts.Length; i++)
        {
            var useProxy = attempts[i];
            var client = useProxy ? GetProxyClient() : _directClient;
            using var request = new HttpRequestMessage(method, url);
            if (content is not null)
            {
                request.Content = content;
            }

            var merged = DefaultHeaders();
            if (headers is not null)
            {
                foreach (var (key, value) in headers) merged[key] = value;
            }
            foreach (var (key, value) in merged)
            {
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(key, value);
            }

            try
            {
                var response = await client.SendAsync(request, _cancellationToken).ConfigureAwait(false);
                RequestRoute = labels[i];
                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (lastException is not null)
        {
            if (HttpUtils.IsProxyError(lastException))
            {
                var proxy = HttpUtils.ProxyDescription;
                throw new InvalidOperationException(
                    $"贴吧请求失败：代理 {(proxy.Length > 0 ? proxy : "系统代理")} 不可用。\n" +
                    $"原始错误：{lastException.Message}\n" +
                    "处理办法：启动代理软件，或关闭系统代理设置后重试。");
            }
            throw new InvalidOperationException($"贴吧请求失败（网络层）：{lastException.Message}");
        }
        throw new InvalidOperationException("贴吧请求失败：没有任何可用的网络走线");
    }

    private HttpClient GetProxyClient()
    {
        if (_proxyClient is not null) return _proxyClient;
        _proxyClient = HttpUtils.CreateClient(Cookie, null, useProxy: true, TimeSpan.FromSeconds(20));
        return _proxyClient;
    }

    private async Task<T> WithCooldownAsync<T>(Func<Task<T>> fetch, string what)
    {
        for (var attempt = 0; attempt <= BlockRetries; attempt++)
        {
            try
            {
                return await fetch().ConfigureAwait(false);
            }
            catch (TiebaBlockedException ex)
            {
                if (attempt >= BlockRetries || _cancellationToken.IsCancellationRequested)
                {
                    lock (_sync)
                    {
                        _blockedUntil = DateTime.UtcNow.AddSeconds(BlockCooldown);
                    }
                    throw new TiebaBlockedException(
                        $"{what}被贴吧风控拦截，重试后仍未通过。\n" +
                        $"已进入 {FmtSeconds(BlockCooldown)} 秒冷却（可在设置页调整" +
                        "「风控冷却」，或调大「请求间隔」降低被拦概率）。\n" +
                        "也可稍等几分钟再试，或改用「B站相关视频概览」等不依赖贴吧的模式。\n" +
                        $"原始信息：{ex.Message}");
                }

                await SleepAsync(TimeSpan.FromSeconds(BlockCooldown)).ConfigureAwait(false);
                if (_cancellationToken.IsCancellationRequested) throw;
                lock (_sync)
                {
                    _lastRequest = DateTime.MinValue;
                }
            }
        }
        throw new TiebaBlockedException($"{what}失败：未知原因");
    }

    private static string BuildUrl(string url, IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        if (parameters is null) return url;
        var list = parameters.ToList();
        if (list.Count == 0) return url;
        return url + "?" + HttpUtils.BuildQuery(list);
    }

    private async Task<string> GetHtmlAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? parameters,
        IDictionary<string, string>? headers)
    {
        return await WithCooldownAsync(async () =>
        {
            await ThrottleAsync().ConfigureAwait(false);
            using var response = await DoRequestAsync(HttpMethod.Get, BuildUrl(url, parameters),
                null, headers).ConfigureAwait(false);
            var text = await HttpUtils.DecodeTextAsync(response).ConfigureAwait(false);
            RaiseIfBlocked(response, text);
            return text;
        }, "获取页面").ConfigureAwait(false);
    }

    private async Task<JsonNode> GetJsonAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? parameters,
        IDictionary<string, string>? headers, string what)
    {
        return await WithCooldownAsync(async () =>
        {
            await ThrottleAsync().ConfigureAwait(false);
            using var response = await DoRequestAsync(HttpMethod.Get, BuildUrl(url, parameters),
                null, headers).ConfigureAwait(false);
            var text = await HttpUtils.DecodeTextAsync(response).ConfigureAwait(false);
            RaiseIfBlocked(response, text);
            try
            {
                return JsonNode.Parse(text)
                       ?? throw new TiebaBlockedException("贴吧返回了空数据。");
            }
            catch (TiebaBlockedException)
            {
                throw;
            }
            catch
            {
                throw new TiebaBlockedException(
                    $"贴吧返回了非 JSON 内容（HTTP {(int)response.StatusCode}），可能触发了风控。");
            }
        }, what).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, List<Pb.Field>>> PostProtoAsync(string url, byte[] payload)
    {
        return await WithCooldownAsync(async () =>
        {
            await ThrottleAsync().ConfigureAwait(false);
            var body = ProtoBody(payload);
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type",
                $"multipart/form-data; boundary={AppProtoBoundary}");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = AppUa,
                ["x_bd_data_type"] = "protobuf",
            };

            using var response = await DoRequestAsync(HttpMethod.Post, url, content, headers)
                .ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || bytes.Length == 0)
            {
                var text = HttpUtils.DecodeBytes(bytes, null);
                RaiseIfBlocked(response, text);
                throw new TiebaBlockedException(
                    $"贴吧 App 接口返回异常（HTTP {(int)response.StatusCode}，{bytes.Length} 字节）。");
            }
            return Pb.Decode(bytes);
        }, "调用贴吧接口").ConfigureAwait(false);
    }

    // ----------------------------------------------------------------
    // 静态解析工具
    // ----------------------------------------------------------------

    private static T? TryGet<T>(JsonNode? node, params string[] path) where T : JsonNode
    {
        var current = JsonUtil.Node(node, path);
        return current as T;
    }

    public static string ForumName(JsonNode? item)
    {
        if (item is null) return "";
        var name = JsonUtil.GetString(item["fname"]);
        if (name.Length == 0) name = JsonUtil.GetString(item["forum_name"]);
        if (name.Length > 0) return name.Trim();
        var info = item["forum_info"];
        return info is JsonObject ? JsonUtil.GetString(info["forum_name"]).Trim() : "";
    }

    public static string NormName(string? text)
    {
        var value = NormSpaceRegex.Replace(text ?? "", "").ToLowerInvariant();
        if (value.Length > 1 && value.EndsWith("吧", StringComparison.Ordinal))
        {
            value = value[..^1];
        }
        return value;
    }

    private static Dictionary<string, object?>? ForumItem(JsonNode? raw, bool exact)
    {
        if (raw is not JsonObject obj) return null;
        var name = JsonUtil.GetString(obj["forum_name"]);
        if (name.Length == 0) name = JsonUtil.GetString(obj["forum_name_show"]);
        name = name.Trim();
        if (name.Length == 0) return null;

        return new Dictionary<string, object?>
        {
            ["platform"] = "tieba",
            ["type"] = "forum",
            ["id"] = JsonUtil.GetString(obj["forum_id"]),
            ["name"] = name,
            ["post_num"] = JsonUtil.ToLong(obj["post_num_ori"]),
            ["concern_num"] = JsonUtil.ToLong(obj["concern_num_ori"]),
            ["slogan"] = JsonUtil.GetString(obj["slogan"]),
            ["exact"] = exact,
        };
    }

    public static (Dictionary<string, object?>? Primary, List<Dictionary<string, object?>> Chosen, string MatchedBy)
        ForumFamily(IReadOnlyList<Dictionary<string, object?>> candidates, string? word)
    {
        var value = (word ?? "").Trim();
        if (candidates.Count == 0 || value.Length == 0)
            return (null, new List<Dictionary<string, object?>>(), "无结果");

        var wordNorm = NormName(value);
        var exact = candidates.Where(c => c.TryGetValue("exact", out var e) && e is true).ToList();
        Dictionary<string, object?>? primary;
        string matchedBy;
        if (exact.Count > 0)
        {
            primary = exact[0];
            matchedBy = "精确匹配";
        }
        else
        {
            var family = candidates
                .Where(c => wordNorm.Length > 0 &&
                            NormName(Item.Str(c, "name")).Contains(wordNorm, StringComparison.Ordinal))
                .ToList();
            if (family.Count > 0)
            {
                primary = family.OrderByDescending(c => Item.Long(c, "post_num")).First();
                matchedBy = "名称匹配";
            }
            else
            {
                primary = candidates[0];
                matchedBy = "首个模糊结果";
            }
        }

        var accepted = candidates
            .Where(c => wordNorm.Length > 0 &&
                        NormName(Item.Str(c, "name")).Contains(wordNorm, StringComparison.Ordinal))
            .Select(c => NormName(Item.Str(c, "name"))).ToHashSet(StringComparer.Ordinal);
        if (primary is not null) accepted.Add(NormName(Item.Str(primary, "name")));

        var chosen = new List<Dictionary<string, object?>>();
        if (primary is not null) chosen.Add(primary);
        foreach (var candidate in candidates)
        {
            var norm = NormName(Item.Str(candidate, "name"));
            if (chosen.Any(x => NormName(Item.Str(x, "name")) == norm)) continue;
            if (accepted.Contains(norm)) chosen.Add(candidate);
        }
        return (primary, chosen, matchedBy);
    }

    public static ResolvedForums BuildResolved(
        IReadOnlyList<Dictionary<string, object?>> candidates, string? word,
        int maxCandidates = MaxForumCandidates)
    {
        var value = (word ?? "").Trim();
        var wordNorm = NormName(value);
        var (primary, chosen, matchedBy) = ForumFamily(candidates, value);
        var accepted = candidates
            .Where(c => wordNorm.Length > 0 &&
                        NormName(Item.Str(c, "name")).Contains(wordNorm, StringComparison.Ordinal))
            .Select(c => NormName(Item.Str(c, "name"))).ToHashSet(StringComparer.Ordinal);
        if (primary is not null) accepted.Add(NormName(Item.Str(primary, "name")));

        return new ResolvedForums
        {
            Word = value,
            WordNorm = wordNorm,
            Primary = primary,
            Candidates = chosen.Take(maxCandidates).ToList(),
            AcceptedNames = accepted,
            MatchedBy = matchedBy,
        };
    }

    public static bool BarMatches(string? barName, ResolvedForums? resolved)
    {
        if (resolved?.Primary is null) return true;
        var name = NormName(barName);
        if (name.Length == 0) return false;
        if (resolved.AcceptedNames.Contains(name)) return true;
        if (resolved.WordNorm.Length > 0 && name.Contains(resolved.WordNorm, StringComparison.Ordinal))
            return true;
        var primary = NormName(Item.Str(resolved.Primary, "name"));
        return primary.Length > 0 &&
               (name == primary || name.Contains(primary, StringComparison.Ordinal) ||
                primary.Contains(name, StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------
    // 热议榜
    // ----------------------------------------------------------------

    public static List<Dictionary<string, object?>> ParseHotTopics(JsonNode? payload, int limit = HotTopicListSize)
    {
        var result = new List<Dictionary<string, object?>>();
        if (payload is not JsonObject obj) return result;
        var bang = obj["bang_topic"];
        if (bang?["topic_list"] is not JsonArray rawList) return result;

        foreach (var node in rawList.Take(Math.Max(0, limit)))
        {
            if (node is not JsonObject raw) continue;
            var topicId = JsonUtil.GetString(raw["topic_id"]).Trim();
            var name = JsonUtil.GetString(raw["topic_name"]).Trim();
            if (topicId.Length == 0 || name.Length == 0) continue;

            var rank = JsonUtil.ToInt(raw["idx_num"], result.Count + 1);
            var discuss = JsonUtil.ToLong(raw["discuss_num"]);
            var url = WebUtility.HtmlDecode(JsonUtil.GetString(raw["topic_url"]).Trim());
            if (url.Length == 0) url = $"{HotTopicPageUrl}?topic_id={topicId}";

            result.Add(new Dictionary<string, object?>
            {
                ["id"] = topicId,
                ["name"] = name,
                ["desc"] = JsonUtil.GetString(raw["topic_desc"]).Trim().Length > 0
                    ? JsonUtil.GetString(raw["topic_desc"]).Trim()
                    : JsonUtil.GetString(raw["abstract"]).Trim(),
                ["rank"] = rank,
                ["discuss_num"] = discuss,
                ["url"] = url,
            });
        }
        return result;
    }

    public static List<Dictionary<string, object?>> ParseHotTopicPosts(string? html,
        IReadOnlyDictionary<string, object?> topic)
    {
        var posts = new List<Dictionary<string, object?>>();
        if (string.IsNullOrEmpty(html)) return posts;

        foreach (var block in HtmlBlocks(html, "li", "thread-item"))
        {
            var link = Regex.Match(block,
                @"<a[^>]*class=""[^""]*\btitle\b[^""]*""[^>]*>(?<text>.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            string href;
            string title;
            if (link.Success)
            {
                href = Regex.Match(link.Value, @"href=""(?<href>[^""]+)""",
                    RegexOptions.IgnoreCase).Groups["href"].Value;
                title = StripTags(link.Groups["text"].Value);
            }
            else
            {
                var anyLink = Regex.Match(block,
                    @"<a[^>]*href=""(?<href>[^""]*?/p/\d+[^""]*)""[^>]*>(?<text>.*?)</a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!anyLink.Success) continue;
                href = anyLink.Groups["href"].Value;
                title = StripTags(anyLink.Groups["text"].Value);
            }

            var tidMatch = Regex.Match(href, @"/p/(\d+)");
            if (!tidMatch.Success) continue;
            var tid = tidMatch.Groups[1].Value;
            if (title.Length == 0) continue;

            var replyMatch = Regex.Match(block,
                @"<span[^>]*class=""[^""]*\breply-num\b[^""]*""[^>]*>(?<n>.*?)</span>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var replyDigits = Regex.Replace(replyMatch.Success
                ? StripTags(replyMatch.Groups["n"].Value) : "", @"\D", "");
            _ = int.TryParse(replyDigits, out var replyCount);

            var contentMatch = Regex.Match(block,
                @"<p[^>]*class=""[^""]*\bcontent\b[^""]*""[^>]*>(?<c>.*?)</p>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var content = contentMatch.Success ? StripTags(contentMatch.Groups["c"].Value) : "";

            var barMatch = Regex.Match(block,
                @"<a[^>]*class=""[^""]*\bforum-name\b[^""]*""[^>]*>(?<b>.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var bar = barMatch.Success ? StripTags(barMatch.Groups["b"].Value) : "";

            posts.Add(new Dictionary<string, object?>
            {
                ["platform"] = "tieba",
                ["type"] = "post",
                ["id"] = tid,
                ["title"] = title,
                ["content"] = content.Length > 300 ? content[..300] : content,
                ["bar"] = bar,
                ["author"] = "",
                ["reply_count"] = replyCount,
                ["date"] = "",
                ["url"] = $"https://tieba.baidu.com/p/{tid}",
                ["hot_topic"] = Item.Str(topic, "name"),
                ["hot_topic_id"] = Item.Str(topic, "id"),
                ["hot_rank"] = Item.Long(topic, "rank"),
                ["hot_discuss"] = Item.Long(topic, "discuss_num"),
            });
        }
        return posts;
    }

    // ----------------------------------------------------------------
    // 请求签名与 protobuf 打包
    // ----------------------------------------------------------------

    public static List<KeyValuePair<string, string>> PcSign(IReadOnlyDictionary<string, string> parameters)
    {
        var data = parameters.Where(p => p.Key != "sign")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        var raw = string.Concat(data.Select(p => $"{p.Key}={p.Value}"));
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw + PcSalt)))
            .ToLowerInvariant();
        data.Add(new KeyValuePair<string, string>("sign", sign));
        return data;
    }

    internal static byte[] AppCommon(string clientVersion = AppClientVersion) =>
        Pb.Concat(
            Pb.PutInt(1, 2),
            Pb.PutStr(2, clientVersion),
            Pb.PutStr(3, AppClientId),
            Pb.PutStr(7, AppCuid),
            Pb.PutStr(9, "SM-S9180"),
            Pb.PutStr(25, "13"),
            Pb.PutStr(26, "samsung"),
            Pb.PutStr(29, "com.baidu.tieba"),
            Pb.PutInt(37, 1080),
            Pb.PutInt(38, 2340));

    internal static byte[] BuildFrsPage(string keyword, int pn = 0, int rn = 30)
    {
        var data = Pb.Concat(
            Pb.PutStr(1, keyword),
            Pb.PutInt(2, rn),
            Pb.PutInt(3, rn + 5),
            Pb.PutInt(15, pn),
            Pb.PutMsg(39, AppCommon()));
        return Pb.PutMsg(1, data);
    }

    internal static byte[] BuildPbPage(string tid, int pn = 1, int rn = 30)
    {
        var data = Pb.Concat(
            Pb.PutInt(4, long.TryParse(tid, out var id) ? id : 0),
            Pb.PutInt(8, 1),
            Pb.PutInt(9, rn),
            Pb.PutInt(13, rn),
            Pb.PutInt(18, pn),
            Pb.PutMsg(25, AppCommon(AppClientVersionNew)));
        return Pb.PutMsg(1, data);
    }

    private static byte[] ProtoBody(byte[] payload)
    {
        var head = Encoding.UTF8.GetBytes(
            $"--{AppProtoBoundary}\r\n" +
            "Content-Disposition: form-data; name=\"data\"; filename=\"file\"\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{AppProtoBoundary}--\r\n");
        return Pb.Concat(head, payload, tail);
    }

    private static string BlocksText(IReadOnlyDictionary<int, List<Pb.Field>> fields, int listField = 5)
    {
        var parts = new List<string>();
        var imageCount = 0;
        foreach (var raw in Pb.GetMessages(fields, listField))
        {
            var block = Pb.Decode(raw);
            var text = Pb.GetStr(block, 2);
            if (text.Length > 0)
            {
                parts.Add(text);
            }
            else if (Pb.GetStr(block, 4).Length > 0 || Pb.GetStr(block, 8).Length > 0)
            {
                imageCount++;
            }
        }
        var joined = StripEmoticonRegex.Replace(string.Concat(parts), "");
        joined = SpaceRegex.Replace(joined, " ").Trim();
        if (imageCount > 0)
        {
            var marker = imageCount == 1 ? "[图片]" : $"[图片×{imageCount}]";
            joined = $"{joined} {marker}".Trim();
        }
        return joined;
    }

    private static string ContentText(IReadOnlyDictionary<int, List<Pb.Field>> threadFields)
    {
        var text = BlocksText(threadFields, 142);
        return text.Length > 0 ? text : BlocksText(threadFields, 14);
    }

    private static string UserName(IReadOnlyDictionary<int, List<Pb.Field>> userFields)
    {
        var name = Pb.GetStr(userFields, 4);
        return name.Length > 0 ? name : Pb.GetStr(userFields, 3);
    }

    private static Dictionary<long, string> BuildUserMap(
        IReadOnlyDictionary<int, List<Pb.Field>> dataFields, int listField = 13)
    {
        var users = new Dictionary<long, string>();
        foreach (var raw in Pb.GetMessages(dataFields, listField))
        {
            var fields = Pb.Decode(raw);
            var uid = Pb.GetInt(fields, 2);
            var name = UserName(fields);
            if (uid != 0 && name.Length > 0) users[uid] = name;
        }
        return users;
    }

    public static string FormatTime(long timestamp)
    {
        if (timestamp <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    // ----------------------------------------------------------------
    // 安全验证检测
    // ----------------------------------------------------------------

    public static bool IsSecurityPage(HttpResponseMessage? response, string text)
    {
        var status = response is null ? 0 : (int)response.StatusCode;
        if (status is 403 or 412 &&
            (text.Contains("验证", StringComparison.Ordinal) ||
             text.Contains("verify", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (SecurityMarkers.Any(m => text.Contains(m, StringComparison.Ordinal))) return true;
        var title = Regex.Match(text, @"<title[^>]*>(.*?)</title>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return title.Success && title.Groups[1].Value.Contains("验证", StringComparison.Ordinal);
    }

    private static void RaiseIfBlocked(HttpResponseMessage response, string text)
    {
        if (IsSecurityPage(response, text)) throw new TiebaBlockedException(BlockedMessage());
        if ((int)response.StatusCode is 401 or 403)
        {
            throw new TiebaBlockedException(
                $"贴吧拒绝访问（HTTP {(int)response.StatusCode}），可能触发了风控或需要登录。");
        }
    }

    public static string BlockedMessage() =>
        "贴吧返回「百度安全验证」页（HTTP 403），本次请求被拦。\n" +
        "本项目默认走移动端 / App 接口，若仍被拦通常是临时风控：\n" +
        "  · 等几分钟再试，或降低「贴吧搜索页数 / 每帖子回复数」；\n" +
        "  · 点设置页「🩺 诊断贴吧连接」确认三条通路的具体情况；\n" +
        "  · 换网络出口（手机热点）后重试。";

    // ----------------------------------------------------------------
    // 搜索帖子
    // ----------------------------------------------------------------

    public async Task<List<Dictionary<string, object?>>> SearchPostsAsync(
        string keyword, int page = 1, int? rn = null)
    {
        var ladder = rn is > 0 ? new[] { rn.Value } : new[] { 40, 30, 20 };
        JsonNode? data = null;
        var lastError = "";

        foreach (var size in ladder)
        {
            var parameters = PcSign(new Dictionary<string, string>
            {
                ["word"] = keyword,
                ["pn"] = page.ToString(CultureInfo.InvariantCulture),
                ["rn"] = size.ToString(CultureInfo.InvariantCulture),
                ["st"] = SearchSort.ToString(CultureInfo.InvariantCulture),
                ["tt"] = "1",
                ["subapp_type"] = "pc",
                ["_client_type"] = "20",
            });
            var headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://tieba.baidu.com/f/search/res",
            };
            var response = await GetJsonAsync(MoSearchUrl, parameters, headers, "搜索").ConfigureAwait(false);
            if (response is not JsonObject obj)
            {
                throw new TiebaBlockedException("贴吧搜索接口返回了意外的数据结构。");
            }
            var no = JsonUtil.ToInt(obj["no"], 0);
            if (no != 0 && obj["no"] is not null)
            {
                lastError = $"no={no} error={JsonUtil.GetString(obj["error"])}";
                continue;
            }
            data = obj;
            LastPageSize = size;
            break;
        }

        if (data is null)
        {
            throw new TiebaBlockedException(
                $"贴吧搜索接口返回错误：{(lastError.Length > 0 ? lastError : "未知错误")}");
        }

        LastSource = "移动端搜索接口";
        LastSearchSort = SearchSort switch
        {
            SearchSortRelevance => "相关度",
            SearchSortTime => "时间",
            _ => "热度",
        };
        return ParsePostList(data["data"]?["post_list"] as JsonArray);
    }

    private static List<Dictionary<string, object?>> ParsePostList(JsonArray? array)
    {
        var results = new List<Dictionary<string, object?>>();
        if (array is null) return results;
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var tid = JsonUtil.GetString(item["tid"]);
            var user = item["user"];
            var content = JsonUtil.GetString(item["content"]).Replace("\r", " ").Replace("\n", " ").Trim();
            var created = JsonUtil.ToLong(item["create_time"]);
            if (created == 0) created = JsonUtil.ToLong(item["time"]);
            results.Add(new Dictionary<string, object?>
            {
                ["platform"] = "tieba",
                ["type"] = "post",
                ["id"] = tid,
                ["title"] = JsonUtil.GetString(item["title"]).Trim(),
                ["content"] = content.Length > 300 ? content[..300] : content,
                ["bar"] = ForumName(item),
                ["forum_id"] = JsonUtil.GetString(item["forum_id"]),
                ["author"] = JsonUtil.GetString(user?["show_nickname"]).Length > 0
                    ? JsonUtil.GetString(user?["show_nickname"])
                    : JsonUtil.GetString(user?["user_name"]),
                ["reply_count"] = JsonUtil.ToLong(item["post_num"]),
                ["date"] = FormatTime(created),
                ["url"] = tid.Length > 0 ? $"https://tieba.baidu.com/p/{tid}" : "",
            });
        }
        return results;
    }

    // ----------------------------------------------------------------
    // 吧名搜索
    // ----------------------------------------------------------------

    public async Task<List<Dictionary<string, object?>>> SearchForumsAsync(
        string word, int page = 1, int rn = ForumSearchPageSize)
    {
        var parameters = PcSign(new Dictionary<string, string>
        {
            ["word"] = word,
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["rn"] = rn.ToString(CultureInfo.InvariantCulture),
            ["st"] = "0",
            ["tt"] = "1",
            ["subapp_type"] = "pc",
            ["_client_type"] = "20",
        });
        var headers = new Dictionary<string, string> { ["Referer"] = "https://tieba.baidu.com/" };
        var response = await GetJsonAsync(MoForumSearchUrl, parameters, headers, "吧名搜索")
            .ConfigureAwait(false);
        if (response is not JsonObject obj)
        {
            throw new TiebaBlockedException("贴吧吧名搜索接口返回了意外的数据结构。");
        }
        var no = JsonUtil.ToInt(obj["no"], 0);
        if (no != 0 && obj["no"] is not null)
        {
            throw new TiebaBlockedException(
                $"贴吧吧名搜索返回错误：no={no} error={JsonUtil.GetString(obj["error"])}");
        }

        LastSource = "移动端吧名搜索接口";
        var results = new List<Dictionary<string, object?>>();
        var payload = obj["data"];
        if (payload?["exactMatch"] is JsonObject exact)
        {
            var item = ForumItem(exact, exact: true);
            if (item is not null) results.Add(item);
        }
        if (payload?["fuzzyMatch"] is JsonArray fuzzy)
        {
            foreach (var raw in fuzzy)
            {
                var item = ForumItem(raw, exact: false);
                if (item is not null &&
                    results.All(r => Item.Str(r, "name") != Item.Str(item, "name")))
                {
                    results.Add(item);
                }
            }
        }
        return results;
    }

    public async Task<ResolvedForums> ResolveForumsAsync(
        string word, int maxCandidates = MaxForumCandidates)
    {
        var value = (word ?? "").Trim();
        var empty = new ResolvedForums { Word = value };
        if (value.Length == 0) return empty;
        var candidates = await SearchForumsAsync(value).ConfigureAwait(false);
        return candidates.Count == 0 ? empty : BuildResolved(candidates, value, maxCandidates);
    }

    public async Task<List<Dictionary<string, object?>>> SearchInBarAsync(
        string barName, string keyword = "", int page = 1)
    {
        var posts = await GetBarPostsAsync(barName, page).ConfigureAwait(false);
        if (keyword.Length == 0) return posts;
        var parsed = Relevance.ParseKeyword(keyword);
        return Relevance.FilterRelevant(posts, parsed, requireTitle: true)
            .Select(post =>
            {
                post.Remove("_rel");
                post.Remove("_rank");
                return post;
            }).ToList();
    }

    // ----------------------------------------------------------------
    // 热点榜单
    // ----------------------------------------------------------------

    public async Task<List<Dictionary<string, object?>>> FetchHotTopicsAsync(
        int limit = HotTopicListSize)
    {
        var headers = new Dictionary<string, string> { ["Referer"] = "https://tieba.baidu.com/" };
        var response = await GetJsonAsync(HotTopicListUrl, null, headers, "获取热榜话题")
            .ConfigureAwait(false);
        if (response is not JsonObject obj)
        {
            throw new TiebaBlockedException("贴吧热议榜接口返回了意外的数据结构。");
        }
        var errno = JsonUtil.ToInt(obj["errno"], 0);
        if (errno != 0 && obj["errno"] is not null)
        {
            throw new TiebaBlockedException(
                $"贴吧热议榜接口返回错误：errno={errno} errmsg={JsonUtil.GetString(obj["errmsg"])}");
        }
        var topics = ParseHotTopics(obj["data"], limit);
        LastSource = "贴吧热议榜接口";
        return topics;
    }

    public async Task<List<Dictionary<string, object?>>> FetchHotTopicPostsAsync(
        IReadOnlyDictionary<string, object?> topic)
    {
        var topicId = Item.Str(topic, "id").Trim();
        if (topicId.Length == 0) return new List<Dictionary<string, object?>>();
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = PcUa,
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            ["Referer"] = "https://tieba.baidu.com/hottopic/",
        };
        var html = await GetHtmlAsync(HotTopicPageUrl,
            new[] { new KeyValuePair<string, string>("topic_id", topicId) }, headers)
            .ConfigureAwait(false);
        var posts = ParseHotTopicPosts(html, topic);
        LastSource = "贴吧热议榜话题页";
        return posts;
    }

    public async Task<Dictionary<string, object?>> SearchHotPostsAsync(
        string keyword, Relevance.KeywordInfo? parsed = null, int? maxTopics = null,
        Action<string>? onLog = null)
    {
        parsed ??= Relevance.ParseKeyword(keyword);
        var limit = maxTopics is null
            ? DefaultHotTopicLimit
            : Math.Clamp(maxTopics.Value, 0, MaxHotTopicLimit);
        var result = new Dictionary<string, object?>
        {
            ["posts"] = new List<Dictionary<string, object?>>(),
            ["topics"] = new List<Dictionary<string, object?>>(),
            ["note"] = "",
            ["error"] = "",
        };
        if (limit <= 0)
        {
            result["note"] = "热榜优先已关闭";
            return result;
        }

        List<Dictionary<string, object?>> topics;
        try
        {
            topics = await FetchHotTopicsAsync().ConfigureAwait(false);
        }
        catch (TiebaBlockedException ex)
        {
            result["error"] = $"热榜话题列表被拦截：{HttpUtils.Trim(ex.Message.Split('\n')[0], 90)}";
            return result;
        }
        catch (Exception ex)
        {
            result["error"] =
                $"热榜话题列表获取失败：{ex.GetType().Name}: {HttpUtils.Trim(ex.Message, 90)}";
            return result;
        }

        if (topics.Count == 0)
        {
            result["error"] = "热榜话题列表为空（接口可能改版）";
            return result;
        }

        var scored = new List<(double Score, Dictionary<string, object?> Topic, Relevance.HotMatch Match)>();
        foreach (var topic in topics)
        {
            var match = Relevance.MatchHotTopic(topic, parsed);
            if (match.Score > 0) scored.Add((match.Score, topic, match));
        }
        scored = scored.OrderByDescending(x => x.Score).ToList();
        var hit = scored.Take(limit).ToList();
        var hitTopics = hit.Select(x =>
        {
            var copy = Item.Clone(x.Topic);
            copy["match"] = x.Match;
            return copy;
        }).ToList();
        result["topics"] = hitTopics;
        if (hit.Count == 0)
        {
            result["note"] = $"热榜 {topics.Count} 个话题均与关键词不相关";
            return result;
        }

        var posts = (List<Dictionary<string, object?>>)result["posts"]!;
        foreach (var topic in hitTopics)
        {
            try
            {
                var batch = await FetchHotTopicPostsAsync(topic).ConfigureAwait(false);
                posts.AddRange(batch);
                onLog?.Invoke($"热榜话题「{Item.Str(topic, "name")}」（第 {Item.Long(topic, "rank")} 名）：" +
                              $"{batch.Count} 条帖子");
            }
            catch (TiebaBlockedException ex)
            {
                result["error"] =
                    $"热榜话题「{Item.Str(topic, "name")}」被拦截：{HttpUtils.Trim(ex.Message.Split('\n')[0], 90)}";
                break;
            }
            catch (Exception ex)
            {
                result["error"] =
                    $"热榜话题「{Item.Str(topic, "name")}」获取失败：" +
                    $"{ex.GetType().Name}: {HttpUtils.Trim(ex.Message, 60)}";
            }
        }

        if (posts.Count > 0)
        {
            var names = string.Join("、", hitTopics.Select(t => $"「{Item.Str(t, "name")}」"));
            result["note"] = $"命中 {hitTopics.Count} 个热榜话题：{names}，取到 {posts.Count} 条热榜帖子";
        }
        else if (string.IsNullOrEmpty(result["error"]?.ToString()))
        {
            result["note"] = $"命中 {hitTopics.Count} 个热榜话题，但话题页未解析到帖子";
        }
        return result;
    }

    // ----------------------------------------------------------------
    // 帖子楼层
    // ----------------------------------------------------------------

    public async Task<Dictionary<string, object?>> GetPostRepliesAsync(string postId, int maxPages = 2)
    {
        try
        {
            return await GetPostRepliesProtoAsync(postId, maxPages).ConfigureAwait(false);
        }
        catch (TiebaBlockedException)
        {
            throw;
        }
        catch
        {
            return await GetPostRepliesHtmlAsync(postId, maxPages).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, object?>> GetPostRepliesProtoAsync(string postId, int maxPages)
    {
        var url = $"https://{AppBaseHost}/c/f/pb/page?cmd=302001";
        var title = "";
        var mainContent = "";
        var replies = new List<Dictionary<string, object?>>();

        for (var page = 1; page <= maxPages; page++)
        {
            var top = await PostProtoAsync(url, BuildPbPage(postId, page)).ConfigureAwait(false);
            var err = Pb.GetMessage(top, 1);
            if (err is not null)
            {
                var errFields = Pb.Decode(err);
                var code = Pb.GetInt(errFields, 1);
                if (code != 0)
                {
                    throw new TiebaBlockedException(
                        $"贴吧帖子接口返回错误码 {code}：{Pb.GetStr(errFields, 2)}");
                }
            }

            var data = Pb.GetMessage(top, 2);
            if (data is null) break;
            var dataFields = Pb.Decode(data);
            var userMap = BuildUserMap(dataFields);

            var thread = Pb.GetMessage(dataFields, 8);
            if (thread is not null)
            {
                var threadFields = Pb.Decode(thread);
                if (title.Length == 0) title = Pb.GetStr(threadFields, 3).Trim();
                if (mainContent.Length == 0) mainContent = ContentText(threadFields);
            }

            var postList = Pb.GetMessages(dataFields, 6);
            if (postList.Count == 0) break;
            foreach (var rawPost in postList)
            {
                var post = Pb.Decode(rawPost);
                var floor = Pb.GetInt(post, 3);
                var content = BlocksText(post);
                userMap.TryGetValue(Pb.GetInt(post, 19), out var author);
                if (content.Length == 0) continue;
                if (mainContent.Length == 0 && floor == 1)
                {
                    mainContent = content;
                    continue;
                }
                replies.Add(new Dictionary<string, object?>
                {
                    ["platform"] = "tieba",
                    ["type"] = "reply",
                    ["id"] = postId,
                    ["user"] = author ?? "",
                    ["content"] = content,
                    ["floor"] = floor,
                    ["time"] = FormatTime(Pb.GetInt(post, 4)),
                });
            }
            if (postList.Count < 20) break;
        }

        LastSource = "App protobuf 接口";
        return new Dictionary<string, object?>
        {
            ["title"] = title,
            ["content"] = mainContent,
            ["replies"] = replies,
            ["reply_count"] = replies.Count,
        };
    }

    private async Task<Dictionary<string, object?>> GetPostRepliesHtmlAsync(string postId, int maxPages)
    {
        var title = "";
        var mainContent = "";
        var replies = new List<Dictionary<string, object?>>();

        for (var page = 1; page <= maxPages; page++)
        {
            var html = await GetHtmlAsync("https://tieba.baidu.com/mo/q/m",
                new[]
                {
                    new KeyValuePair<string, string>("kz", postId),
                    new KeyValuePair<string, string>("pn", page.ToString(CultureInfo.InvariantCulture)),
                }, null).ConfigureAwait(false);

            if (title.Length == 0)
            {
                var titleTag = Regex.Match(html,
                    @"<h3[^>]*class=""[^""]*core_title_txt[^""]*""[^>]*>(?<t>.*?)</h3>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!titleTag.Success)
                {
                    titleTag = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                }
                title = titleTag.Success ? StripTags(titleTag.Groups["t"].Value) : "";
            }

            var blocks = HtmlBlocks(html, "div", "l_post").ToList();
            foreach (var block in blocks)
            {
                var authorMatch = Regex.Match(block,
                    @"<a[^>]*class=""[^""]*p_author_name[^""]*""[^>]*>(?<a>.*?)</a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var author = authorMatch.Success ? StripTags(authorMatch.Groups["a"].Value) : "";

                var contentMatch = Regex.Match(block,
                    @"<div[^>]*class=""[^""]*d_post_content[^""]*""[^>]*>(?<c>.*?)</div>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var content = contentMatch.Success ? StripTags(contentMatch.Groups["c"].Value) : "";
                if (content.Length == 0) continue;

                var floor = 0;
                var floorMatch = Regex.Match(block,
                    @"<div[^>]*class=""[^""]*post-tail-wrap[^""]*""[^>]*>(?<f>.*?)</div>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (floorMatch.Success)
                {
                    var number = Regex.Match(StripTags(floorMatch.Groups["f"].Value), @"\d+");
                    if (number.Success) int.TryParse(number.Value, out floor);
                }

                if (mainContent.Length == 0 && floor == 1)
                {
                    mainContent = content;
                }
                else
                {
                    replies.Add(new Dictionary<string, object?>
                    {
                        ["platform"] = "tieba",
                        ["type"] = "reply",
                        ["id"] = postId,
                        ["user"] = author,
                        ["content"] = content,
                        ["floor"] = floor,
                        ["time"] = "",
                    });
                }
            }
            if (blocks.Count < 10) break;
        }

        LastSource = "移动版 HTML 页面";
        return new Dictionary<string, object?>
        {
            ["title"] = title,
            ["content"] = mainContent,
            ["replies"] = replies,
            ["reply_count"] = replies.Count,
        };
    }

    // ----------------------------------------------------------------
    // 吧帖子列表
    // ----------------------------------------------------------------

    public async Task<List<Dictionary<string, object?>>> GetBarPostsAsync(string barName, int page = 1)
    {
        var url = $"http://{AppBaseHost}/c/f/frs/page?cmd=301001";
        var top = await PostProtoAsync(url, BuildFrsPage(barName, Math.Max(0, page - 1)))
            .ConfigureAwait(false);

        var err = Pb.GetMessage(top, 1);
        if (err is not null)
        {
            var errFields = Pb.Decode(err);
            var code = Pb.GetInt(errFields, 1);
            if (code != 0)
            {
                throw new TiebaBlockedException(
                    $"贴吧吧列表接口返回错误码 {code}：{Pb.GetStr(errFields, 2)}");
            }
        }

        var data = Pb.GetMessage(top, 2);
        if (data is null) return new List<Dictionary<string, object?>>();
        var dataFields = Pb.Decode(data);

        var forumName = barName;
        var forum = Pb.GetMessage(dataFields, 2);
        if (forum is not null)
        {
            forumName = Pb.GetStr(Pb.Decode(forum), 2);
            if (forumName.Length == 0) forumName = barName;
        }

        var userMap = BuildUserMap(dataFields, 17);
        LastSource = "App protobuf 接口";
        var results = new List<Dictionary<string, object?>>();
        foreach (var rawThread in Pb.GetMessages(dataFields, 7))
        {
            var thread = Pb.Decode(rawThread);
            var tid = Pb.GetInt(thread, 1);
            var author = "";
            var authorMessage = Pb.GetMessage(thread, 18);
            if (authorMessage is not null) author = UserName(Pb.Decode(authorMessage));
            if (author.Length == 0)
            {
                userMap.TryGetValue(Pb.GetInt(thread, 56), out var mapped);
                author = mapped ?? "";
            }

            var bar = Pb.GetStr(thread, 28);
            if (bar.Length == 0) bar = forumName;
            results.Add(new Dictionary<string, object?>
            {
                ["platform"] = "tieba",
                ["type"] = "post",
                ["id"] = tid.ToString(CultureInfo.InvariantCulture),
                ["title"] = Pb.GetStr(thread, 3).Trim(),
                ["content"] = "",
                ["bar"] = bar,
                ["author"] = author,
                ["reply_count"] = Pb.GetInt(thread, 4),
                ["date"] = FormatTime(Pb.GetInt(thread, 45)),
                ["url"] = tid != 0 ? $"https://tieba.baidu.com/p/{tid}" : "",
            });
        }
        return results;
    }

    // ----------------------------------------------------------------
    // 传统模式：综合采集
    // ----------------------------------------------------------------

    public async Task<Dictionary<string, object?>> CollectKeywordDataAsync(
        string keyword, int maxPages = 3, int repliesPerPost = 20)
    {
        var allPosts = new List<Dictionary<string, object?>>();
        var allReplies = new List<Dictionary<string, object?>>();
        var partialBlock = "";

        for (var page = 1; page <= maxPages; page++)
        {
            if (_cancellationToken.IsCancellationRequested) break;
            try
            {
                var posts = await SearchPostsAsync(keyword, page).ConfigureAwait(false);
                if (posts.Count == 0) break;
                allPosts.AddRange(posts);
            }
            catch (TiebaBlockedException ex)
            {
                if (allPosts.Count == 0) throw;
                partialBlock = ex.Message;
                break;
            }
            catch
            {
                break;
            }
        }

        var topPosts = allPosts.OrderByDescending(p => Item.Long(p, "reply_count")).ToList();
        var repliesNeeded = repliesPerPost;
        foreach (var post in topPosts)
        {
            if (repliesNeeded <= 0 || Item.Str(post, "id").Length == 0 ||
                _cancellationToken.IsCancellationRequested) break;
            try
            {
                var detail = await GetPostRepliesAsync(Item.Str(post, "id"), 1).ConfigureAwait(false);
                var replies = detail["replies"] as List<Dictionary<string, object?>>
                              ?? new List<Dictionary<string, object?>>();
                allReplies.AddRange(replies);
                repliesNeeded -= replies.Count;
            }
            catch (TiebaBlockedException ex)
            {
                partialBlock = partialBlock.Length > 0 ? partialBlock : ex.Message;
                break;
            }
            catch
            {
                // 单个帖子失败继续
            }
        }

        var result = new Dictionary<string, object?>
        {
            ["posts"] = allPosts,
            ["replies"] = allReplies,
            ["total_posts"] = allPosts.Count,
            ["total_replies"] = allReplies.Count,
        };
        if (partialBlock.Length > 0) result["partial_blocked"] = partialBlock;
        return result;
    }

    // ----------------------------------------------------------------
    // HTML 辅助
    // ----------------------------------------------------------------

    private static IEnumerable<string> HtmlBlocks(string html, string tag, string classToken)
    {
        var pattern = $@"<{tag}\b[^>]*class\s*=\s*[""'][^""']*\b{Regex.Escape(classToken)}\b[^""']*[""'][^>]*>";
        var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count
                ? matches[i + 1].Index
                : html.Length;
            var block = html.Substring(start, end - start);
            if (tag.Equals("li", StringComparison.OrdinalIgnoreCase))
            {
                var close = block.IndexOf("</li>", StringComparison.OrdinalIgnoreCase);
                if (close > 0) block = block[..(close + 5)];
            }
            yield return block;
        }
    }

    private static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<script[^>]*>.*?</script>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\s*\n\s*", "\n");
        return text.Trim();
    }
}
