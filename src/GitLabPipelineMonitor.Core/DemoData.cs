using GitLabPipelineMonitor.Core.GitLab;

namespace GitLabPipelineMonitor.Core;

/// <summary>演示数据：--demo 启动时填充，用于在没有令牌的情况下预览界面。</summary>
public static class DemoData
{
    public static void Apply(AppState state)
    {
        var now = DateTime.Now;

        state.DemoHost = "gitlab.example.com";

        GitLabProject Project(int id, string path, string branch, int minutes, bool starred = true)
            => new()
            {
                Id = id,
                Name = path.Contains('/') ? path.Split('/')[^1] : path,
                PathWithNamespace = path,
                WebUrl = "https://gitlab.example.com/" + path,
                DefaultBranch = branch,
                LastActivityAt = now.AddMinutes(-minutes),
                StarCount = 3,
                Archived = false,
                Starred = starred
            };

        Pipeline Make(int id, int projectId, int iid, string reference, string sha,
            PipelineStatus status, int minutesAgo, double? duration, string user) => new()
        {
            Id = id,
            Iid = iid,
            ProjectId = projectId,
            Sha = sha,
            Ref = reference,
            Status = status,
            Source = "push",
            CreatedAt = now.AddMinutes(-minutesAgo),
            StartedAt = now.AddMinutes(-minutesAgo),
            UpdatedAt = now.AddMinutes(-minutesAgo),
            Duration = duration,
            Coverage = status == PipelineStatus.Success ? "87.2" : null,
            User = new GitLabUser { Id = 2, Username = user, Name = user }
        };

        var projects = new[]
        {
            Project(101, "components/poll-app", "main", 4),
            Project(102, "web/customer-admin", "develop", 12),
            Project(103, "web/customer-survey", "main", 31),
            Project(104, "infra/k8s-cluster", "staging", 88),
            Project(105, "design/prototype-docs", "main", 260)
        };

        state.LoadDemoData(
            new GitLabUser { Id = 1, Username = "demo.user", Name = "演示用户" }, "17.5.1-ee", projects,
            projects.Concat(new[] { Project(106, "platform/api-gateway", "main", 420, false) }).ToList(),
            new Dictionary<int, IReadOnlyList<Pipeline>>
            {
                [101] = new[]
                {
                    Make(9001, 101, 482, "main", "a1b2c3d4e5f6", PipelineStatus.Running, 4, null, "demo.user"),
                    Make(9002, 101, 481, "main", "9f8e7d6c", PipelineStatus.Success, 55, 412, "ci-bot"),
                    Make(9003, 101, 480, "feature/order-form", "1a2b3c4d", PipelineStatus.Failed, 130, 208, "demo.li")
                },
                [102] = new[]
                {
                    Make(9011, 102, 1291, "develop", "55aa66bb77cc", PipelineStatus.Failed, 12, 96, "demo.user"),
                    Make(9012, 102, 1290, "feature/module-editor", "deedbeef", PipelineStatus.Success, 70, 505, "demo.li"),
                    Make(9013, 102, 1289, "develop", "00112233", PipelineStatus.Success, 190, 480, "ci-bot")
                },
                [103] = new[]
                {
                    Make(9021, 103, 77, "main", "cafe1234", PipelineStatus.Success, 31, 233, "demo.user"),
                    Make(9022, 103, 76, "main", "badd1ca7", PipelineStatus.Canceled, 210, 41, "demo.wu")
                },
                [104] = new[]
                {
                    Make(9031, 104, 315, "staging", "feedface", PipelineStatus.Pending, 1, null, "ci-bot"),
                    Make(9032, 104, 314, "staging", "beefc0de", PipelineStatus.Success, 95, 1204, "ci-bot")
                },
                [105] = new[]
                {
                    Make(9041, 105, 8, "main", "12345678", PipelineStatus.Manual, 260, 12, "demo.bot")
                }
            });
    }

    /// <summary>演示作业：按流水线状态给一套合理的阶段/作业组合。</summary>
    public static IReadOnlyList<Job> Jobs(Pipeline pipeline, GitLabProject project)
    {
        var baseTime = DateTime.Now;
        Job Make(int id, string stage, string name, PipelineStatus status, int minutes,
            double? duration, string? reason = null, bool? allow = null) => new()
        {
            Id = id, Name = name, Stage = stage, Status = status, Ref = pipeline.Ref, Duration = duration,
            StartedAt = baseTime.AddMinutes(-minutes),
            FinishedAt = status.IsActive() ? null : baseTime.AddMinutes(-Math.Max(minutes - 1, 0)),
            WebUrl = project.WebUrl + "/-/jobs/" + id,
            FailureReason = reason,
            AllowFailure = allow
        };

        return pipeline.Status switch
        {
            PipelineStatus.Running => new Job[]
            {
                Make(1, "install", "dependencies", PipelineStatus.Success, 12, 96),
                Make(2, "build", "compile", PipelineStatus.Success, 9, 214),
                Make(3, "test", "unit", PipelineStatus.Running, 3, null),
                Make(4, "test", "e2e", PipelineStatus.Created, 3, null),
                Make(5, "deploy", "staging", PipelineStatus.Created, 3, null)
            },
            PipelineStatus.Failed => new Job[]
            {
                Make(6, "install", "dependencies", PipelineStatus.Success, 20, 88),
                Make(7, "build", "compile", PipelineStatus.Success, 16, 205),
                Make(8, "test", "unit", PipelineStatus.Failed, 12, 173, reason: "执行脚本失败 exit code 1，2 个用例未通过"),
                Make(9, "test", "lint", PipelineStatus.Success, 12, 41),
                Make(10, "deploy", "staging", PipelineStatus.Canceled, 11, null, allow: true)
            },
            PipelineStatus.Success => new Job[]
            {
                Make(11, "install", "dependencies", PipelineStatus.Success, 60, 90),
                Make(12, "build", "compile", PipelineStatus.Success, 56, 198),
                Make(13, "test", "unit", PipelineStatus.Success, 50, 320),
                Make(14, "deploy", "staging", PipelineStatus.Success, 44, 122)
            },
            PipelineStatus.Canceled => new Job[]
            {
                Make(15, "install", "dependencies", PipelineStatus.Success, 200, 84),
                Make(16, "build", "compile", PipelineStatus.Canceled, 196, 61)
            },
            PipelineStatus.Pending or PipelineStatus.Scheduled or PipelineStatus.Created
                or PipelineStatus.WaitingForResource or PipelineStatus.Preparation => new Job[]
            {
                Make(17, "install", "dependencies", PipelineStatus.Pending, 1, null),
                Make(18, "build", "compile", PipelineStatus.Created, 1, null)
            },
            _ => new[] { Make(19, "test", "unit", pipeline.Status, 30, 60) }
        };
    }

    public static Commit CommitFor(Pipeline pipeline) => new()
    {
        Id = pipeline.Sha,
        ShortId = pipeline.ShortSha,
        Title = "feat: 支持字段语义标签与档案同步",
        Message = "feat: 支持字段语义标签与档案同步",
        AuthorName = pipeline.User?.DisplayName ?? "演示用户",
        CreatedAt = pipeline.CreatedAt
    };
}
