using System.Text;
using Sonar.Models;

namespace Sonar.Services;

public sealed class CollectCancelledException : Exception
{
    public CollectCancelledException(string message) : base(message) { }
}

/// <summary>一次两阶段采集的过程记录，用于界面日志与 LLM 提示。</summary>
public sealed class CollectReport
{
    public string Keyword { get; set; } = "";
    public string Platform { get; set; } = "";
    public Relevance.KeywordInfo? Parsed { get; set; }
    public List<string> Lines { get; } = new();
    public int BiliPool { get; set; }
    public int TiebaPool { get; set; }
    public int BiliKept { get; set; }
    public int TiebaKept { get; set; }
    public List<Dictionary<string, object?>> BiliTargets { get; } = new();
    public List<Dictionary<string, object?>> TiebaTargets { get; } = new();
    public List<string> TiebaBars { get; } = new();
    public string TiebaMatchedBy { get; set; } = "";
    public List<string> TiebaHotTopics { get; } = new();
    public int TiebaHotPosts { get; set; }
    public string TiebaHotNote { get; set; } = "";
    public Dictionary<string, string> TimeNotes { get; } = new();
    public WindowSpec WindowSpec { get; set; } = WindowSpec.Default;
    public int WindowHours { get; set; }
    public List<string> Errors { get; } = new();

    public void Add(string text) => Lines.Add(text);

    public string Summary() => string.Join("\n", Lines);

    /// <summary>给 LLM 看的采集说明：词性分工 + 数据规模 + 时效 + 异常。</summary>
    public string PromptNote()
    {
        var parsed = Parsed ?? new Relevance.KeywordInfo();
        var builder = new StringBuilder();
        builder.AppendLine($"【采集说明】用户输入：{Keyword}");
        var coreLine = $"  核心词（分析主体）：{parsed.Core}";
        if (parsed.Qualifiers.Count > 0)
            coreLine += $"；限定词：{string.Join("、", parsed.Qualifiers)}";
        if (parsed.Platform.Count > 0)
            coreLine += $"；平台/主题词：{string.Join("、", parsed.Platform)}";
        builder.AppendLine(coreLine);
        builder.AppendLine($"  检索变体：{string.Join("、", parsed.Variants)}");

        if (Platform.Length > 0)
        {
            var bars = TiebaBars.Count > 0 ? string.Join("、", TiebaBars) : "未命中";
            builder.AppendLine($"  平台/吧名：{Platform}" +
                               $"（贴吧先按吧名检索，再在吧内搜关键词；实际检索吧：{bars}）");
        }
        if (TiebaHotTopics.Count > 0 && TiebaHotPosts > 0)
        {
            var names = string.Join("、", TiebaHotTopics.Select(n => $"「{n}」"));
            builder.AppendLine($"  贴吧热榜：命中 {names}，已优先纳入 {TiebaHotPosts} 条" +
                               "热榜帖子（热榜为跨吧聚合，部分帖子可能不属于上述吧族）");
        }
        else if (TiebaHotNote.Length > 0)
        {
            builder.AppendLine($"  贴吧热榜：{TiebaHotNote}");
        }

        builder.AppendLine($"  时效窗口：{Relevance.WindowLabel(WindowSpec)}");
        builder.AppendLine($"  B站：宽召回 {BiliPool} 条标题 → 筛出 {BiliKept} 条相关 → " +
                           $"深挖 {BiliTargets.Count} 个视频的评论");
        builder.AppendLine($"  贴吧：宽召回 {TiebaPool} 条标题 → 筛出 {TiebaKept} 条相关 → " +
                           $"深挖 {TiebaTargets.Count} 个帖子的回复");
        foreach (var (platform, note) in TimeNotes)
        {
            builder.AppendLine($"  时效说明（{platform}）：{note}");
        }
        if (Errors.Count > 0)
        {
            builder.AppendLine("  采集中遇到的问题：" + string.Join("；", Errors));
        }
        builder.Append("  注意：本次数据由上述关键词检索而来，分析请围绕**核心词**展开，" +
                       "限定词只作为语境；若数据量偏少或时间偏旧，请在报告中如实说明。");
        return builder.ToString();
    }
}

/// <summary>
/// 两阶段采集编排：宽召回 → 本地筛选 → 精准深挖。
/// 贴吧额外遵循「热榜 → 吧名 → 吧内 → 深挖」的顺序。
/// </summary>
public static class Pipeline
{
    public static async Task<(Dictionary<string, object?>? Bilibili, Dictionary<string, object?>? Tieba,
        CollectReport Report)> CollectTwoStageAsync(
        string keyword,
        string platform = "",
        BilibiliCollector? biliCollector = null,
        TiebaCollector? tiebaCollector = null,
        WindowSpec? timeWindow = null,
        int deepBili = 4,
        int deepTieba = 4,
        int maxVariants = Relevance.MaxVariants,
        Action<string>? onLog = null,
        int variantPages = 1,
        Func<bool>? cancelCheck = null,
        bool hotFirst = true,
        int hotTopics = 2,
        CancellationToken cancellationToken = default)
    {
        var report = new CollectReport
        {
            Keyword = keyword,
            Platform = (platform ?? "").Trim(),
            WindowSpec = WindowSpec.Normalize(timeWindow),
        };
        var windowHours = Relevance.WindowHours(report.WindowSpec);
        report.WindowHours = windowHours;
        var parsed = Relevance.ParseKeyword(Relevance.ComposeKeyword(platform, keyword));
        report.Parsed = parsed;
        var variants = (parsed.Variants.Count > 0 ? parsed.Variants : new List<string> { keyword })
            .Take(Math.Max(1, maxVariants)).ToList();

        void Log(string text)
        {
            report.Add(text);
            onLog?.Invoke(text);
        }

        void EnsureRunning()
        {
            if (cancellationToken.IsCancellationRequested || (cancelCheck?.Invoke() ?? false))
            {
                throw new CollectCancelledException("采集已被用户停止");
            }
        }

        var keywordLine = $"关键词解析：核心「{parsed.Core}」";
        if (parsed.Qualifiers.Count > 0) keywordLine += $"，限定「{string.Join("、", parsed.Qualifiers)}」";
        if (parsed.Platform.Count > 0) keywordLine += $"，平台「{string.Join("、", parsed.Platform)}」";
        Log(keywordLine);
        Log($"时效窗口：{Relevance.WindowLabel(report.WindowSpec)}");
        Log($"检索变体（{variants.Count} 个）：{string.Join("、", variants)}");

        var biliData = await CollectBilibiliAsync(biliCollector, variants, parsed, report, Log,
            windowHours, deepBili, variantPages, EnsureRunning, cancellationToken).ConfigureAwait(false);
        EnsureRunning();
        var tiebaData = await CollectTiebaAsync(tiebaCollector, variants, parsed, report, Log,
            windowHours, deepTieba, variantPages, report.Platform, EnsureRunning,
            hotFirst, hotTopics, cancellationToken).ConfigureAwait(false);

        return (biliData, tiebaData, report);
    }

    private static async Task<Dictionary<string, object?>?> CollectBilibiliAsync(
        BilibiliCollector? collector, List<string> variants, Relevance.KeywordInfo parsed,
        CollectReport report, Action<string> log, int windowHours, int deep, int pages,
        Action ensureRunning, CancellationToken cancellationToken)
    {
        if (collector is null) return null;

        var pool = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in variants)
        {
            for (var page = 1; page <= Math.Max(1, pages); page++)
            {
                ensureRunning();
                List<Dictionary<string, object?>> videos;
                try
                {
                    videos = await collector.SearchVideosAsync(word, page,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var message = $"B站搜索「{word}」失败：{ex.GetType().Name}: {HttpUtils.Trim(ex.Message, 60)}";
                    report.Errors.Add(message);
                    log(message);
                    break;
                }

                var fresh = new List<Dictionary<string, object?>>();
                foreach (var video in videos)
                {
                    var id = Item.Str(video, "id");
                    if (id.Length == 0 || !seen.Add(id)) continue;
                    fresh.Add(video);
                }
                pool.AddRange(fresh);
                log($"  B站「{word}」第{page}页：{videos.Count} 条（新增 {fresh.Count}）");
                if (videos.Count < 10) break;
            }
        }

        report.BiliPool = pool.Count;
        if (pool.Count == 0)
        {
            log("B站未召回任何视频");
            return null;
        }

        var ranked = Relevance.RankItems(pool, parsed, hotKey: "play", replyKey: "reply",
            hoursLimit: windowHours);
        var (kept, effective, note) = Relevance.ApplyTimeWindow(ranked, windowHours,
            dateField: "pubdate", baseLabel: Relevance.WindowLabel(report.WindowSpec));
        report.BiliKept = kept.Count;
        report.TimeNotes["B站"] = note;
        log($"B站本地筛选：{pool.Count} → 相关 {ranked.Count} → 时效：{note}");

        var targets = Relevance.SelectTargets(kept, deep);
        report.BiliTargets.AddRange(targets);
        log(targets.Count > 0
            ? $"B站深挖目标（{targets.Count} 个）：" +
              string.Join("；", targets.Select(t => HttpUtils.Trim(Item.Str(t, "title"), 26)))
            : "B站无深挖目标");

        var comments = new List<Dictionary<string, object?>>();
        foreach (var video in targets)
        {
            ensureRunning();
            try
            {
                var batch = await collector.GetVideoCommentsAsync(Item.Long(video, "aid"),
                    1, 20, 2, cancellationToken).ConfigureAwait(false);
                comments.AddRange(batch);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"获取视频《{HttpUtils.Trim(Item.Str(video, "title"), 18)}》评论失败：" +
                              HttpUtils.Trim(ex.Message, 50);
                report.Errors.Add(message);
                log("  " + message);
            }
        }

        var videosOut = kept.Select(Relevance.StripInternal).ToList();
        return new Dictionary<string, object?>
        {
            ["videos"] = videosOut,
            ["comments"] = comments,
            ["video_info"] = videosOut.Count > 0 ? videosOut[0] : null,
            ["total_videos"] = videosOut.Count,
            ["total_comments"] = comments.Count,
            ["pool_size"] = report.BiliPool,
            ["time_window_hours"] = effective,
        };
    }

    private static async Task<Dictionary<string, object?>?> CollectTiebaAsync(
        TiebaCollector? collector, List<string> variants, Relevance.KeywordInfo parsed,
        CollectReport report, Action<string> log, int windowHours, int deep, int pages,
        string platform, Action ensureRunning, bool hotFirst, int hotTopics,
        CancellationToken cancellationToken)
    {
        if (collector is null) return null;

        var pool = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blocked = "";
        ResolvedForums? resolved = null;

        List<Dictionary<string, object?>> AddPosts(IEnumerable<Dictionary<string, object?>> posts)
        {
            var fresh = new List<Dictionary<string, object?>>();
            foreach (var post in posts)
            {
                var id = Item.Str(post, "id");
                if (id.Length == 0 || !seen.Add(id)) continue;
                fresh.Add(post);
            }
            pool.AddRange(fresh);
            return fresh;
        }

        void MarkBlocked(Exception ex, string action)
        {
            if (blocked.Length == 0) blocked = ex.Message;
            log($"  {action}被贴吧风控拦截，停止继续检索");
        }

        // ① 热点榜单
        if (hotFirst && blocked.Length == 0)
        {
            ensureRunning();
            Dictionary<string, object?> hotResult;
            try
            {
                hotResult = await collector.SearchHotPostsAsync(report.Keyword, parsed, hotTopics,
                    text => log("  " + text)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                hotResult = new Dictionary<string, object?>
                {
                    ["posts"] = new List<Dictionary<string, object?>>(),
                    ["topics"] = new List<Dictionary<string, object?>>(),
                    ["note"] = "",
                    ["error"] = $"{ex.GetType().Name}: {HttpUtils.Trim(ex.Message, 80)}",
                };
            }

            var hotPosts = hotResult["posts"] as List<Dictionary<string, object?>>
                           ?? new List<Dictionary<string, object?>>();
            report.TiebaHotTopics.AddRange((hotResult["topics"] as List<Dictionary<string, object?>>
                    ?? new List<Dictionary<string, object?>>())
                .Select(t => Item.Str(t, "name")).Where(n => n.Length > 0));
            if (hotPosts.Count > 0)
            {
                var fresh = AddPosts(hotPosts);
                report.TiebaHotPosts = fresh.Count;
                report.TiebaHotNote = hotResult["note"]?.ToString() ?? "";
                log($"贴吧热榜：{(report.TiebaHotNote.Length > 0 ? report.TiebaHotNote : "命中相关话题")}；" +
                    $"优先纳入 {fresh.Count} 条热榜帖子");
            }
            else if (!string.IsNullOrEmpty(hotResult["error"]?.ToString()))
            {
                report.TiebaHotNote = hotResult["error"]!.ToString()!;
                log($"贴吧热榜不可用：{report.TiebaHotNote}；本次按常规搜索");
            }
            else
            {
                report.TiebaHotNote = hotResult["note"]?.ToString() ?? "";
                if (report.TiebaHotNote.Length == 0) report.TiebaHotNote = "未命中相关话题";
                log($"贴吧热榜：{report.TiebaHotNote}；本次按常规搜索");
            }
        }

        // ② 吧名检索
        if (platform.Length > 0)
        {
            ensureRunning();
            try
            {
                resolved = await collector.ResolveForumsAsync(platform).ConfigureAwait(false);
            }
            catch (TiebaBlockedException ex)
            {
                MarkBlocked(ex, $"吧名检索「{platform}」");
            }
            catch (Exception ex)
            {
                var message = $"贴吧吧名检索「{platform}」失败：{ex.GetType().Name}: " +
                              HttpUtils.Trim(ex.Message, 60);
                report.Errors.Add(message);
                log(message);
            }

            if (resolved?.Primary is not null)
            {
                var primary = Item.Str(resolved.Primary, "name");
                report.TiebaBars.AddRange(resolved.Candidates.Select(c => Item.Str(c, "name")));
                report.TiebaMatchedBy = resolved.MatchedBy;
                var relatives = resolved.Candidates.Skip(1).Select(c => Item.Str(c, "name")).ToList();
                log($"贴吧吧名检索：「{platform}」→ 主吧「{primary}」" +
                    $"（{report.TiebaMatchedBy}）" +
                    (relatives.Count > 0 ? $"，相关吧：{string.Join("、", relatives)}" : ""));
            }
            else if (blocked.Length == 0)
            {
                log($"贴吧未找到与「{platform}」匹配的吧，退回全站搜索");
            }
        }

        var barMode = resolved?.Primary is not null;

        // ③ 吧内检索
        if (barMode && resolved is not null)
        {
            var primary = Item.Str(resolved.Primary, "name");
            for (var page = 1; page <= Math.Max(1, pages); page++)
            {
                ensureRunning();
                List<Dictionary<string, object?>> posts;
                try
                {
                    posts = await collector.GetBarPostsAsync(primary, page).ConfigureAwait(false);
                }
                catch (TiebaBlockedException ex)
                {
                    MarkBlocked(ex, $"吧「{primary}」第{page}页");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var message = $"吧「{primary}」第{page}页获取失败：{ex.GetType().Name}: " +
                                  HttpUtils.Trim(ex.Message, 60);
                    report.Errors.Add(message);
                    log("  " + message);
                    break;
                }

                var fresh = AddPosts(posts);
                log($"  吧「{primary}」第{page}页：{posts.Count} 条（新增 {fresh.Count}）");
                if (posts.Count < 10) break;
            }

            foreach (var word in variants)
            {
                if (blocked.Length > 0) break;
                ensureRunning();
                List<Dictionary<string, object?>> posts;
                try
                {
                    posts = await collector.SearchPostsAsync(word, 1).ConfigureAwait(false);
                }
                catch (TiebaBlockedException ex)
                {
                    MarkBlocked(ex, $"吧内关键词「{word}」");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var message = $"吧内关键词「{word}」检索失败：{ex.GetType().Name}: " +
                                  HttpUtils.Trim(ex.Message, 60);
                    report.Errors.Add(message);
                    log("  " + message);
                    continue;
                }

                var matched = posts.Where(p => TiebaCollector.BarMatches(Item.Str(p, "bar"), resolved))
                    .ToList();
                var fresh = AddPosts(matched);
                log($"  吧内关键词「{word}」：{posts.Count} 条 → 本吧 {matched.Count} 条" +
                    $"（新增 {fresh.Count}）");
            }
        }

        // ③' 未指定平台（或没找到吧）时的全站搜索回退
        if (!barMode && blocked.Length == 0)
        {
            foreach (var word in variants)
            {
                for (var page = 1; page <= Math.Max(1, pages); page++)
                {
                    if (blocked.Length > 0) break;
                    ensureRunning();
                    List<Dictionary<string, object?>> posts;
                    try
                    {
                        posts = await collector.SearchPostsAsync(word, page).ConfigureAwait(false);
                    }
                    catch (TiebaBlockedException ex)
                    {
                        MarkBlocked(ex, $"贴吧搜索「{word}」");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var message = $"贴吧搜索「{word}」失败：{ex.GetType().Name}: " +
                                      HttpUtils.Trim(ex.Message, 60);
                        report.Errors.Add(message);
                        log(message);
                        break;
                    }

                    var fresh = AddPosts(posts);
                    log($"  贴吧「{word}」第{page}页：{posts.Count} 条（新增 {fresh.Count}）");
                    if (posts.Count < 10) break;
                }
            }
        }

        report.TiebaPool = pool.Count;
        if (pool.Count == 0)
        {
            if (blocked.Length == 0) log("贴吧未召回任何帖子");
            if (blocked.Length > 0)
            {
                return new Dictionary<string, object?>
                {
                    ["posts"] = new List<Dictionary<string, object?>>(),
                    ["replies"] = new List<Dictionary<string, object?>>(),
                    ["total_posts"] = 0,
                    ["total_replies"] = 0,
                    ["pool_size"] = 0,
                    ["blocked"] = blocked,
                    ["forums"] = report.TiebaBars,
                    ["hot_topics"] = report.TiebaHotTopics,
                    ["hot_posts"] = report.TiebaHotPosts,
                    ["hot_note"] = report.TiebaHotNote,
                };
            }
            return null;
        }

        var ranked = Relevance.RankItems(pool, parsed, hotKey: "like", replyKey: "reply_count",
            hoursLimit: windowHours);
        var (kept, effective, note) = Relevance.ApplyTimeWindow(ranked, windowHours,
            dateField: "date", baseLabel: Relevance.WindowLabel(report.WindowSpec));
        report.TiebaKept = kept.Count;
        report.TimeNotes["贴吧"] = note;
        log($"贴吧本地筛选：{pool.Count} → 相关 {ranked.Count} → 时效：{note}");

        var targets = Relevance.SelectTargets(kept, deep);
        report.TiebaTargets.AddRange(targets);
        log(targets.Count > 0
            ? $"贴吧深挖目标（{targets.Count} 个）：" +
              string.Join("；", targets.Select(t => HttpUtils.Trim(Item.Str(t, "title"), 26)))
            : "贴吧无深挖目标");

        var replies = new List<Dictionary<string, object?>>();
        foreach (var post in targets)
        {
            ensureRunning();
            try
            {
                var detail = await collector.GetPostRepliesAsync(Item.Str(post, "id"), 1)
                    .ConfigureAwait(false);
                if (detail["replies"] is List<Dictionary<string, object?>> batch) replies.AddRange(batch);
            }
            catch (TiebaBlockedException ex)
            {
                if (blocked.Length == 0) blocked = ex.Message;
                log("  贴吧深挖楼层被风控拦截，停止继续");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"获取帖子《{HttpUtils.Trim(Item.Str(post, "title"), 18)}》回复失败：" +
                              HttpUtils.Trim(ex.Message, 50);
                report.Errors.Add(message);
                log("  " + message);
            }
        }

        var postsOut = kept.Select(Relevance.StripInternal).ToList();
        return new Dictionary<string, object?>
        {
            ["posts"] = postsOut,
            ["replies"] = replies,
            ["total_posts"] = postsOut.Count,
            ["total_replies"] = replies.Count,
            ["pool_size"] = report.TiebaPool,
            ["time_window_hours"] = effective,
            ["forums"] = report.TiebaBars,
            ["primary_forum"] = report.TiebaBars.Count > 0 ? report.TiebaBars[0] : "",
            ["hot_topics"] = report.TiebaHotTopics,
            ["hot_posts"] = report.TiebaHotPosts,
            ["hot_note"] = report.TiebaHotNote,
            ["blocked"] = blocked,
        };
    }
}
