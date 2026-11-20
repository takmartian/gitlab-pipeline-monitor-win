using System.Text.Json;

namespace GitLabPipelineMonitor.Core.GitLab;

public sealed class Job
{
    public int Id { get; init; }
    public string Name { get; init; } = "job";
    public string Stage { get; init; } = "default";
    public PipelineStatus Status { get; init; } = PipelineStatus.Unknown;
    public string? Ref { get; init; }
    public double? Duration { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? WebUrl { get; init; }
    public string? FailureReason { get; init; }
    public bool? AllowFailure { get; init; }

    public static Job FromJson(JsonElement root)
        => new()
        {
            Id = GitLabJson.Int(root, "id") ?? 0,
            Name = GitLabJson.Str(root, "name") ?? "job",
            Stage = GitLabJson.Str(root, "stage") ?? "default",
            Status = PipelineStatusExtensions.Parse(GitLabJson.Str(root, "status")),
            Ref = GitLabJson.Str(root, "ref"),
            Duration = GitLabJson.Double(root, "duration"),
            StartedAt = GitLabJson.Date(root, "started_at"),
            FinishedAt = GitLabJson.Date(root, "finished_at"),
            WebUrl = GitLabJson.Str(root, "web_url"),
            FailureReason = GitLabJson.Str(root, "failure_reason"),
            AllowFailure = GitLabJson.Bool(root, "allow_failure")
        };

    /// <summary>作业日志页；接口没给 web_url 时用项目地址自己拼。</summary>
    public string ResolvedWebUrl(string? baseUrl, GitLabProject? project)
    {
        var value = WebUrl?.Trim() ?? "";
        if (value.Length > 0) return value;
        if (project is null) return "";
        return project.ResolvedWebUrl(baseUrl) + "/-/jobs/" + Id;
    }
}

public sealed class Commit
{
    public string Id { get; init; } = "";
    public string? ShortId { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string? AuthorName { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string? WebUrl { get; init; }

    public static Commit FromJson(JsonElement root)
        => new()
        {
            Id = GitLabJson.Str(root, "id") ?? "",
            ShortId = GitLabJson.Str(root, "short_id"),
            Title = GitLabJson.Str(root, "title"),
            Message = GitLabJson.Str(root, "message"),
            AuthorName = GitLabJson.Str(root, "author_name"),
            CreatedAt = GitLabJson.Date(root, "created_at"),
            WebUrl = GitLabJson.Str(root, "web_url")
        };
}

public sealed class Branch
{
    public string Name { get; init; } = "";
    public bool? Merged { get; init; }

    /// <summary>GitLab 在分支对象里给的默认分支标记字段名是 default。</summary>
    public bool? Default { get; init; }

    public static Branch FromJson(JsonElement root)
        => new()
        {
            Name = GitLabJson.Str(root, "name") ?? "",
            Merged = GitLabJson.Bool(root, "merged"),
            Default = GitLabJson.Bool(root, "default")
        };
}

public sealed class GitLabVersion
{
    public string? Version { get; init; }
    public string? Revision { get; init; }

    public static GitLabVersion FromJson(JsonElement root)
        => new()
        {
            Version = GitLabJson.Str(root, "version"),
            Revision = GitLabJson.Str(root, "revision")
        };
}
