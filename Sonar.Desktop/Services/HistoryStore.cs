using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Sonar.Services;

public sealed class HistoryEntry
{
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Mode { get; init; } = "";
    public DateTime Time { get; init; }
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public override string ToString() => $"{TimeText} · {Title}";
}

/// <summary>
/// 分析历史归档：报告 Markdown + 原始数据 JSON，位置与 Python 版一致
/// （~/.sonar/history）。归档走后台线程，不阻塞「分析完成」反馈。
/// </summary>
public static class HistoryStore
{
    private static readonly Regex SanitizeRegex = new(@"[\\/:*?""<>|\s]+", RegexOptions.Compiled);
    private static readonly Regex FileNameRegex = new(
        @"^(?<ts>\d{8}_\d{6})_(?<mode>[^_]+)_(?<keyword>.+)$", RegexOptions.Compiled);

    public static string SaveAnalysis(string mode, string keyword, string report,
        IDictionary<string, long>? usage = null,
        Dictionary<string, object?>? bilibiliData = null,
        Dictionary<string, object?>? tiebaData = null)
    {
        Directory.CreateDirectory(AppPaths.HistoryDir);
        var now = DateTime.Now;
        var baseName = $"{now:yyyyMMdd_HHmmss}_{Sanitize(mode)}_{Sanitize(keyword)}";
        var path = Path.Combine(AppPaths.HistoryDir, baseName + ".md");
        var suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(AppPaths.HistoryDir, $"{baseName}_{suffix++}.md");
        }

        var header = new List<string>
        {
            $"# {keyword}",
            "",
            $"- 时间: {now:yyyy-MM-dd HH:mm:ss}",
            $"- 模式: {mode}",
        };
        if (usage is not null && usage.TryGetValue("total_tokens", out var tokens) && tokens > 0)
        {
            header.Add($"- LLM 用量: {tokens} tokens（请求 {usage["requests"]} 次，" +
                       $"输入 {usage["prompt_tokens"]} / 输出 {usage["completion_tokens"]}）");
        }
        if (bilibiliData is not null)
        {
            header.Add($"- B站: {Item.Long(bilibiliData, "total_videos")} 视频, " +
                       $"{Item.Long(bilibiliData, "total_comments")} 评论");
        }
        if (tiebaData is not null)
        {
            header.Add($"- 贴吧: {Item.Long(tiebaData, "total_posts")} 帖子, " +
                       $"{Item.Long(tiebaData, "total_replies")} 回复");
        }

        File.WriteAllText(path,
            string.Join("\n", header) + "\n\n---\n\n" + (report ?? ""),
            new UTF8Encoding(false));

        if (bilibiliData is not null || tiebaData is not null)
        {
            var dataPath = Path.ChangeExtension(path, ".json");
            var payload = new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["keyword"] = keyword,
                ["time"] = now.ToString("o", CultureInfo.InvariantCulture),
                ["usage"] = usage,
                ["bilibili_data"] = bilibiliData,
                ["tieba_data"] = tiebaData,
            };
            File.WriteAllText(dataPath, JsonUtil.Serialize(payload), new UTF8Encoding(false));
        }

        return path;
    }

    public static List<HistoryEntry> ListEntries()
    {
        var entries = new List<HistoryEntry>();
        try
        {
            if (!Directory.Exists(AppPaths.HistoryDir)) return entries;
            foreach (var file in Directory.EnumerateFiles(AppPaths.HistoryDir, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var match = FileNameRegex.Match(name);
                var time = File.GetLastWriteTime(file);
                if (match.Success)
                {
                    DateTime.TryParseExact(match.Groups["ts"].Value, "yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
                }

                var title = "";
                try
                {
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    var first = reader.ReadLine();
                    if (!string.IsNullOrEmpty(first) && first.StartsWith("# ", StringComparison.Ordinal))
                    {
                        title = first[2..].Trim();
                    }
                }
                catch
                {
                    // 忽略读取失败的单条记录
                }

                entries.Add(new HistoryEntry
                {
                    Path = file,
                    FileName = Path.GetFileName(file),
                    Title = title.Length > 0 ? title : match.Groups["keyword"].Value,
                    Mode = match.Success ? match.Groups["mode"].Value : "",
                    Time = time,
                });
            }
        }
        catch
        {
            // 历史目录不可读时返回空列表
        }
        return entries.OrderByDescending(e => e.Time).ToList();
    }

    public static string LoadReport(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";

    public static void OpenFolder()
    {
        Directory.CreateDirectory(AppPaths.HistoryDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.HistoryDir,
            UseShellExecute = true,
        });
    }

    private static string Sanitize(string? name)
    {
        var cleaned = SanitizeRegex.Replace((name ?? "").Trim(), "_").Trim('_');
        if (cleaned.Length == 0) cleaned = "untitled";
        return cleaned.Length > 30 ? cleaned[..30] : cleaned;
    }
}
