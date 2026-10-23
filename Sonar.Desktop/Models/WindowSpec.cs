using System.Globalization;
using System.Text.Json.Nodes;

namespace Sonar.Models;

/// <summary>时效窗口：年 / 月 / 天 / 小时，四项全 0 表示不限。</summary>
public sealed class WindowSpec
{
    public int Years { get; set; }
    public int Months { get; set; }
    public int Days { get; set; }
    public int Hours { get; set; }

    public static WindowSpec Default => new() { Days = 90 };

    public static WindowSpec Unlimited => new();

    public WindowSpec Clone() => new()
    {
        Years = Years, Months = Months, Days = Days, Hours = Hours,
    };

    public static (int Min, int Max) Limits(string key) => key switch
    {
        "years" => (0, 10),
        "months" => (0, 23),
        "days" => (0, 365),
        "hours" => (0, 23),
        _ => (0, 0),
    };

    public static WindowSpec Normalize(WindowSpec? spec)
    {
        spec ??= Default;
        return new WindowSpec
        {
            Years = Math.Clamp(spec.Years, 0, 10),
            Months = Math.Clamp(spec.Months, 0, 23),
            Days = Math.Clamp(spec.Days, 0, 365),
            Hours = Math.Clamp(spec.Hours, 0, 23),
        };
    }

    /// <summary>兼容三种形态：对象 / 旧格式天数（数字或数字字符串）/ null。</summary>
    public static WindowSpec Parse(JsonNode? node)
    {
        if (node is null) return Default;
        if (node is JsonObject obj)
        {
            return Normalize(new WindowSpec
            {
                Years = JsonUtil.ToInt(obj["years"], 0),
                Months = JsonUtil.ToInt(obj["months"], 0),
                Days = JsonUtil.ToInt(obj["days"], 0),
                Hours = JsonUtil.ToInt(obj["hours"], 0),
            });
        }

        double days;
        try
        {
            days = node.GetValue<double>();
        }
        catch
        {
            var text = node.ToJsonString().Trim('"');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out days))
                return Default;
        }

        if (days <= 0) return Unlimited;
        var result = new WindowSpec();
        if (days > 365)
        {
            result.Years = (int)Math.Min(10, Math.Floor(days / 365));
            result.Days = (int)(days % 365);
        }
        else
        {
            result.Days = (int)days;
        }
        return Normalize(result);
    }

    public static WindowSpec ParseDays(int days)
    {
        if (days <= 0) return Unlimited;
        var node = JsonValue.Create(days);
        return Parse(node);
    }

    public JsonObject ToJson() => new()
    {
        ["years"] = Years,
        ["months"] = Months,
        ["days"] = Days,
        ["hours"] = Hours,
    };

    public override string ToString() =>
        $"y{Years} m{Months} d{Days} h{Hours}";
}
