namespace GitLabPipelineMonitor.Core;

/// <summary>
/// 服务器地址的规范化与链接安全。规则与 macOS 版逐条对齐。
/// 这些逻辑看起来简单，但每一条都对应一个真实踩过的坑，所以单独成类、单独测。</summary>
public static class ServerAddress
{
    /// <summary>
    /// 规范化用户填的地址：补 https、去掉末尾斜杠、去掉误粘进来的 /api/v4。
    /// 空串或没有主机名时返回 null。</summary>
    public static string? Normalize(string? raw)
    {
        var value = raw?.Trim() ?? "";
        if (value.Length == 0) return null;
        if (!value.Contains("://")) value = "https://" + value;
        value = value.TrimEnd('/');
        // 很多人会把 API 地址一起粘进来，这里自愈，免得 404
        while (value.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/api/v4".Length];
            value = value.TrimEnd('/');
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        // 只有主机名没有点、也不是 IP/端口的，基本是还没输完
        if (!uri.Host.Contains('.') && !uri.Host.Contains(':') && !IsIpHost(uri.Host)) return null;
        return value;
    }

    private static bool IsIpHost(string host) => System.Net.IPAddress.TryParse(host, out _);

    /// <summary>
    /// 令牌存储的账户名：只有像样的主机名才算。把令牌误粘进地址栏（glpat-xxx）
    /// 或地址还没输完时返回 null —— 免得凭空多出一条垃圾条目，
    /// 也免得来回改地址时把令牌清成空。</summary>
    public static string? StrictAccount(string? rawUrl)
    {
        var normalized = Normalize(rawUrl);
        if (normalized is null) return null;
        var host = new Uri(normalized).Host;
        return host.Contains('.') || host.Contains(':') ? host : null;
    }

    /// <summary>显示用的主机名（带端口）。</summary>
    public static string? HostOf(string? rawUrl) => Fmt.Host(Normalize(rawUrl));

    /// <summary>
    /// 把任意字符串整理成可以安全交给浏览器的链接：补 scheme、用服务器地址补全相对路径、
    /// 拒绝任何非 http(s) 的结果。macOS 版这里踩过坑：残缺链接（比如 web_url 没取到时拼出的
    /// "/pipelines"）也是合法 URL，交给系统会被当文件路径打开并报错。</summary>
    public static string? SafeBrowserUrl(string? raw, string? baseUrl = null)
    {
        var value = raw?.Trim() ?? "";
        if (value.Length == 0) value = baseUrl?.Trim() ?? "";
        if (value.Length == 0) return null;
        if (value.StartsWith('/')) value = (baseUrl?.TrimEnd('/') ?? "") + value;
        if (!value.Contains("://")) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return uri.ToString();
    }

    /// <summary>拼 API 地址：{base}/api/v4{path}。</summary>
    public static string ApiUrl(string baseUrl, string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        var url = baseUrl.TrimEnd('/') + "/api/v4" + path;
        var pairs = query?.ToList();
        if (pairs is { Count: > 0 })
            url += "?" + string.Join("&", pairs.Select(p =>
                Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return url;
    }

    /// <summary>路径片段转义：分支名可能带 / 和中文，必须逐段转义。</summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);
}
