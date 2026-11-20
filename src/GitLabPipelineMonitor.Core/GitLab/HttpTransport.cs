namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>默认传输实现：HttpClient，带认证头、超时、禁用缓存。</summary>
public sealed class HttpTransport : IGitLabTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpTransport(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            // 令牌在头里，不让重定向把它带到别的域去
            ServerCertificateCustomValidationCallback = null
        };
        _client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        // 令牌变了要立刻生效，不能让缓存把旧响应端回来
        _client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
    }

    public async Task<RawResponse> SendAsync(string url, string method, string? token, GitLabAuthMode authMode,
        string? requestBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (!string.IsNullOrEmpty(token))
        {
            if (authMode == GitLabAuthMode.Oauth) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            else request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        }
        if (requestBody is not null)
            request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var nextPage = "";
            if (response.Headers.TryGetValues("X-Next-Page", out var values)) nextPage = values.FirstOrDefault() ?? "";
            return new RawResponse
            {
                Status = (int)response.StatusCode,
                Body = body,
                ContentType = contentType,
                NextPage = nextPage.Trim()
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时也是 TaskCanceledException，这里归为网络问题而不是「用户取消」
            throw new GitLabException(GitLabErrorKind.Network, "timeout");
        }
        catch (HttpRequestException error)
        {
            throw new GitLabException(GitLabErrorKind.Network, error.Message);
        }
    }

    public void Dispose() => _client.Dispose();
}
