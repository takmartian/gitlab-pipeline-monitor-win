using GitLabPipelineMonitor.Core.GitLab;

namespace GitLabPipelineMonitor.Tests;

/// <summary>GitLab 的错误响应形态很杂，要把里面能看懂的信息捞出来给用户。</summary>
public static class ErrorTests
{
    public static Task Run()
    {
        Check.Equal("项目不存在", GitLabException.ServerMessage("{\"message\":\"项目不存在\"}") ?? "", "message 字符串");
        Check.Equal("head is not null", GitLabException.ServerMessage("{\"error\":\"head is not null\"}") ?? "", "error 字段");
        Check.Equal("invalid ref", GitLabException.ServerMessage("{\"error_description\":\"invalid ref\"}") ?? "", "error_description");
        Check.Equal("a, b", GitLabException.ServerMessage("{\"message\":[\"a\",\"b\"]}") ?? "", "message 是数组");
        Check.Equal("ref: 无效", GitLabException.ServerMessage("{\"message\":{\"ref\":\"无效\"}}") ?? "", "message 是对象");
        Check.Equal("name: 已存在", GitLabException.ServerMessage("{\"errors\":{\"name\":\"已存在\"}}") ?? "", "errors 对象");
        Check.Equal("纯文本", GitLabException.ServerMessage("\"纯文本\"") ?? "", "响应本身就是字符串");
        Check.IsNull(GitLabException.ServerMessage("不是 JSON"), "非 JSON 给 null 而不是抛");
        Check.IsNull(GitLabException.ServerMessage(null), "null 给 null");
        Check.IsNull(GitLabException.ServerMessage("{\"other\":1}"), "没有可捞的字段给 null");

        // 错误消息要带人话，还要把服务器原话附上（设置页排查靠它）
        var unauthorized = new GitLabException(GitLabErrorKind.Unauthorized, "bad token", 401);
        Check.Contains("令牌无效或已过期（401）", unauthorized.Message, "401 有人话");
        Check.Contains("bad token", unauthorized.Message, "服务器原话要带上");

        var plain = new GitLabException(GitLabErrorKind.Network);
        Check.Equal("无法连接服务器，请检查地址、网络或 VPN", plain.Message, "网络错误消息");

        var http = new GitLabException(GitLabErrorKind.Http, "418", 418);
        Check.Contains("418", http.Message, "其它状态码至少报出数字");

        Program.Ok("错误信息提取");
        return Task.CompletedTask;
    }
}
