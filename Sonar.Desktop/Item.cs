using System.Globalization;
using System.Text.Json;

namespace Sonar;

/// <summary>采集结果统一用 Dictionary&lt;string, object?&gt; 表示，这里集中做容错取值。</summary>
public static class Item
{
    public static string Str(IReadOnlyDictionary<string, object?>? item, string key)
    {
        if (item is null || !item.TryGetValue(key, out var value) || value is null) return "";
        return value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    public static long Long(IReadOnlyDictionary<string, object?>? item, string key, long fallback = 0)
    {
        if (item is null || !item.TryGetValue(key, out var value) || value is null) return fallback;
        try
        {
            return value switch
            {
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                double d => (long)d,
                float f => (long)f,
                decimal m => (long)m,
                bool b => b ? 1 : 0,
                string s => long.TryParse(s.Trim(), out var parsed) ? parsed : fallback,
                JsonElement element when element.ValueKind == JsonValueKind.Number =>
                    element.TryGetInt64(out var l) ? l : (long)element.GetDouble(),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    public static double Num(IReadOnlyDictionary<string, object?>? item, string key, double fallback = 0)
    {
        if (item is null || !item.TryGetValue(key, out var value) || value is null) return fallback;
        try
        {
            return value switch
            {
                double d => d,
                float f => f,
                decimal m => (double)m,
                long l => l,
                int i => i,
                bool b => b ? 1 : 0,
                string s => double.TryParse(s.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback,
                JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetDouble(),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    public static bool Has(IReadOnlyDictionary<string, object?>? item, string key) =>
        item is not null && item.TryGetValue(key, out var value) && value is not null &&
        (value is not string s || s.Length > 0);

    public static Dictionary<string, object?> Clone(IReadOnlyDictionary<string, object?> item)
    {
        var copy = new Dictionary<string, object?>(item.Count, StringComparer.Ordinal);
        foreach (var (key, value) in item)
        {
            copy[key] = value;
        }
        return copy;
    }

    public static Dictionary<string, object?> New() => new(StringComparer.Ordinal);

    public static List<object?> ListOf(IEnumerable<object?> items) => items.ToList();
}
