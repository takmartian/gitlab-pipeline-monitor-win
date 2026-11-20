using System.Text.Json;

namespace GitLabPipelineMonitor.Core.GitLab;

public sealed class GitLabProject
{
    public int Id { get; init; }
    public string Name { get; init; } = "project";
    public string PathWithNamespace { get; init; } = "project";
    public string WebUrl { get; init; } = "";
    public string? DefaultBranch { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public int StarCount { get; set; }
    public bool Archived { get; init; }

    /// <summary>是否已打星：由拉取来源标记，接口本身不给这个字段，所以是可变状态。</summary>
    public bool Starred { get; set; }

    /// <summary>命名空间部分（group/subgroup），用于副标题。</summary>
    public string Namespace
    {
        get
        {
            var index = PathWithNamespace.LastIndexOf('/');
            return index > 0 ? PathWithNamespace[..index] : "";
        }
    }

    /// <summary>路径最后一段，用于主标题。</summary>
    public string ShortPath
    {
        get
        {
            var index = PathWithNamespace.LastIndexOf('/');
            return index >= 0 ? PathWithNamespace[(index + 1)..] : PathWithNamespace;
        }
    }

    public static GitLabProject FromJson(JsonElement root, bool starred)
    {
        var name = GitLabJson.Str(root, "name") ?? "project";
        return new GitLabProject
        {
            Id = GitLabJson.Int(root, "id") ?? 0,
            Name = name,
            PathWithNamespace = GitLabJson.Str(root, "path_with_namespace") ?? name,
            WebUrl = GitLabJson.Str(root, "web_url") ?? "",
            DefaultBranch = GitLabJson.Str(root, "default_branch"),
            LastActivityAt = GitLabJson.Date(root, "last_activity_at"),
            StarCount = GitLabJson.Int(root, "star_count") ?? 0,
            Archived = GitLabJson.Bool(root, "archived") ?? false,
            Starred = starred
        };
    }

    /// <summary>复制一份。星标列表和参与列表在真实场景里来自两次独立请求、各自持有
    /// 自己的对象；本地改标记时若两边共享同一实例，星数会被加两次。</summary>
    public GitLabProject Clone() => new()
    {
        Id = Id,
        Name = Name,
        PathWithNamespace = PathWithNamespace,
        WebUrl = WebUrl,
        DefaultBranch = DefaultBranch,
        LastActivityAt = LastActivityAt,
        StarCount = StarCount,
        Archived = Archived,
        Starred = Starred
    };

    /// <summary>项目主页。web_url 万一没取到，用服务器地址 + 路径自己拼，
    /// 免得「在项目页打开」点开一个空字符串。</summary>
    public string ResolvedWebUrl(string? baseUrl)
    {
        var value = WebUrl.Trim();
        if (value.Length > 0) return value;
        var trimmed = baseUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) ? "" : trimmed + "/" + PathWithNamespace;
    }

    /// <summary>流水线列表页。GitLab 13 起内部页面挂在 /-/ 命名空间下。</summary>
    public string PipelinesUrl(string? baseUrl) => ResolvedWebUrl(baseUrl) + "/-/pipelines";

    public string PipelineUrl(Pipeline pipeline, string? baseUrl) => PipelinesUrl(baseUrl) + "/" + pipeline.Id;
}
