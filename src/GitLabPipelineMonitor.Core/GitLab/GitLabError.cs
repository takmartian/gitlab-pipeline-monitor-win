using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Core.GitLab;

public enum GitLabErrorKind
{
    NotConfigured,
    InvalidUrl,
    Html,
    Network,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    Server,
    Decoding,
    Canceled,
    Http
}

/// <summary>GitLab 请求的错误。Detail 是服务器返回的原始信息，设置页要拿它排查问题。</summary>
public sealed class GitLabException : Exception
{
    public GitLabErrorKind Kind { get; }
    public string? Detail { get; }
    public int StatusCode { get; }

    public GitLabException(GitLabErrorKind kind, string? detail = null, int statusCode = 0)
        : base(BuildMessage(kind, detail))
    {
        Kind = kind;
        Detail = detail;
        StatusCode = statusCode;
    }

    private static string BuildMessage(GitLabErrorKind kind, string? detail)
    {
        var baseMessage = kind switch
        {
            GitLabErrorKind.NotConfigured => Loc.T("未配置服务器地址或令牌"),
            GitLabErrorKind.InvalidUrl => Loc.T("服务器地址无效，请填写形如 https://gitlab.example.com 的地址"),
            GitLabErrorKind.Html => Loc.T("服务器返回的是网页而不是 API，请检查地址是否指向 GitLab 根地址"),
            GitLabErrorKind.Network => Loc.T("无法连接服务器，请检查地址、网络或 VPN"),
            GitLabErrorKind.Unauthorized => Loc.T("令牌无效或已过期（401）"),
            GitLabErrorKind.Forbidden => Loc.T("没有权限（403）"),
            GitLabErrorKind.NotFound => Loc.T("项目不存在或已被删除（404）"),
            GitLabErrorKind.RateLimited => Loc.T("请求过于频繁（429），请加大刷新间隔"),
            GitLabErrorKind.Server => Loc.T("服务器错误"),
            GitLabErrorKind.Decoding => Loc.T("数据解析失败，可能不是 GitLab 服务器"),
            GitLabErrorKind.Canceled => Loc.T("请求已取消"),
            _ => $"HTTP {detail}"
        };
        return string.IsNullOrEmpty(detail) || kind == GitLabErrorKind.Http ? baseMessage : $"{baseMessage}：{detail}";
    }

    /// <summary>从 GitLab 的错误响应里尽量捞出人话：message / error / errors / error_description。</summary>
    public static string? ServerMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.String) return root.GetString();
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            foreach (var key in new[] { "message", "error", "error_description" })
            {
                if (!root.TryGetProperty(key, out var value)) continue;
                switch (value.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String:
                        var text = value.GetString();
                        if (!string.IsNullOrEmpty(text)) return text;
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        var items = value.EnumerateArray()
                            .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s));
                        var joined = string.Join(", ", items);
                        if (joined.Length > 0) return joined;
                        break;
                    case System.Text.Json.JsonValueKind.Object:
                        var parts = value.EnumerateObject()
                            .Select(p => $"{p.Name}: {DescribeValue(p.Value)}").ToList();
                        if (parts.Count > 0) return string.Join(", ", parts);
                        break;
                }
            }
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var parts = errors.EnumerateObject()
                    .Select(p => $"{p.Name}: {DescribeValue(p.Value)}").ToList();
                if (parts.Count > 0) return string.Join(", ", parts);
            }
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            // 错误响应本身不是 JSON（比如网关的 HTML 错误页）：没有可捞的信息
            return null;
        }
    }

    private static string DescribeValue(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
        System.Text.Json.JsonValueKind.Array => string.Join(", ",
            value.EnumerateArray().Select(DescribeValue)),
        _ => value.ToString()
    };
}
