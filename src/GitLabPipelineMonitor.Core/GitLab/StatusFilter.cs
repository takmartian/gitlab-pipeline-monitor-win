using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>面板顶部的状态筛选。</summary>
public enum StatusFilter
{
    All,
    Active,
    Failed,
    Success,
    Other
}

public static class StatusFilterExtensions
{
    public static readonly StatusFilter[] AllCases =
        { StatusFilter.All, StatusFilter.Active, StatusFilter.Failed, StatusFilter.Success, StatusFilter.Other };

    public static string Title(this StatusFilter filter) => filter switch
    {
        StatusFilter.All => Loc.T("全部"),
        StatusFilter.Active => Loc.T("运行中"),
        StatusFilter.Failed => Loc.T("失败"),
        StatusFilter.Success => Loc.T("成功"),
        _ => Loc.T("其他")
    };

    /// <summary>胶囊上的色点；「全部」没有色点。</summary>
    public static string? TintHex(this StatusFilter filter) => filter switch
    {
        StatusFilter.Active => PipelineStatus.Running.ColorHex(),
        StatusFilter.Failed => PipelineStatus.Failed.ColorHex(),
        StatusFilter.Success => PipelineStatus.Success.ColorHex(),
        StatusFilter.Other => PipelineStatus.Canceled.ColorHex(),
        _ => null
    };

    public static bool Matches(this StatusFilter filter, PipelineStatus status) => filter switch
    {
        StatusFilter.All => true,
        StatusFilter.Active => status.IsActive(),
        StatusFilter.Failed => status == PipelineStatus.Failed,
        StatusFilter.Success => status == PipelineStatus.Success,
        // 「其他」= 不在跑、不是失败、不是成功：取消、跳过、手动、阻塞这些
        _ => !status.IsActive() && status != PipelineStatus.Failed && status != PipelineStatus.Success
    };

    /// <summary>各档计数（面板胶囊上的数字）。</summary>
    public static IReadOnlyDictionary<StatusFilter, int> Counts(IEnumerable<Pipeline> pipelines)
    {
        var list = pipelines.Select(p => p.Status).ToList();
        return AllCases.ToDictionary(f => f, f => list.Count(status => f.Matches(status)));
    }
}
