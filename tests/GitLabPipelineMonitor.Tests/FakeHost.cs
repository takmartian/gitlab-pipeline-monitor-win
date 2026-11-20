using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Platform;

namespace GitLabPipelineMonitor.Tests;

/// <summary>假宿主：把平台能力换成可断言的内存实现。</summary>
public sealed class FakeHost : IHostServices
{
    public string MachineFingerprint { get; set; } = "test-machine-guid";
    public string UserName { get; set; } = "tester";
    public List<string> OpenedUrls { get; } = new();
    public List<string> Clipboard { get; } = new();
    public bool NotificationsAvailable { get; set; } = true;
    public List<(string Title, string Body, string Id)> Notifications { get; } = new();
    public bool LaunchAtLogin { get; set; }

    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public void CopyToClipboard(string text) => Clipboard.Add(text);
    public void Notify(string title, string body, string id) => Notifications.Add((title, body, id));
}

/// <summary>假传输：按 URL 后缀返回预置响应，并记录所有请求。</summary>
public sealed class FakeTransport : IGitLabTransport
{
    public List<(string Url, string Method, string? Token, GitLabAuthMode Mode, string? Body)> Requests { get; } = new();

    /// <summary>URL 包含该关键字时返回的响应；按登记顺序取第一个匹配的。</summary>
    private readonly List<(string Match, Func<(string Url, string? Body), RawResponse> Reply)> _routes = new();

    public FakeTransport On(string match, Func<(string Url, string? Body), RawResponse> reply)
    {
        _routes.Add((match, reply));
        return this;
    }

    public Task<RawResponse> SendAsync(string url, string method, string? token, GitLabAuthMode authMode,
        string? requestBody, CancellationToken cancellationToken)
    {
        Requests.Add((url, method, token, authMode, requestBody));
        // 匹配串可以带方法前缀（如 "DELETE"），也可以只写 URL 片段
        var probe = method + " " + url;
        foreach (var (match, reply) in _routes)
            if (probe.Contains(match, StringComparison.Ordinal))
                return Task.FromResult(reply((url, requestBody)));
        throw new InvalidOperationException($"假传输没有登记这个请求：{method} {url}");
    }

    public static RawResponse Json(string body, string nextPage = "", int status = 200)
        => new() { Status = status, Body = body, ContentType = "application/json", NextPage = nextPage };
}
