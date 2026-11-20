using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>
/// 流水线 / 作业状态。颜色和图标语义与 macOS 版逐一对齐。
/// 颜色用 #RRGGBB 字符串而不是平台颜色类型：Core 要能在非 Windows 上编译，
/// 而且 hex 可以直接写进单元测试里核对。</summary>
public enum PipelineStatus
{
    Created,
    WaitingForResource,
    Preparation,
    Pending,
    Running,
    Success,
    Failed,
    Canceled,
    Skipped,
    Manual,
    Scheduled,
    Blocked,
    NotStarted,
    Unknown
}

public static class PipelineStatusExtensions
{
    /// <summary>GitLab 返回的状态字符串 → 枚举；认不出的一律算 Unknown。</summary>
    public static PipelineStatus Parse(string? raw) => raw switch
    {
        "created" => PipelineStatus.Created,
        "waiting_for_resource" => PipelineStatus.WaitingForResource,
        "preparation" => PipelineStatus.Preparation,
        "pending" => PipelineStatus.Pending,
        "running" => PipelineStatus.Running,
        "success" => PipelineStatus.Success,
        "failed" => PipelineStatus.Failed,
        "canceled" => PipelineStatus.Canceled,
        "skipped" => PipelineStatus.Skipped,
        "manual" => PipelineStatus.Manual,
        "scheduled" => PipelineStatus.Scheduled,
        "blocked" => PipelineStatus.Blocked,
        "not_started" => PipelineStatus.NotStarted,
        // 认不出的一律 Unknown（含空串）：和 macOS 版 rawValue 失败退 Unknown 的语义一致
        _ => PipelineStatus.Unknown
    };

    /// <summary>枚举 → GitLab 的状态字符串（解析和序列化共用一套映射）。</summary>
    public static string ToApiString(this PipelineStatus status) => status switch
    {
        PipelineStatus.Created => "created",
        PipelineStatus.WaitingForResource => "waiting_for_resource",
        PipelineStatus.Preparation => "preparation",
        PipelineStatus.Pending => "pending",
        PipelineStatus.Running => "running",
        PipelineStatus.Success => "success",
        PipelineStatus.Failed => "failed",
        PipelineStatus.Canceled => "canceled",
        PipelineStatus.Skipped => "skipped",
        PipelineStatus.Manual => "manual",
        PipelineStatus.Scheduled => "scheduled",
        PipelineStatus.Blocked => "blocked",
        PipelineStatus.NotStarted => "not_started",
        _ => "unknown"
    };

    public static string Label(this PipelineStatus status) => status switch
    {
        PipelineStatus.Created => Loc.T("已创建"),
        PipelineStatus.WaitingForResource => Loc.T("等待资源"),
        PipelineStatus.Preparation => Loc.T("准备中"),
        PipelineStatus.Pending => Loc.T("排队中"),
        PipelineStatus.Running => Loc.T("运行中"),
        PipelineStatus.Success => Loc.T("已通过"),
        PipelineStatus.Failed => Loc.T("已失败"),
        PipelineStatus.Canceled => Loc.T("已取消"),
        PipelineStatus.Skipped => Loc.T("已跳过"),
        PipelineStatus.Manual => Loc.T("手动"),
        PipelineStatus.Scheduled => Loc.T("已计划"),
        PipelineStatus.Blocked => Loc.T("已阻塞"),
        PipelineStatus.NotStarted => Loc.T("未开始"),
        _ => Loc.T("未知")
    };

    /// <summary>状态色，取值与 macOS 版逐一对齐。</summary>
    public static string ColorHex(this PipelineStatus status) => status switch
    {
        PipelineStatus.Success => "#33B85C",
        PipelineStatus.Failed => "#F04545",
        PipelineStatus.Running => "#388CFA",
        PipelineStatus.Pending or PipelineStatus.Scheduled or PipelineStatus.WaitingForResource or PipelineStatus.Preparation
            => "#F5A624",
        PipelineStatus.Canceled => "#8C949E",
        PipelineStatus.Manual => "#9E73E6",
        PipelineStatus.Blocked => "#D97333",
        _ => "#8E8E93"
    };

    /// <summary>是否还在跑（排队也算）。</summary>
    public static bool IsActive(this PipelineStatus status) => status switch
    {
        PipelineStatus.Running or PipelineStatus.Pending or PipelineStatus.Scheduled or PipelineStatus.Created
            or PipelineStatus.WaitingForResource or PipelineStatus.Preparation => true,
        _ => false
    };

    /// <summary>GitLab 只对 failed / canceled / success 提供 retry，运行中也允许（会重跑未完成作业）。</summary>
    public static bool CanRetry(this PipelineStatus status) =>
        status is PipelineStatus.Failed or PipelineStatus.Canceled or PipelineStatus.Success or PipelineStatus.Running;

    public static bool CanCancel(this PipelineStatus status) => status.IsActive();
}
