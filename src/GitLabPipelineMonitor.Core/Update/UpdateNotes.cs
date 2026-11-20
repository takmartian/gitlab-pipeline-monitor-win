using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Core.Update;

/// <summary>
/// release body 是 markdown，直接塞进纯文本控件会露出符号。这里粗略清一遍：
/// 去掉井号标题、代码围栏、反引号、加粗、删除线、水平分隔线、引用符号，
/// 列表统一成中点，链接展开成「文字 (地址)」。
/// 清理规则与 macOS 版完全一致 —— 两端看到的内容应当一样。</summary>
public static class UpdateNotes
{
    public static string Plain(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Loc.T("这个版本没有写更新说明。");
        var lines = new List<string>();
        var inFence = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            // 分隔线（--- / *** / ___）：markdown 里是横线，纯文本里只是几个字符，去掉
            if (IsHorizontalRule(line)) continue;

            if (line.Length == 0)
            {
                // 连续空行压成一个
                if (lines.Count == 0 || lines[^1].Length > 0) lines.Add("");
                continue;
            }

            while (line.StartsWith('>')) line = line[1..].Trim();
            while (line.StartsWith('#')) line = line[1..].Trim();

            var item = ListItem(line);
            if (item is not null) line = "· " + item;

            line = line.Replace("`", "").Replace("**", "").Replace("~~", "");
            line = FlattenLink(line);
            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? Loc.T("这个版本没有写更新说明。") : string.Join("\n", lines);
    }

    /// <summary>整行都是 - / * / _（至少 3 个）时是 markdown 的水平分隔线。</summary>
    public static bool IsHorizontalRule(string line)
    {
        var compact = line.Replace(" ", "");
        return compact.Length >= 3 && compact.All(c => c == '-' || c == '*' || c == '_');
    }

    /// <summary>列表项正文："- x" / "* x" / "+ x" / "1. x" → "x"，不是列表返回 null。</summary>
    public static string? ListItem(string line)
    {
        foreach (var prefix in new[] { "- ", "* ", "+ " })
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        var space = line.IndexOf(' ');
        if (space <= 0) return null;
        var head = line[..space];
        if (!head.EndsWith('.') || head.Length < 2) return null;
        var digits = head[..^1];
        return digits.All(char.IsAsciiDigit) ? line[(space + 1)..] : null;
    }

    /// <summary>[文字](地址) → 文字 (地址)。</summary>
    public static string FlattenLink(string line)
    {
        if (!line.Contains("](")) return line;
        var builder = new System.Text.StringBuilder();
        var index = 0;
        while (index < line.Length)
        {
            var open = line.IndexOf('[', index);
            if (open < 0) { builder.Append(line[index..]); break; }
            var close = line.IndexOf(']', open);
            if (close < 0 || close + 1 >= line.Length || line[close + 1] != '(')
            {
                builder.Append(line, index, open - index + 1);
                index = open + 1;
                continue;
            }
            var parenEnd = line.IndexOf(')', close + 1);
            if (parenEnd < 0) { builder.Append(line[index..]); break; }
            builder.Append(line, index, open - index);
            builder.Append(line, open + 1, close - open - 1);
            builder.Append(" (").Append(line, close + 2, parenEnd - close - 2).Append(')');
            index = parenEnd + 1;
        }
        return builder.ToString();
    }
}
