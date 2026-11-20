using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Core;

/// <summary>通用格式化：相对时间、时长、时钟、主机名。
/// 相对时间自己实现而不用库：Core 要保持零依赖，而 .NET 自带的
/// 相对时间格式化只在部分 ICU 环境下可用，行为还跟平台不一致。</summary>
public static class Fmt
{
    /// <summary>相对时间（"3 分钟前" / "3 minutes ago"），跟随当前语言。
    /// 阈值与 macOS 版 RelativeDateTimeFormatter 的 short 风格对齐。</summary>
    public static string Relative(DateTime? date, DateTime? now = null)
    {
        if (date is not { } value) return "—";
        // 用「现在减过去」：过去为正、未来为负。写反会把所有历史时间显示成「即将」。
        var delta = ((now ?? DateTime.Now) - value).TotalSeconds;
        var future = delta < 0;
        var abs = Math.Abs(delta);

        // 昨天单独处理：两种语言里它都不是「数字 + 单位」的形式。
        // 只在 1.5～2.5 天之间算昨天，再久就按天数走（阈值对齐 macOS 版的行为）
        if (abs >= 36 * 3600 && abs < 60 * 3600 && !future) return Loc.T("昨天");

        (long N, string Unit) = abs switch
        {
            < 45 => (Math.Max(0, (long)abs), "秒前"),
            < 90 => (1, "分钟前"),
            < 45 * 60 => ((long)(abs / 60), "分钟前"),
            < 90 * 60 => (1, "小时前"),
            < 22 * 3600 => ((long)(abs / 3600), "小时前"),
            < 7 * 86400 => ((long)(abs / 86400), "天前"),
            < 30 * 86400 => ((long)(abs / (7 * 86400)), "周前"),
            < 365 * 86400 => ((long)(abs / (30 * 86400)), "个月前"),
            _ => ((long)(abs / (365 * 86400)), "年前")
        };

        // 英文要单复数和时态。表里存的是 "minutes ago" 这种完整短语，
        // 先把 " ago" 剥出来说成名词，再按时态拼：过去是 "5 minutes ago"，未来是 "in 5 minutes"。
        var unit = Loc.T(Unit);
        if (Loc.IsEnglish)
        {
            var noun = unit.EndsWith(" ago", StringComparison.Ordinal) ? unit[..^4] : unit;
            if (N == 1 && noun.EndsWith('s')) noun = noun[..^1];
            return future ? $"in {N} {noun}" : $"{N} {noun} ago";
        }
        return future ? "即将" : $"{N} {unit}";
    }

    /// <summary>时钟 HH:mm:ss。用固定区域：中文/英文系统下都输出同一形态。</summary>
    public static string Clock(DateTime? date)
        => date?.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "—";

    /// <summary>秒 → "1h 12m" / "3m 12s" / "45s"。</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not { } value || value < 0) return "—";
        var total = (long)Math.Round(value);
        if (total < 60) return $"{total}s";
        var m = total / 60;
        var s = total % 60;
        if (m < 60) return s == 0 ? $"{m}m" : $"{m}m {s}s";
        var h = m / 60;
        var mm = m % 60;
        return mm == 0 ? $"{h}h" : $"{h}h {mm}m";
    }

    /// <summary>主机名（带端口）。</summary>
    public static string Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "—";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "—";
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}
