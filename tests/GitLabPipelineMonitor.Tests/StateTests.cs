using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Storage;

namespace GitLabPipelineMonitor.Tests;

/// <summary>状态编排：监控范围、汇总状态、打星后的本地即时反映。</summary>
public static class StateTests
{
    private static AppState MakeState(out AppSettings settings, bool onlyStarred = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glpm-state-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        settings = new AppSettings(new FakeHost(), dir)
        {
            ServerUrl = "https://gitlab.example.com",
            OnlyStarred = onlyStarred
        };
        settings.ApplyToken("glpat-test");
        return new AppState(settings, new FakeHost(), new FakeTransport());
    }

    private static GitLabProject Project(int id, string path, int minutesAgo, bool starred = true) => new()
    {
        Id = id,
        Name = path.Contains('/') ? path.Split('/')[^1] : path,
        PathWithNamespace = path,
        WebUrl = "https://gitlab.example.com/" + path,
        LastActivityAt = new DateTime(2026, 11, 20).AddMinutes(-minutesAgo),
        Starred = starred
    };

    private static Pipeline Pipe(int id, int projectId, PipelineStatus status, int minutesAgo) => new()
    {
        Id = id,
        Iid = id,
        ProjectId = projectId,
        Sha = "deadbeefcafe",
        Ref = "main",
        Status = status,
        UpdatedAt = new DateTime(2026, 11, 20).AddMinutes(-minutesAgo)
    };

    public static Task Run()
    {
        // 可用项目 = 星标 + 参与，去重后按活跃度排序
        var state = MakeState(out var settings);
        var a = Project(1, "web/alpha", 10);
        var b = Project(2, "web/beta", 30, starred: false);
        var c = Project(3, "infra/gamma", 5);
        state.LoadDemoData(new GitLabUser { Id = 1, Username = "u", Name = "U" }, "17.0",
            new[] { a, c }, new[] { a, b, c }, new Dictionary<int, IReadOnlyList<Pipeline>>());
        Check.Equal(3, state.AvailableProjects.Count, "合并去重后三个项目");
        Check.Equal(3, state.AvailableProjects[0].Id, "按活跃度排序，最活跃在前");

        // 仅监控星标：只看星标项目
        Check.Equal(2, state.MonitoredProjects.Count, "仅星标时监控范围是星标项目");

        // 关掉仅星标：按勾选走
        settings.OnlyStarred = false;
        settings.SetMonitoredProjectIds(new[] { 2, 3 });
        Check.Equal(2, state.MonitoredProjects.Count, "手动模式按勾选");
        Check.True(state.MonitoredProjects.Any(p => p.Id == 2), "勾了的参与项目在监控范围里");
        Check.False(state.MonitoredProjects.Any(p => p.Id == 1), "没勾的不在");

        // 汇总状态：有失败就是失败，图标要红
        var state2 = MakeState(out _);
        state2.LoadDemoData(new GitLabUser { Id = 1, Username = "u", Name = "U" }, "17.0",
            new[] { a, b }, new[] { a, b }, new Dictionary<int, IReadOnlyList<Pipeline>>
            {
                [1] = new[] { Pipe(10, 1, PipelineStatus.Failed, 5), Pipe(11, 1, PipelineStatus.Success, 60) },
                [2] = new[] { Pipe(20, 2, PipelineStatus.Success, 3) }
            });
        Check.Equal(AggregateStatus.Failed, state2.Aggregate, "有失败就是失败");
        Check.Equal(1, state2.FailingCount, "失败计数");
        Check.Equal(2, state2.LatestPerProject.Count, "每个项目取最新一条");
        Check.Equal(3, state2.AllVisiblePipelines.Count, "全部可见流水线");
        Check.Equal(10, state2.LatestPerProject.First(p => p.ProjectId == 1).Id, "取的是最新那条而不是最旧的");

        // 没有失败但有在跑的 → 蓝
        var state3 = MakeState(out _);
        state3.LoadDemoData(new GitLabUser { Id = 1, Username = "u", Name = "U" }, "17.0",
            new[] { a }, new[] { a }, new Dictionary<int, IReadOnlyList<Pipeline>>
            {
                [1] = new[] { Pipe(10, 1, PipelineStatus.Running, 5) }
            });
        Check.Equal(AggregateStatus.Running, state3.Aggregate, "有在跑的就是蓝");

        // 有监控项目但一条流水线都没有 → 不算「都通过了」
        var state4 = MakeState(out _);
        state4.LoadDemoData(new GitLabUser { Id = 1, Username = "u", Name = "U" }, "17.0",
            new[] { a }, new[] { a }, new Dictionary<int, IReadOnlyList<Pipeline>>());
        Check.Equal(AggregateStatus.None, state4.Aggregate, "没有流水线时不算通过");

        // 没配置也没有数据 → 无状态
        var state5 = MakeState(out var settings5);
        settings5.ServerUrl = "";
        settings5.ApplyToken("");
        Check.Equal(AggregateStatus.None, state5.Aggregate, "空状态");

        // 打星后本地立刻反映：星标列表、监控范围、星数都要动，不能等下一轮拉取
        // 真实场景里星标列表和参与列表来自两次独立请求，是各自的对象；
        // 这里也照真实构造，避免同一实例被两边各加一次
        var state6 = MakeState(out var settings6);
        var b2 = new GitLabProject { Id = 2, Name = "beta", PathWithNamespace = "web/beta", StarCount = 3 };
        state6.LoadDemoData(new GitLabUser { Id = 1, Username = "u", Name = "U" }, "17.0",
            new[] { a }, new[] { a, b2 }, new Dictionary<int, IReadOnlyList<Pipeline>>());
        state6.ApplyStar(2, starred: true);
        Check.True(state6.StarredProjects.Any(p => p.Id == 2), "打星后立刻进星标列表");
        Check.Equal(4, state6.MemberProjects.First(p => p.Id == 2).StarCount, "参与列表里的星数 +1");
        state6.ApplyStar(1, starred: false);
        Check.False(state6.StarredProjects.Any(p => p.Id == 1), "取消打星后离开星标列表");
        Check.Equal(0, state6.MemberProjects.First(p => p.Id == 1).StarCount, "星数不会被减成负数");

        // 项目归属查找
        Check.Equal(1, state2.ProjectFor(Pipe(10, 1, PipelineStatus.Failed, 5))?.Id ?? 0, "能按流水线找到项目");
        Check.IsNull(state2.ProjectFor(Pipe(10, 999, PipelineStatus.Failed, 5)), "找不到时给 null 而不是崩");

        Program.Ok("状态编排");
        return Task.CompletedTask;
    }
}
