namespace GitLabPipelineMonitor.Core.Update;

/// <summary>版本号比较：按数字分段，1.1.10 比 1.1.9 新。</summary>
public static class AppVersion
{
    /// <summary>去掉 v 前缀和首尾空白。</summary>
    public static string Normalize(string? raw)
    {
        var value = raw?.Trim() ?? "";
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        return value.Trim();
    }

    /// <summary>解析成数字段。非数字后缀（1.2.3-beta）在比较时忽略。</summary>
    public static IReadOnlyList<long> Parse(string? raw)
    {
        var text = Normalize(raw);
        if (text.Length == 0) return Array.Empty<long>();
        var parts = text.Split('.', '-', '+');
        var result = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            // 取该段开头的数字部分："3-beta" → 3，"beta" → 停止
            var digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits])) digits++;
            if (digits == 0) break;
            result.Add(long.Parse(part[..digits], System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }

    /// <summary>candidate 是否比 current 新。缺位的段按 0 算（1.2 比 1.2.0 相等，比 1.1.9 新）。</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        if (a.Count == 0) return false;
        var count = Math.Max(a.Count, b.Count);
        for (var index = 0; index < count; index++)
        {
            var left = index < a.Count ? a[index] : 0;
            var right = index < b.Count ? b[index] : 0;
            if (left != right) return left > right;
        }
        return false;
    }
}
