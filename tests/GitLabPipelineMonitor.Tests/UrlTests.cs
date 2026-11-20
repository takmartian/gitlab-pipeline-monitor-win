using GitLabPipelineMonitor.Core;

namespace GitLabPipelineMonitor.Tests;

/// <summary>服务器地址规范化与链接安全。这些规则每一条都对应一个真实踩过的坑。</summary>
public static class UrlTests
{
    public static Task Run()
    {
        Check.Equal("https://gitlab.example.com", ServerAddress.Normalize("gitlab.example.com"), "缺 scheme 要补 https");
        Check.Equal("https://gitlab.example.com", ServerAddress.Normalize("  gitlab.example.com  "), "首尾空白要清掉");
        Check.Equal("https://gitlab.example.com", ServerAddress.Normalize("https://gitlab.example.com/"), "末尾斜杠要去掉");
        Check.Equal("https://gitlab.example.com", ServerAddress.Normalize("https://gitlab.example.com///"), "多个末尾斜杠都要去掉");

        // 很多人会把 API 地址一起粘进来，必须自愈，否则全部 404
        Check.Equal("https://gitlab.example.com", ServerAddress.Normalize("https://gitlab.example.com/api/v4"), "误粘 /api/v4 要去掉");
        Check.Equal("https://gitlab.example.com", ServerAddress.Normalize("https://gitlab.example.com/api/v4/"), "误粘 /api/v4/ 要去掉");

        Check.Equal("https://gitlab.example.com:8080", ServerAddress.Normalize("gitlab.example.com:8080"), "端口要保留");
        Check.Equal("http://192.168.1.10:8080", ServerAddress.Normalize("http://192.168.1.10:8080"), "显式 http 不该被改成 https");

        Check.IsNull(ServerAddress.Normalize(null), "null 返回 null");
        Check.IsNull(ServerAddress.Normalize(""), "空串返回 null");
        Check.IsNull(ServerAddress.Normalize("   "), "纯空白返回 null");
        Check.IsNull(ServerAddress.Normalize("https://"), "只有 scheme 没有主机名返回 null");

        // 令牌误粘进地址栏：glpat-xxx 没有点，不该被当成合法主机名
        Check.IsNull(ServerAddress.Normalize("glpat-abc123XYZ"), "令牌误粘进地址栏不算合法地址");

        Check.Equal("gitlab.example.com", ServerAddress.StrictAccount("https://gitlab.example.com") ?? "", "账户是主机名");
        // 账户只取主机名、不含端口：同一台机器换端口仍共用令牌（与 macOS 版一致）
        Check.Equal("gitlab.example.com", ServerAddress.StrictAccount("gitlab.example.com:8080") ?? "", "账户不含端口");
        Check.IsNull(ServerAddress.StrictAccount("gitlab"), "没有点的主机名不算账户（地址还没输完）");
        Check.IsNull(ServerAddress.StrictAccount("glpat-abc"), "令牌不算账户");
        Check.IsNull(ServerAddress.StrictAccount(null), "null 不算账户");
        Check.Equal("127.0.0.1", ServerAddress.StrictAccount("http://127.0.0.1:3000") ?? "", "IP 地址可以当账户");

        Check.Equal("https://x/api/v4/user", ServerAddress.ApiUrl("https://x/", "/user"), "API 地址拼接");
        Check.Equal("https://x/api/v4/projects?starred=true",
            ServerAddress.ApiUrl("https://x", "/projects", new[] { new KeyValuePair<string, string>("starred", "true") }),
            "查询参数拼接");

        // 分支名带斜杠和中文必须转义，否则路径会被拆错
        Check.Equal("feature%2Fabc", ServerAddress.Segment("feature/abc"), "片段里的斜杠要转义");
        Check.Contains("%E4%B8%AD", ServerAddress.Segment("中文"), "中文要转义");

        Program.Ok("服务器地址规范化");
        return Task.CompletedTask;
    }

    public static Task RunSafeLinks()
    {
        Check.Equal("https://gitlab.example.com/group/proj",
            ServerAddress.SafeBrowserUrl("https://gitlab.example.com/group/proj"), "完整链接原样通过");
        Check.Equal("https://gitlab.example.com/-/pipelines",
            ServerAddress.SafeBrowserUrl("/-/pipelines", "https://gitlab.example.com"), "相对路径用服务器地址补全");
        Check.Equal("https://gitlab.example.com/proj",
            ServerAddress.SafeBrowserUrl("gitlab.example.com/proj"), "缺 scheme 要补");

        // macOS 版踩过的坑：残缺链接交给系统会被当文件路径打开并报 -50
        Check.IsNull(ServerAddress.SafeBrowserUrl("/pipelines"), "没有服务器地址可补全时拒绝打开");
        Check.IsNull(ServerAddress.SafeBrowserUrl(""), "空链接拒绝");
        Check.IsNull(ServerAddress.SafeBrowserUrl(null), "null 拒绝");
        Check.IsNull(ServerAddress.SafeBrowserUrl("file:///etc/passwd"), "非 http(s) 协议一律拒绝");
        Check.IsNull(ServerAddress.SafeBrowserUrl("javascript:alert(1)"), "javascript: 协议必须拒绝");

        Program.Ok("链接安全");
        return Task.CompletedTask;
    }
}
