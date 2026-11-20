using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>
/// GitLab REST API v4 的薄封装，只做这个工具需要的几件事：
/// 读用户、读项目、读流水线/作业、重试/取消/新建流水线。
/// 接口通过 IGitLabTransport 注入，测试里可以塞假响应，不打网络。</summary>
public interface IGitLabTransport
{
    Task<RawResponse> SendAsync(string url, string method, string? token, GitLabAuthMode authMode,
        string? requestBody, CancellationToken cancellationToken);
}

/// <summary>一次 HTTP 往返的结果（只保留判断需要的部分）。</summary>
public sealed record RawResponse
{
    public required int Status { get; init; }
    public required string Body { get; init; }
    public string ContentType { get; init; } = "";
    public string NextPage { get; init; } = "";
}

public sealed class GitLabClient
{
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly GitLabAuthMode _authMode;
    private readonly IGitLabTransport _transport;

    public GitLabClient(string baseUrl, string token, GitLabAuthMode authMode, IGitLabTransport transport)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _authMode = authMode;
        _transport = transport;
    }

    private Task<RawResponse> SendAsync(string path, string method = "GET",
        IEnumerable<KeyValuePair<string, string>>? query = null, string? requestBody = null,
        CancellationToken cancellationToken = default)
    {
        var url = ServerAddress.ApiUrl(_baseUrl, path, query);
        return _transport.SendAsync(url, method, _token, _authMode, requestBody, cancellationToken);
    }

    /// <summary>把状态码翻译成带人话说明的异常。</summary>
    private static GitLabException Fail(RawResponse response)
    {
        var detail = GitLabException.ServerMessage(response.Body) ?? $"HTTP {response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return response.Status switch
        {
            401 => new GitLabException(GitLabErrorKind.Unauthorized, GitLabException.ServerMessage(response.Body), 401),
            403 => new GitLabException(GitLabErrorKind.Forbidden, GitLabException.ServerMessage(response.Body), 403),
            404 => new GitLabException(GitLabErrorKind.NotFound, GitLabException.ServerMessage(response.Body), 404),
            429 => new GitLabException(GitLabErrorKind.RateLimited, GitLabException.ServerMessage(response.Body), 429),
            >= 500 and <= 599 => new GitLabException(GitLabErrorKind.Server, detail, response.Status),
            _ => new GitLabException(GitLabErrorKind.Http,
                response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture), response.Status)
        };
    }

    private static void EnsureJson(RawResponse response)
    {
        if (response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new GitLabException(GitLabErrorKind.Html, statusCode: response.Status);
    }

    /// <summary>取单个对象。解析失败按「不是 GitLab」处理。</summary>
    private async Task<T> GetObjectAsync<T>(string path, Func<JsonElement, T> parse,
        IEnumerable<KeyValuePair<string, string>>? query = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(path, query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Status is < 200 or >= 300) throw Fail(response);
        EnsureJson(response);
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            return parse(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new GitLabException(GitLabErrorKind.Decoding, statusCode: response.Status);
        }
    }

    /// <summary>
    /// 取列表并按 X-Next-Page 翻页。逐元素解析：某条记录字段奇怪就跳过它，
    /// 而不是让整页打不开 —— 和 macOS 版「宁可少一个字段」的取向一致。</summary>
    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path,
        IEnumerable<KeyValuePair<string, string>> query, Func<JsonElement, T> parse, int maxPages,
        CancellationToken cancellationToken = default)
    {
        var pairs = query.ToList();
        var all = new List<T>();
        var page = "1";
        for (var index = 0; index < maxPages; index++)
        {
            var existing = pairs.FindIndex(p => p.Key == "page");
            if (existing >= 0) pairs[existing] = new KeyValuePair<string, string>("page", page);
            else pairs.Add(new KeyValuePair<string, string>("page", page));

            var response = await SendAsync(path, query: pairs, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.Status is < 200 or >= 300) throw Fail(response);
            EnsureJson(response);

            int added;
            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new GitLabException(GitLabErrorKind.Decoding, statusCode: response.Status);
                var before = all.Count;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    try { all.Add(parse(item)); }
                    catch (Exception) { /* 单条解析失败：丢掉这一条，保住整页 */ }
                }
                added = all.Count - before;
            }
            catch (JsonException)
            {
                throw new GitLabException(GitLabErrorKind.Decoding, statusCode: response.Status);
            }

            // 没有下一页标记，或者这一页一条都没解析出来，就别再翻（否则会一直翻到 maxPages）
            if (string.IsNullOrWhiteSpace(response.NextPage) || added == 0) break;
            page = response.NextPage;
        }
        return all;
    }

    private async Task PostAsync(string path, IEnumerable<KeyValuePair<string, string>>? query = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(path, "POST", query, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Status is < 200 or >= 300) throw Fail(response);
    }
    // MARK: - 接口

    public Task<GitLabUser> CurrentUserAsync(CancellationToken cancellationToken = default)
        => GetObjectAsync("/user", GitLabUser.FromJson, cancellationToken: cancellationToken);

    public Task<GitLabVersion> VersionAsync(CancellationToken cancellationToken = default)
        => GetObjectAsync("/version", GitLabVersion.FromJson, cancellationToken: cancellationToken);

    /// <summary>打星项目。</summary>
    public async Task<IReadOnlyList<GitLabProject>> StarredProjectsAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetListAsync("/projects", new[] {
            Kv("starred", "true"), Kv("order_by", "last_activity_at"),
            Kv("per_page", "100"), Kv("archived", "false")
        }, root => GitLabProject.FromJson(root, true), 5, cancellationToken).ConfigureAwait(false);
        return list;
    }

    /// <summary>我参与的项目（用于手动勾选未打星的项目、以及连接测试里报项目数）。</summary>
    public Task<IReadOnlyList<GitLabProject>> MemberProjectsAsync(CancellationToken cancellationToken = default)
        => GetListAsync("/projects", new[] {
            Kv("membership", "true"), Kv("order_by", "last_activity_at"),
            Kv("per_page", "100"), Kv("archived", "false")
        }, root => GitLabProject.FromJson(root, false), 5, cancellationToken);

    public async Task<IReadOnlyList<Pipeline>> PipelinesAsync(int projectId, int limit, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Max(1, Math.Min(limit, 40));
        var list = await GetListAsync($"/projects/{projectId}/pipelines", new[] { Kv("per_page", clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            root =>
            {
                var pipeline = Pipeline.FromJson(root);
                // 列表接口不返回 project_id，归属在这里补齐
                pipeline.ProjectId = projectId;
                return pipeline;
            }, 1, cancellationToken).ConfigureAwait(false);
        return list;
    }

    /// <summary>单条流水线详情。列表接口不返回 user / duration / coverage，只有这里才有。</summary>
    public Task<Pipeline> PipelineDetailAsync(int projectId, int pipelineId, CancellationToken cancellationToken = default)
        => GetObjectAsync($"/projects/{projectId}/pipelines/{pipelineId}", Pipeline.FromJson, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<Job>> JobsAsync(int projectId, int pipelineId, CancellationToken cancellationToken = default)
        => GetListAsync($"/projects/{projectId}/pipelines/{pipelineId}/jobs", new[] { Kv("per_page", "100") },
            Job.FromJson, 3, cancellationToken);

    public Task<Commit> CommitAsync(int projectId, string sha, CancellationToken cancellationToken = default)
        => GetObjectAsync($"/projects/{projectId}/repository/commits/{ServerAddress.Segment(sha)}", Commit.FromJson,
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<Branch>> BranchesAsync(int projectId, CancellationToken cancellationToken = default)
        => GetListAsync($"/projects/{projectId}/repository/branches", new[] {
            Kv("per_page", "100"), Kv("order_by", "updated_at")
        }, Branch.FromJson, 2, cancellationToken);

    public Task StarAsync(int projectId, CancellationToken cancellationToken = default)
        => PostAsync($"/projects/{projectId}/star", cancellationToken: cancellationToken);

    /// <summary>
    /// 取消打星。这个端点在不同 GitLab 上不一致：官方文档写的是 DELETE /projects/:id/star，
    /// 但实测自建实例（GitLab 19.5.3）上该路由根本没注册 —— DELETE 返回的是纯文本 404，
    /// 而 POST /projects/:id/unstar 返回 201。所以先按官方写法试，拿 404 就自动退回旧端点，
    /// 两类实例都能用。（这条经验来自 macOS 版的实测。）</summary>
    public async Task UnstarAsync(int projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            await PostDeleteAsync($"/projects/{projectId}/star", cancellationToken).ConfigureAwait(false);
        }
        catch (GitLabException error) when (error.Kind == GitLabErrorKind.NotFound)
        {
            await PostAsync($"/projects/{projectId}/unstar", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PostDeleteAsync(string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(path, "DELETE", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Status is < 200 or >= 300) throw Fail(response);
    }

    public Task RetryPipelineAsync(int projectId, int pipelineId, CancellationToken cancellationToken = default)
        => PostAsync($"/projects/{projectId}/pipelines/{pipelineId}/retry", cancellationToken: cancellationToken);

    public Task CancelPipelineAsync(int projectId, int pipelineId, CancellationToken cancellationToken = default)
        => PostAsync($"/projects/{projectId}/pipelines/{pipelineId}/cancel", cancellationToken: cancellationToken);

    public Task CreatePipelineAsync(int projectId, string reference, CancellationToken cancellationToken = default)
        => PostAsync($"/projects/{projectId}/pipeline", new[] { Kv("ref", reference) }, cancellationToken);

    public Task RetryJobAsync(int projectId, int jobId, CancellationToken cancellationToken = default)
        => PostAsync($"/projects/{projectId}/jobs/{jobId}/retry", cancellationToken: cancellationToken);

    public Task CancelJobAsync(int projectId, int jobId, CancellationToken cancellationToken = default)
        => PostAsync($"/projects/{projectId}/jobs/{jobId}/cancel", cancellationToken: cancellationToken);

    /// <summary>诊断用：直接发一个请求，把状态码和服务器原话一起返回（不抛错）。</summary>
    public async Task<(int Status, string Body)> RawRequestAsync(string path, string method = "GET")
    {
        try
        {
            var response = await SendAsync(path, method).ConfigureAwait(false);
            return (response.Status, response.Body);
        }
        catch (GitLabException error)
        {
            var code = error.Kind switch
            {
                GitLabErrorKind.Unauthorized => 401,
                GitLabErrorKind.Forbidden => 403,
                GitLabErrorKind.NotFound => 404,
                GitLabErrorKind.RateLimited => 429,
                GitLabErrorKind.Server => 500,
                GitLabErrorKind.Http => error.StatusCode,
                _ => 0
            };
            return (code, (error.Detail ?? "") + " ⟨" + error.Message + "⟩");
        }
        catch (Exception error)
        {
            return (0, error.Message);
        }
    }

    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);
}
