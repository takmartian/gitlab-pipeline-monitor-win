using System.Text.Json;

namespace GitLabPipelineMonitor.Core.GitLab;

public sealed class Pipeline
{
    public int Id { get; init; }
    public int Iid { get; init; }

    /// <summary>列表接口不返回 project_id，归属关系在加载时补齐。</summary>
    public int? ProjectId { get; set; }

    public string? Name { get; set; }
    public string Sha { get; init; } = "";
    public string Ref { get; init; } = "";
    public PipelineStatus Status { get; init; } = PipelineStatus.Unknown;
    public string? Source { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? UpdatedAt { get; init; }
    public double? Duration { get; set; }

    /// <summary>覆盖率。GitLab 的字段名是 coverage。</summary>
    public string? Coverage { get; set; }

    public GitLabUser? User { get; set; }

    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;

    // 下面这几个是给界面直接绑定的派生值。放在 Core 里是因为格式化本身就在 Core，
    // 而且这样 XAML 可以直接绑模型，不必再套一层只转发属性的包装类。

    /// <summary>相对时间（面板行右侧与元信息都要用）。</summary>
    public string RelativeTime => Fmt.Relative(SortDate);

    /// <summary>格式化后的耗时。</summary>
    public string DurationText => Fmt.Duration(Duration);

    public bool HasDuration => Duration.HasValue;

    public bool HasCoverage => !string.IsNullOrEmpty(Coverage);

    public bool HasUser => User is not null;

    public string UserName => User?.Username ?? "";

    /// <summary>状态文字（行右侧平时显示的就是它）。</summary>
    public string StatusText => Status.Label();

    /// <summary>整行的悬停提示：项目 · 分支 · 状态。</summary>
    public string TooltipFor(string projectPath) => $"{projectPath} · {Ref} · {Status.Label()}";

    /// <summary>排序用的时间：列表里最可信的是 updated_at，逐层退回。</summary>
    public DateTime SortDate => UpdatedAt ?? CreatedAt ?? StartedAt ?? FinishedAt ?? DateTime.MinValue;

    public static Pipeline FromJson(JsonElement root)
    {
        GitLabUser? user = null;
        if (GitLabJson.Object(root, "user") is { } userElement)
            user = GitLabUser.FromJson(userElement);
        return new Pipeline
        {
            Id = GitLabJson.Int(root, "id") ?? 0,
            Iid = GitLabJson.Int(root, "iid") ?? 0,
            ProjectId = GitLabJson.Int(root, "project_id"),
            Name = GitLabJson.Str(root, "name"),
            Sha = GitLabJson.Str(root, "sha") ?? "",
            Ref = GitLabJson.Str(root, "ref") ?? "",
            Status = PipelineStatusExtensions.Parse(GitLabJson.Str(root, "status")),
            Source = GitLabJson.Str(root, "source"),
            CreatedAt = GitLabJson.Date(root, "created_at"),
            StartedAt = GitLabJson.Date(root, "started_at"),
            FinishedAt = GitLabJson.Date(root, "finished_at"),
            UpdatedAt = GitLabJson.Date(root, "updated_at"),
            Duration = GitLabJson.Double(root, "duration"),
            Coverage = GitLabJson.Str(root, "coverage"),
            User = user
        };
    }

    /// <summary>列表接口不返回 user / duration / coverage，用详情接口的结果补齐。
    /// 只补空字段：status 和时间以列表为准，免得详情那边读到旧值反而把状态说旧。</summary>
    public void MergeDetail(Pipeline detail)
    {
        User ??= detail.User;
        Duration ??= detail.Duration;
        if (string.IsNullOrEmpty(Coverage)) Coverage = detail.Coverage;
        StartedAt ??= detail.StartedAt;
        FinishedAt ??= detail.FinishedAt;
        if (string.IsNullOrEmpty(Name)) Name = detail.Name;
    }
}
