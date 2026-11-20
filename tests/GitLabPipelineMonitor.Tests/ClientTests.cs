using GitLabPipelineMonitor.Core.GitLab;

namespace GitLabPipelineMonitor.Tests;

/// <summary>API 客户端：认证头、分页、错误分类、端点兼容，全部用假传输验证，不打网络。</summary>
public static class ClientTests
{
    private const string Base = "https://gitlab.example.com";

    private static GitLabClient Client(FakeTransport transport, GitLabAuthMode mode = GitLabAuthMode.Pat)
        => new(Base, "test-token", mode, transport);

    public static async Task Run()
    {
        // 认证头：两种令牌类型走不同的头
        var t1 = new FakeTransport().On("/user", _ => FakeTransport.Json("{\"id\":1,\"username\":\"a\",\"name\":\"A\"}"));
        await Client(t1).CurrentUserAsync();
        Check.Equal("test-token", t1.Requests[0].Token ?? "", "令牌传进来了");
        Check.Equal(GitLabAuthMode.Pat, t1.Requests[0].Mode, "默认 PAT");
        var t2 = new FakeTransport().On("/user", _ => FakeTransport.Json("{\"id\":1,\"username\":\"a\",\"name\":\"A\"}"));
        await Client(t2, GitLabAuthMode.Oauth).CurrentUserAsync();
        Check.Equal(GitLabAuthMode.Oauth, t2.Requests[0].Mode, "OAuth 模式");

        // URL：必须带 /api/v4 前缀，且基础地址末尾斜杠不会造成双斜杠
        Check.Contains("/api/v4/user", t1.Requests[0].Url, "带 API 前缀");
        Check.False(t1.Requests[0].Url.Contains("/api/v4//"), "不出现双斜杠");
        var t3 = new FakeTransport().On("/user", _ => FakeTransport.Json("{\"id\":1}"));
        await new GitLabClient("https://gitlab.example.com/", "x", GitLabAuthMode.Pat, t3).CurrentUserAsync();
        Check.False(t3.Requests[0].Url.Contains("//api"), "基础地址带斜杠也不出现双斜杠");

        // 打星项目的查询参数
        var t4 = new FakeTransport().On("/projects", _ => FakeTransport.Json("[]"));
        await Client(t4).StarredProjectsAsync();
        Check.Contains("starred=true", t4.Requests[0].Url, "starred 参数");
        Check.Contains("archived=false", t4.Requests[0].Url, "排除归档项目");
        Check.Contains("order_by=last_activity_at", t4.Requests[0].Url, "按活跃度排序");

        // 分页：有 X-Next-Page 就继续翻，没有就停
        var t5 = new FakeTransport().On("/projects", req =>
            req.Url.Contains("page=2")
                ? FakeTransport.Json("[{\"id\":2,\"name\":\"b\"}]", nextPage: "")
                : FakeTransport.Json("[{\"id\":1,\"name\":\"a\"}]", nextPage: "2"));
        var projects = await Client(t5).StarredProjectsAsync();
        Check.Equal(2, projects.Count, "翻页把第二页也收进来了");
        Check.Equal(2, t5.Requests.Count, "确实发了两次请求");

        // 翻到 maxPages 要停，不能无限翻
        var t6 = new FakeTransport().On("/projects", _ => FakeTransport.Json("[{\"id\":1}]", nextPage: "99"));
        await Client(t6).StarredProjectsAsync();
        Check.Equal(5, t6.Requests.Count, "最多翻 5 页就停");

        // 流水线：limit 夹在 1..40，且结果补上 project_id
        var t7 = new FakeTransport().On("/pipelines", _ => FakeTransport.Json("[{\"id\":9,\"iid\":1,\"sha\":\"a\",\"ref\":\"main\",\"status\":\"success\"}]"));
        var pipelines = await Client(t7).PipelinesAsync(42, limit: 999);
        Check.Contains("per_page=40", t7.Requests[0].Url, "limit 上限夹到 40");
        Check.Equal(42, pipelines[0].ProjectId ?? 0, "列表结果补上 project_id");
        var t8 = new FakeTransport().On("/pipelines", _ => FakeTransport.Json("[]"));
        await Client(t8).PipelinesAsync(1, limit: 0);
        Check.Contains("per_page=1", t8.Requests[0].Url, "limit 下限夹到 1");

        // 取消打星的端点兼容：DELETE /star 返回 404 时自动退回 POST /unstar
        // （实测自建 GitLab 19.5.3 上 DELETE 那个路由根本没注册，返回纯文本 404）
        var t9 = new FakeTransport()
            .On("DELETE", _ => FakeTransport.Json("404 Not Found", status: 404))
            .On("/unstar", _ => FakeTransport.Json("{\"id\":1}", status: 201));
        await Client(t9).UnstarAsync(7);
        Check.Equal(2, t9.Requests.Count, "先试 DELETE 再退回 POST");
        Check.Equal("DELETE", t9.Requests[0].Method, "第一发是 DELETE");
        Check.Contains("/unstar", t9.Requests[1].Url, "第二发退回 unstar");

        // DELETE 成功时不该多发一枪
        var t10 = new FakeTransport().On("/star", _ => FakeTransport.Json("{\"id\":1}", status: 200));
        await new GitLabClient(Base, "x", GitLabAuthMode.Pat, t10).UnstarAsync(7);
        Check.Equal(1, t10.Requests.Count, "DELETE 成功就不再退回 unstar");

        // 错误分类
        await CheckStatus(401, GitLabErrorKind.Unauthorized, "401 → 令牌无效");
        await CheckStatus(403, GitLabErrorKind.Forbidden, "403 → 没有权限");
        await CheckStatus(404, GitLabErrorKind.NotFound, "404 → 项目不存在");
        await CheckStatus(429, GitLabErrorKind.RateLimited, "429 → 请求过频");
        await CheckStatus(500, GitLabErrorKind.Server, "500 → 服务器错误");
        await CheckStatus(502, GitLabErrorKind.Server, "502 → 服务器错误");

        // 地址填成网页（比如填了 GitLab 首页的 /explore）会返回 HTML
        var t11 = new FakeTransport().On("/user", _ => new RawResponse
        {
            Status = 200,
            Body = "<html><body>GitLab</body></html>",
            ContentType = "text/html"
        });
        var html = await Check.Throws<GitLabException>(() => Client(t11).CurrentUserAsync(), "HTML 响应");
        Check.Equal(GitLabErrorKind.Html, html.Kind, "返回网页要认出来说清楚");

        // 数组里混进坏元素：跳过它，其余照常出来（整页打不开是最糟的体验）
        var t12 = new FakeTransport().On("/pipelines", _ => FakeTransport.Json(
            "[{\"id\":1,\"iid\":1,\"sha\":\"a\",\"ref\":\"main\",\"status\":\"success\"},\"垃圾\",null,{\"id\":2,\"iid\":2,\"sha\":\"b\",\"ref\":\"dev\",\"status\":\"failed\"}]"));
        var mixed = await Client(t12).PipelinesAsync(1, 5);
        Check.Equal(2, mixed.Count, "坏元素被跳过，好记录保留");
        Check.Equal(2, mixed[1].Id, "第二条是好的那条");

        // 响应根本不是 JSON
        var t13 = new FakeTransport().On("/user", _ => FakeTransport.Json("不是 JSON"));
        var bad = await Check.Throws<GitLabException>(() => Client(t13).CurrentUserAsync(), "非 JSON 响应");
        Check.Equal(GitLabErrorKind.Decoding, bad.Kind, "解析失败分类正确");

        // 诊断入口不抛异常，把状态码和原话一起返回
        var t14 = new FakeTransport().On("/x", _ => FakeTransport.Json("{\"message\":\" nope\"}", status: 403));
        var raw = await Client(t14).RawRequestAsync("/x");
        Check.Equal(403, raw.Status, "诊断入口给出状态码");
        Check.Contains("nope", raw.Body, "诊断入口给出服务器原话");

        Program.Ok("API 客户端");
    }

    private static async Task CheckStatus(int status, GitLabErrorKind expected, string what)
    {
        var transport = new FakeTransport().On("/user", _ => FakeTransport.Json("{\"message\":\"x\"}", status: status));
        var error = await Check.Throws<GitLabException>(() => Client(transport).CurrentUserAsync(), what);
        Check.Equal(expected, error.Kind, what);
        Check.Equal(status, error.StatusCode, what + " 状态码");
    }
}
