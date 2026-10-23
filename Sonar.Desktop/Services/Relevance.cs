using System.Globalization;
using System.Text.RegularExpressions;
using Sonar.Models;

namespace Sonar.Services;

/// <summary>
/// 关键词解析与相关性打分（Python relevance.py 的 C# 移植）。
/// 纯文本处理，不依赖任何平台代码，便于独立维护与复用。
/// </summary>
public static class Relevance
{
    public const string LabelSeparator = "+";
    public const int MaxVariants = 4;
    public const int MinPartLen = 2;
    public const double HotTopicBoost = 8.0;
    public const int MinWindowKeep = 3;
    public const int HoursPerDay = 24;
    public const int DaysPerMonth = 30;
    public const int DaysPerYear = 365;
    public const int MaxWidenHours = DaysPerYear * HoursPerDay;

    private static readonly string[] QualifierPatterns =
    {
        @"v\s*\d+(?:\s*改)?",
        @"[零一二三四五六七八九十两0-9]+\s*改",
    };

    private static readonly string[] VersionEquivalents =
    {
        "v4", "v 4", "v四", "v 四", "4改", "四改", "v4改", "v四改",
    };

    private static readonly string[] KnownPlatforms =
    {
        "原神", "崩坏星穹铁道", "崩坏3", "崩铁", "绝区零", "王者荣耀", "英雄联盟",
        "明日方舟", "鸣潮", "幻塔", "阴阳师", "第五人格", "和平精英", "蛋仔派对",
        "哔哩哔哩", "bilibili", "贴吧", "抖音", "小红书", "微博", "知乎",
    };

    private static readonly Regex QualifierRegex = new(
        string.Join("|", QualifierPatterns), RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex NormalizeRegex = new(@"[\s\-_·・]+", RegexOptions.Compiled);
    private static readonly Regex HrRegex = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

    public static readonly (string Key, string Unit)[] WindowUnits =
    {
        ("years", "年"), ("months", "月"), ("days", "天"), ("hours", "小时"),
    };

    public static readonly (string Label, WindowSpec Spec)[] QuickWindows =
    {
        ("不限", WindowSpec.Unlimited),
        ("24 小时", new WindowSpec { Days = 1 }),
        ("7 天", new WindowSpec { Days = 7 }),
        ("30 天", new WindowSpec { Days = 30 }),
        ("90 天", new WindowSpec { Days = 90 }),
        ("1 年", new WindowSpec { Years = 1 }),
    };

    // ----------------------------------------------------------------
    // 关键词解析
    // ----------------------------------------------------------------

    public sealed class KeywordInfo
    {
        public string Raw { get; set; } = "";
        public List<string> Platform { get; set; } = new();
        public string Core { get; set; } = "";
        public List<string> Qualifiers { get; set; } = new();
        public List<string> Variants { get; set; } = new();
        public bool Labeled { get; set; }
    }

    public static string ComposeKeyword(string? platform, string? keyword)
    {
        var p = (platform ?? "").Trim();
        var k = (keyword ?? "").Trim();
        if (p.Length == 0) return k;
        if (k.Length == 0) return p;
        return p + LabelSeparator + k;
    }

    public static KeywordInfo ParseKeyword(string? raw)
    {
        var text = (raw ?? "").Trim();
        var result = new KeywordInfo { Raw = text, Core = text };
        if (text.Length == 0) return result;

        if (text.Contains(LabelSeparator))
        {
            var parts = text.Split(LabelSeparator)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            result.Labeled = true;

            if (parts.Count >= 2)
            {
                result.Platform = parts.Take(parts.Count - 1).ToList();
                var head = parts[^1];
                var (core, quals) = StripVersion(head);
                result.Core = core;
                result.Qualifiers = quals.Count > 0 ? quals : ExtractQualifiers(head);

                var variants = new List<string> { head };
                for (var i = parts.Count - 1; i >= 1; i--)
                {
                    variants.Add(string.Concat(parts.Take(i)) + head);
                }
                variants.AddRange(parts.Take(parts.Count - 1));
                result.Variants = Dedup(variants);
                return result;
            }

            // 只有一个片段时退化为普通关键词
            text = parts.Count > 0 ? parts[0] : text;
            result.Raw = text;
        }

        var (heuristicCore, heuristicPlatform, heuristicQualifiers) = SplitHeuristic(text);
        result.Core = heuristicCore;
        result.Platform = heuristicPlatform;
        result.Qualifiers = heuristicQualifiers;
        result.Variants = BuildVariants(text, result.Core, result.Platform);
        return result;
    }

    private static List<string> ExtractQualifiers(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (Match match in QualifierRegex.Matches(text))
        {
            found.Add(match.Value);
        }
        return Dedup(found);
    }

    private static (string Core, List<string> Qualifiers) StripVersion(string? text)
    {
        var core = (text ?? "").Trim();
        var quals = ExtractQualifiers(core);
        if (quals.Count == 0) return (core, new List<string>());

        var stripped = core;
        foreach (var q in quals)
        {
            stripped = stripped.Replace(q, " ", StringComparison.Ordinal);
        }
        stripped = SpaceRegex.Replace(stripped, " ").Trim();
        return stripped.Length < MinPartLen ? (core, quals) : (stripped, quals);
    }

    private static (string Core, List<string> Platform, List<string> Qualifiers) SplitHeuristic(string raw)
    {
        var text = raw;
        var platform = new List<string>();

        foreach (var name in KnownPlatforms.OrderByDescending(n => n.Length))
        {
            if (name.Length > 0 && text.Contains(name, StringComparison.Ordinal) && text.Length > name.Length)
            {
                platform.Add(name);
                var index = text.IndexOf(name, StringComparison.Ordinal);
                text = text.Remove(index, name.Length);
                break;
            }
        }

        var (core, qualifiers) = StripVersion(text.Trim());
        core = core.Trim();
        if (core.Length == 0) core = raw;
        return (core, platform, qualifiers);
    }

    private static List<string> BuildVariants(string raw, string core, List<string> platform)
    {
        var variants = new List<string>();
        if (core.Length > 0) variants.Add(core);
        if (platform.Count > 0) variants.Add(string.Concat(platform) + core);
        if (core != raw) variants.Add(raw);
        variants.Add(core);
        if (platform.Count > 0) variants.Add(string.Concat(platform));

        var cleaned = new List<string>();
        foreach (var v in variants)
        {
            var value = (v ?? "").Trim();
            if (value.Length >= MinPartLen && !cleaned.Contains(value))
            {
                cleaned.Add(value);
            }
        }
        return cleaned.Take(MaxVariants).ToList();
    }

    private static List<string> Dedup(IEnumerable<string> items)
    {
        var result = new List<string>();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item) && !result.Contains(item))
            {
                result.Add(item);
            }
        }
        return result;
    }

    public static bool LooseHit(string? needle, string? haystack)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        var n = Normalize(needle);
        var h = Normalize(haystack ?? "");
        if (n.Length > 0 && h.Contains(n, StringComparison.Ordinal)) return true;

        if (n is "v4" or "v四" or "4改" or "四改" or "v4改" or "v四改")
        {
            return VersionEquivalents.Any(eq => h.Contains(Normalize(eq), StringComparison.Ordinal));
        }
        return false;
    }

    private static string Normalize(string text) =>
        NormalizeRegex.Replace(text, "").ToLowerInvariant();

    // ----------------------------------------------------------------
    // 相关性打分
    // ----------------------------------------------------------------

    public sealed class RelInfo
    {
        public double Score { get; set; }
        public bool CoreHit { get; set; }
        public bool TitleHit { get; set; }
        public bool BodyHit { get; set; }
        public bool QualHit { get; set; }
        public bool PlatformHit { get; set; }
        public bool HotHit { get; set; }
    }

    public sealed class HotMatch
    {
        public double Score { get; set; }
        public bool NameHit { get; set; }
        public bool CoreHit { get; set; }
        public bool QualHit { get; set; }
        public bool PlatformHit { get; set; }
    }

    public static RelInfo RelevanceOf(IReadOnlyDictionary<string, object?> item,
        KeywordInfo parsed, string[]? fields = null)
    {
        fields ??= new[] { "title", "content", "description", "tag" };
        var title = Item.Str(item, "title");
        var others = string.Join(" ", fields.Where(f => f != "title").Select(f => Item.Str(item, f)));
        var core = parsed.Core ?? "";
        var platform = parsed.Platform ?? new List<string>();
        var qualifiers = parsed.Qualifiers ?? new List<string>();

        var titleHit = core.Length > 0 && LooseHit(core, title);
        var bodyHit = core.Length > 0 && LooseHit(core, others);
        var coreHit = titleHit || bodyHit;
        var combined = title + " " + others;
        var qualHit = qualifiers.Any(q => LooseHit(q, combined));
        var platformHit = platform.Any(p => LooseHit(p, combined));
        if (platform.Count == 0 && parsed.Raw.Length > 0)
        {
            platformHit = LooseHit(parsed.Raw, combined);
        }
        var hotHit = Item.Str(item, "hot_topic").Length > 0;

        double score = 0;
        if (titleHit) score += 10;
        if (bodyHit) score += 4;
        if (qualHit) score += 6;
        if (platformHit) score += 1;
        if (hotHit) score += HotTopicBoost;
        if (titleHit && title.Length > 30)
        {
            score -= Math.Min(2.0, (title.Length - 30) / 40.0);
        }

        return new RelInfo
        {
            Score = Math.Max(0, score),
            CoreHit = coreHit,
            TitleHit = titleHit,
            BodyHit = bodyHit,
            QualHit = qualHit,
            PlatformHit = platformHit,
            HotHit = hotHit,
        };
    }

    public static bool IsRelevant(RelInfo info) =>
        info.HotHit || info.CoreHit || (info.QualHit && info.PlatformHit);

    public static HotMatch MatchHotTopic(IReadOnlyDictionary<string, object?>? topic, KeywordInfo parsed)
    {
        topic ??= new Dictionary<string, object?>();
        var name = Item.Str(topic, "name");
        var desc = Item.Str(topic, "desc");
        var text = name + " " + desc;
        var core = parsed.Core ?? "";
        var qualifiers = parsed.Qualifiers ?? new List<string>();
        var platforms = parsed.Platform ?? new List<string>();

        var nameHit = core.Length > 0 && LooseHit(core, name);
        var coreHit = core.Length > 0 && LooseHit(core, text);
        var qualHit = qualifiers.Any(q => LooseHit(q, text));
        var platformHit = platforms.Any(p => LooseHit(p, text));

        double score = 0;
        if (coreHit) score += 10;
        if (nameHit) score += 4;
        if (qualHit) score += 3;
        if (platformHit) score += 1;
        if (!coreHit && !(qualHit && platformHit)) score = 0;

        return new HotMatch
        {
            Score = score, NameHit = nameHit, CoreHit = coreHit,
            QualHit = qualHit, PlatformHit = platformHit,
        };
    }

    public static List<Dictionary<string, object?>> FilterRelevant(
        IEnumerable<Dictionary<string, object?>> items, KeywordInfo parsed, bool requireTitle = false)
    {
        var kept = new List<Dictionary<string, object?>>();
        foreach (var item in items)
        {
            var info = RelevanceOf(item, parsed);
            if (!IsRelevant(info)) continue;
            if (requireTitle && !info.TitleHit) continue;
            var copy = Item.Clone(item);
            copy["_rel"] = info;
            kept.Add(copy);
        }
        return kept;
    }

    // ----------------------------------------------------------------
    // 综合排序
    // ----------------------------------------------------------------

    public static List<Dictionary<string, object?>> RankItems(
        IEnumerable<Dictionary<string, object?>> items,
        KeywordInfo parsed,
        string? hotKey = "play",
        string replyKey = "reply_count",
        int? hoursLimit = null,
        DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var kept = new List<(Dictionary<string, object?> Item, RelInfo Rel, double Rank, int Index)>();
        var index = 0;
        foreach (var item in items)
        {
            var info = RelevanceOf(item, parsed);
            if (IsRelevant(info))
            {
                var copy = Item.Clone(item);
                copy["_rel"] = info;
                var rank = CompositeScore(copy, info, hotKey, replyKey, hoursLimit, current);
                copy["_rank"] = rank;
                kept.Add((copy, info, rank, index));
            }
            index++;
        }

        return kept
            .OrderByDescending(entry => Level(entry.Rel))
            .ThenByDescending(entry => entry.Rank)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
    }

    private static int Level(RelInfo info)
    {
        if (info.TitleHit) return info.QualHit ? 4 : 3;
        if (info.HotHit) return 3;
        return info.CoreHit ? 2 : 1;
    }

    private static double CompositeScore(
        Dictionary<string, object?> item, RelInfo info,
        string? hotKey, string replyKey, int? hoursLimit, DateTime now)
    {
        var score = info.Score * 4.0;

        var hot = string.IsNullOrEmpty(hotKey) ? 0 : Item.Num(item, hotKey);
        if (hot <= 0)
        {
            hot = Item.Num(item, "like") + Item.Num(item, "view_num");
        }
        if (hot > 0) score += Math.Min(15.0, Math.Log10(hot + 1) * 3.0);

        var reply = Item.Num(item, replyKey);
        if (reply > 0) score += Math.Min(15.0, Math.Log10(reply + 1) * 5.0);

        if (info.HotHit)
        {
            score += HotTopicBoost;
            var discuss = Item.Num(item, "hot_discuss");
            if (discuss > 0) score += Math.Min(8.0, Math.Log10(discuss + 1) * 2.0);
        }

        var ageHours = AgeHours(Item.Str(item, "date").Length > 0
            ? Item.Str(item, "date")
            : Item.Str(item, "pubdate"), now);
        if (ageHours.HasValue)
        {
            if (hoursLimit is > 0 && ageHours.Value > hoursLimit.Value)
            {
                score -= 25.0;
            }
            else
            {
                score += Math.Max(0.0, 12.0 - (ageHours.Value / 24.0) / 7.0);
            }
        }
        if (info.QualHit) score += 5.0;
        return Math.Round(score, 2, MidpointRounding.AwayFromZero);
    }

    public static List<Dictionary<string, object?>> SelectTargets(
        IReadOnlyList<Dictionary<string, object?>> ranked, int limit, string replyKey = "reply_count")
    {
        if (limit <= 0) return new List<Dictionary<string, object?>>();
        _ = replyKey;

        var tiers = new List<List<Dictionary<string, object?>>>
        {
            ranked.Where(it => RelOf(it).TitleHit && RelOf(it).QualHit).ToList(),
            ranked.Where(it => RelOf(it).TitleHit || RelOf(it).HotHit).ToList(),
            ranked.Where(it => RelOf(it).CoreHit).ToList(),
            ranked.ToList(),
        };

        var picked = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tier in tiers)
        {
            foreach (var item in tier)
            {
                var key = Item.Str(item, "id");
                if (key.Length == 0) key = Item.Str(item, "url");
                if (key.Length == 0) key = "obj:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
                if (!seen.Add(key)) continue;
                picked.Add(item);
                if (picked.Count >= limit) return picked;
            }
        }
        return picked.Take(limit).ToList();
    }

    private static RelInfo RelOf(Dictionary<string, object?> item) =>
        item.TryGetValue("_rel", out var value) && value is RelInfo info ? info : new RelInfo();

    public static Dictionary<string, object?> StripInternal(Dictionary<string, object?> item)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in item)
        {
            if (!key.StartsWith("_", StringComparison.Ordinal))
            {
                copy[key] = value;
            }
        }
        return copy;
    }

    // ----------------------------------------------------------------
    // 时效
    // ----------------------------------------------------------------

    public static int WindowHours(WindowSpec? spec)
    {
        var window = WindowSpec.Normalize(spec);
        return window.Years * DaysPerYear * HoursPerDay
               + window.Months * DaysPerMonth * HoursPerDay
               + window.Days * HoursPerDay
               + window.Hours;
    }

    public static string HoursLabel(int hours)
    {
        if (hours <= 0) return "不限时间";
        var years = hours / (DaysPerYear * HoursPerDay);
        var rest = hours % (DaysPerYear * HoursPerDay);
        var months = rest / (DaysPerMonth * HoursPerDay);
        rest %= DaysPerMonth * HoursPerDay;
        var days = rest / HoursPerDay;
        rest %= HoursPerDay;

        var parts = new List<string>();
        if (years > 0) parts.Add($"{years} 年");
        if (months > 0) parts.Add($"{months} 个月");
        if (days > 0) parts.Add($"{days} 天");
        if (rest > 0) parts.Add($"{rest} 小时");
        return "近 " + string.Join(" ", parts);
    }

    public static string WindowLabel(WindowSpec? spec)
    {
        var window = WindowSpec.Normalize(spec);
        if (WindowHours(window) == 0) return "不限时间";
        var parts = new List<string>();
        if (window.Years > 0) parts.Add($"{window.Years} 年");
        if (window.Months > 0) parts.Add($"{window.Months} 个月");
        if (window.Days > 0) parts.Add($"{window.Days} 天");
        if (window.Hours > 0) parts.Add($"{window.Hours} 小时");
        return "近 " + string.Join(" ", parts);
    }

    public static double? AgeHours(string? text, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var current = now ?? DateTime.Now;
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        };
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
            {
                return Math.Max(0.0, (current - dt).TotalHours);
            }
        }

        var match = Regex.Match(value, @"^(\d{4})-(\d{2})-(\d{2})");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var y) &&
            int.TryParse(match.Groups[2].Value, out var m) &&
            int.TryParse(match.Groups[3].Value, out var d))
        {
            try
            {
                return Math.Max(0.0, (current - new DateTime(y, m, d)).TotalHours);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public static (List<Dictionary<string, object?>> Kept, string Note) FilterByTime(
        IEnumerable<Dictionary<string, object?>> items, int hours,
        string dateField = "date", DateTime? now = null)
    {
        var source = items.ToList();
        if (hours <= 0) return (source, "");
        var current = now ?? DateTime.Now;
        var kept = source.Where(it =>
        {
            var date = Item.Str(it, dateField);
            if (date.Length == 0) date = Item.Str(it, "pubdate");
            var age = AgeHours(date, current);
            return age is null || age <= hours;
        }).ToList();
        return (kept, $"{HoursLabel(hours)}保留 {kept.Count}/{source.Count} 条");
    }

    public static (List<Dictionary<string, object?>> Kept, int? EffectiveHours, string Note) ApplyTimeWindow(
        IEnumerable<Dictionary<string, object?>> items, int hours,
        string dateField = "date", int minKeep = MinWindowKeep, DateTime? now = null,
        string baseLabel = "")
    {
        var source = items.ToList();
        if (hours <= 0) return (source, null, "时效：不限时间");

        var originalLabel = string.IsNullOrEmpty(baseLabel) ? HoursLabel(hours) : baseLabel;
        var ladder = new List<int> { hours };
        foreach (var factor in new[] { 2, 4, 8 })
        {
            var widened = Math.Min(MaxWidenHours, hours * factor);
            if (widened > hours && !ladder.Contains(widened)) ladder.Add(widened);
        }

        var fallbackKept = new List<Dictionary<string, object?>>(source);
        var fallbackHours = hours;
        foreach (var value in ladder)
        {
            var (kept, _) = FilterByTime(source, value, dateField, now);
            if (kept.Count >= minKeep)
            {
                var label = value == hours ? originalLabel : HoursLabel(value);
                var note = $"{label}，保留 {kept.Count}/{source.Count} 条";
                if (value != hours)
                {
                    note += $"（原设{originalLabel}不足 {minKeep} 条，已自动放宽）";
                }
                return (kept, value, note);
            }
            fallbackKept = kept;
            fallbackHours = value;
        }

        var finalNote = $"{HoursLabel(fallbackHours)}内仅 {fallbackKept.Count} 条" +
                        "（不足以放宽到更旧），已如实保留 —— 请注意数据可能偏旧";
        return (fallbackKept, fallbackHours, finalNote);
    }
}
