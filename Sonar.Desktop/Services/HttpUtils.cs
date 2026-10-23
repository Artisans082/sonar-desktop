using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Sonar.Services;

/// <summary>
/// 统一 HTTP 工具：直连优先、系统代理备用、编码嗅探。
/// 与 Python http_utils.py 的设计一致——国内站点默认直连，代理只在兜底时使用。
/// </summary>
public static class HttpUtils
{
    static HttpUtils()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool SystemProxyConfigured
    {
        get
        {
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy is null) return false;
                var probe = proxy.GetProxy(new Uri("https://www.baidu.com/"));
                if (probe is null) return false;
                return !(probe.Host.Equals("www.baidu.com", StringComparison.OrdinalIgnoreCase)
                         && probe.Port == 443);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string ProxyDescription
    {
        get
        {
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                var uri = proxy?.GetProxy(new Uri("https://www.baidu.com/"));
                return uri?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    public static HttpClient CreateClient(
        string? cookie,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        bool useProxy = false,
        TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli,
            UseCookies = false,
            AllowAutoRedirect = true,
        };

        if (useProxy)
        {
            var proxy = WebRequest.GetSystemWebProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
        }
        else
        {
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };

        // 注意：这里不设置默认 UA / Accept-Language，避免与各采集器自己的
        // 移动端 / App / 桌面 UA 叠加成两个 User-Agent 头。
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        }

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }
        }

        return client;
    }

    /// <summary>Python urllib.parse.quote_plus 的等价实现（用于 WBI 与贴吧 PC 签名）。</summary>
    public static string UrlEncode(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => $"{UrlEncode(p.Key)}={UrlEncode(p.Value)}"));

    /// <summary>
    /// 编码嗅探：贴吧安全验证页会用 GBK 字节声明 utf-8，
    /// 因此不能盲信 Content-Type，按 UTF-8 → GB18030 依次尝试。
    /// </summary>
    public static async Task<string> DecodeTextAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return DecodeBytes(bytes, response.Content.Headers.ContentType?.CharSet);
    }

    public static string DecodeBytes(byte[] bytes, string? declaredCharset)
    {
        if (bytes.Length == 0) return "";

        // BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return SafeDecode(bytes, 3, Encoding.UTF8);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return SafeDecode(bytes, 2, Encoding.Unicode);

        var candidates = new List<Encoding>();
        var declared = (declaredCharset ?? "").Trim().Trim('"');
        if (declared.Length > 0 && !declared.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                candidates.Add(Encoding.GetEncoding(declared));
            }
            catch
            {
                // 忽略未知编码
            }
        }
        candidates.Add(new UTF8Encoding(false, true));
        try
        {
            candidates.Add(Encoding.GetEncoding("GB18030"));
        }
        catch
        {
            // 某些精简运行环境可能没有 GB18030
        }

        string best = Encoding.UTF8.GetString(bytes);
        foreach (var encoding in candidates)
        {
            try
            {
                var text = encoding.GetString(bytes);
                if (!text.Contains('\uFFFD')) return text;
                best = text;
            }
            catch
            {
                // 该编码解码失败，继续下一个
            }
        }
        return best;
    }

    private static string SafeDecode(byte[] bytes, int offset, Encoding encoding) =>
        encoding.GetString(bytes, offset, bytes.Length - offset);

    public static bool IsProxyError(Exception exception) =>
        exception is HttpRequestException http &&
        (http.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase)
         || http.InnerException?.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase) == true);

    public static MediaTypeHeaderValue? ParseContentType(string value)
    {
        return MediaTypeHeaderValue.TryParse(value, out var parsed) ? parsed : null;
    }

    /// <summary>贴吧诊断使用的对照站点。</summary>
    public const string DiagnoseControlUrl = "https://www.baidu.com/";

    public sealed class DiagnoseEntry
    {        public string Route { get; set; } = "";
        public bool Ok { get; set; }
        public int? Status { get; set; }
        public long Length { get; set; }
        public bool Blocked { get; set; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>对同一 URL 依次尝试直连与系统代理，用于「诊断贴吧连接」。</summary>
    public static async Task<List<DiagnoseEntry>> DiagnoseAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DiagnoseEntry>();
        var routes = new List<(string Label, bool Proxy)> { ("直连（不使用代理）", false) };
        if (SystemProxyConfigured) routes.Add(("系统代理", true));

        var baseHeaders = headers?.ToList() ?? new List<KeyValuePair<string, string>>();
        if (baseHeaders.All(h => !h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
        {
            baseHeaders.Add(new KeyValuePair<string, string>("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"));
        }

        foreach (var (label, useProxy) in routes)
        {
            var entry = new DiagnoseEntry { Route = label };
            using var client = CreateClient(null, baseHeaders, useProxy, TimeSpan.FromSeconds(15));
            try
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                var text = await DecodeTextAsync(response).ConfigureAwait(false);
                entry.Ok = true;
                entry.Status = (int)response.StatusCode;
                entry.Length = text.Length;
                entry.Blocked = TiebaCollector.IsSecurityPage(response, text);
                var titleStart = text.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
                if (titleStart >= 0)
                {
                    var titleEnd = text.IndexOf("</title>", titleStart, StringComparison.OrdinalIgnoreCase);
                    if (titleEnd > titleStart)
                    {
                        entry.Title = text.Substring(titleStart + 7, titleEnd - titleStart - 7).Trim();
                        if (entry.Title.Length > 40) entry.Title = entry.Title[..40];
                    }
                }
            }
            catch (Exception ex)
            {
                entry.Detail = $"{ex.GetType().Name}: {Trim(ex.Message, 120)}";
                if (IsProxyError(ex)) entry.Detail += "  ← 代理不可用";
            }
            result.Add(entry);
        }
        return result;
    }

    public static string Trim(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text[..max]);
}
