using GitLabPipelineMonitor.Core.GitLab;

namespace GitLabPipelineMonitor.Tests;

public static class StatusTests
{
    public static Task Run()
    {
        // 「在跑」的判定直接决定图标颜色和取消按钮是否出现
        Check.True(PipelineStatus.Running.IsActive(), "running 是在跑");
        Check.True(PipelineStatus.Pending.IsActive(), "pending 是在跑");
        Check.True(PipelineStatus.Scheduled.IsActive(), "scheduled 是在跑");
        Check.True(PipelineStatus.Created.IsActive(), "created 是在跑");
        Check.True(PipelineStatus.WaitingForResource.IsActive(), "waiting_for_resource 是在跑");
        Check.True(PipelineStatus.Preparation.IsActive(), "preparation 是在跑");
        Check.False(PipelineStatus.Success.IsActive(), "success 不在跑");
        Check.False(PipelineStatus.Failed.IsActive(), "failed 不在跑");
        Check.False(PipelineStatus.Manual.IsActive(), "manual 不在跑");

        // GitLab 只对 failed/canceled/success 提供 retry，运行中也允许（会重跑未完成作业）
        Check.True(PipelineStatus.Failed.CanRetry(), "failed 可重跑");
        Check.True(PipelineStatus.Canceled.CanRetry(), "canceled 可重跑");
        Check.True(PipelineStatus.Success.CanRetry(), "success 可重跑");
        Check.True(PipelineStatus.Running.CanRetry(), "running 可重跑");
        Check.False(PipelineStatus.Created.CanRetry(), "created 不可重跑");
        Check.False(PipelineStatus.Manual.CanRetry(), "manual 不可重跑");
        Check.True(PipelineStatus.Running.CanCancel(), "running 可取消");
        Check.False(PipelineStatus.Success.CanCancel(), "success 不可取消");

        // 颜色：关键状态必须各就各位（图标、文字、托盘圆点都靠它）
        Check.Equal("#33B85C", PipelineStatus.Success.ColorHex(), "成功是绿");
        Check.Equal("#F04545", PipelineStatus.Failed.ColorHex(), "失败是红");
        Check.Equal("#388CFA", PipelineStatus.Running.ColorHex(), "运行中是蓝");
        Check.Equal("#F5A624", PipelineStatus.Pending.ColorHex(), "排队是黄");
        Check.Equal("#F5A624", PipelineStatus.WaitingForResource.ColorHex(), "等待资源也是黄");

        // 状态字符串与枚举要能双向换算（解析和展示共用一套映射）
        foreach (PipelineStatus value in Enum.GetValues(typeof(PipelineStatus)))
        {
            var api = value.ToApiString();
            if (value == PipelineStatus.Unknown) continue;
            Check.Equal(value, PipelineStatusExtensions.Parse(api), "双向换算 " + value);
        }
        Check.Equal("waiting_for_resource", PipelineStatus.WaitingForResource.ToApiString(), "长状态名");

        // 筛选：每档的归属要准确，「其他」是补集
        Check.True(StatusFilter.All.Matches(PipelineStatus.Manual), "全部匹配一切");
        Check.True(StatusFilter.Active.Matches(PipelineStatus.Running), "运行中档含 running");
        Check.False(StatusFilter.Active.Matches(PipelineStatus.Success), "运行中档不含 success");
        Check.True(StatusFilter.Failed.Matches(PipelineStatus.Failed), "失败档");
        Check.True(StatusFilter.Success.Matches(PipelineStatus.Success), "成功档");
        Check.True(StatusFilter.Other.Matches(PipelineStatus.Canceled), "其他档含已取消");
        Check.True(StatusFilter.Other.Matches(PipelineStatus.Manual), "其他档含手动");
        Check.True(StatusFilter.Other.Matches(PipelineStatus.Blocked), "其他档含阻塞");
        Check.False(StatusFilter.Other.Matches(PipelineStatus.Running), "其他档不含在跑的");
        Check.False(StatusFilter.Other.Matches(PipelineStatus.Failed), "其他档不含失败的");

        // 计数：胶囊上的数字
        var pipelines = new[]
        {
            new Pipeline { Id = 1, Status = PipelineStatus.Running },
            new Pipeline { Id = 2, Status = PipelineStatus.Failed },
            new Pipeline { Id = 3, Status = PipelineStatus.Failed },
            new Pipeline { Id = 4, Status = PipelineStatus.Success },
            new Pipeline { Id = 5, Status = PipelineStatus.Canceled }
        };
        var counts = StatusFilterExtensions.Counts(pipelines);
        Check.Equal(5, counts[StatusFilter.All], "全部计数");
        Check.Equal(1, counts[StatusFilter.Active], "运行中计数");
        Check.Equal(2, counts[StatusFilter.Failed], "失败计数");
        Check.Equal(1, counts[StatusFilter.Success], "成功计数");
        Check.Equal(1, counts[StatusFilter.Other], "其他计数");
        // 「全部」与其余四档重叠，所以应当是：除全部之外四档互斥且完备
        Check.Equal(5, counts[StatusFilter.Active] + counts[StatusFilter.Failed]
            + counts[StatusFilter.Success] + counts[StatusFilter.Other], "除全部外各档互斥且完备");

        Program.Ok("状态与筛选");
        return Task.CompletedTask;
    }
}
