namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>托盘图标要表达的整体状态。</summary>
public enum AggregateStatus
{
    /// <summary>没有数据。</summary>
    None,

    /// <summary>最新流水线都通过。</summary>
    Ok,

    /// <summary>有流水线在跑。</summary>
    Running,

    /// <summary>排队/准备中。</summary>
    Pending,

    /// <summary>有失败的。</summary>
    Failed,

    /// <summary>连接本身有问题。</summary>
    Error
}

public static class AggregateStatusExtensions
{
    public static string ColorHex(this AggregateStatus status) => status switch
    {
        AggregateStatus.Ok => PipelineStatus.Success.ColorHex(),
        AggregateStatus.Running => PipelineStatus.Running.ColorHex(),
        AggregateStatus.Pending => PipelineStatus.Pending.ColorHex(),
        AggregateStatus.Failed => PipelineStatus.Failed.ColorHex(),
        AggregateStatus.Error => "#9E9EA8",
        _ => "#8E8E93"
    };
}
