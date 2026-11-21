using System.Collections.ObjectModel;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.ViewModels;

/// <summary>浮标里列出的一条流水线。</summary>
public sealed class BadgeItem
{
    public required int PipelineId { get; init; }
    public required string Project { get; init; }
    public required string Branch { get; init; }
    public required string StatusText { get; init; }

    /// <summary>正在跑。</summary>
    public required bool IsRunning { get; init; }

    /// <summary>失败了（不是在跑的那种）。</summary>
    public required bool IsFailed { get; init; }
}

/// <summary>
/// 屏幕边缘浮标的视图模型。
///
/// 上半部分是状态计数（运行中 / 失败 / 成功），下半部分只列**正在跑**和**刚失败**的流水线。
/// 数据全部来自 <see cref="Core.AppState"/> 里已经拉好的内容 —— 浮标不额外发请求，
/// 只是把托盘图标本来就在用的那份状态换个地方显示。
/// </summary>
public sealed class FloatingBadgeViewModel : ObservableObject
{
    /// <summary>展开后最多列几条，超出的只报个数。</summary>
    private const int MaxItems = 8;

    private readonly Core.AppState _state;

    public FloatingBadgeViewModel(Core.AppState state)
    {
        _state = state;
    }

    /// <summary>正在跑的和刚失败的流水线。</summary>
    public ObservableCollection<BadgeItem> Items { get; } = new();

    private int _runningCount;
    public int RunningCount => _runningCount;

    private int _failedCount;
    public int FailedCount => _failedCount;

    private int _successCount;
    public int SuccessCount => _successCount;

    /// <summary>有流水线在跑。</summary>
    public bool HasRunning => _runningCount > 0;

    /// <summary>有失败的流水线（没在跑的才用这个颜色，免得两个信号打架）。</summary>
    public bool HasFailed => !HasRunning && _failedCount > 0;

    /// <summary>什么都没有 —— 折叠时几乎透明，不打扰。</summary>
    public bool IsIdle => !HasRunning && !HasFailed;

    /// <summary>列表里没列完的条数。</summary>
    private int _hiddenCount;
    public int HiddenCount => _hiddenCount;

    public bool HasMore => _hiddenCount > 0;

    /// <summary>有没有值得列出来的条目（一条都没有时显示一句空态提示）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>展开后顶部那一行摘要。</summary>
    public string Summary => Loc.T("运行中 {0} · 失败 {1} · 成功 {2}", _runningCount, _failedCount, _successCount);

    /// <summary>没有任何值得显示的流水线时的提示。</summary>
    public string EmptyText => Loc.T("没有在跑的流水线");

    public string MoreText => Loc.T("还有 {0} 条…", _hiddenCount);

    /// <summary>按当前状态重建计数与列表。</summary>
    public void Rebuild()
    {
        var pipelines = _state.AllVisiblePipelines;

        var running = 0;
        var failed = 0;
        var success = 0;
        foreach (var pipeline in pipelines)
        {
            if (pipeline.Status.IsActive()) running++;
            else if (pipeline.Status == PipelineStatus.Failed) failed++;
            else if (pipeline.Status == PipelineStatus.Success) success++;
        }

        // 在跑的排前面，其次是失败的；同类按时间新的在前
        var interesting = pipelines
            .Where(p => p.Status.IsActive() || p.Status == PipelineStatus.Failed)
            .OrderByDescending(p => p.Status.IsActive())
            .ThenByDescending(p => p.SortDate)
            .ToList();

        Items.Clear();
        foreach (var pipeline in interesting.Take(MaxItems))
        {
            var project = _state.ProjectFor(pipeline);
            Items.Add(new BadgeItem
            {
                PipelineId = pipeline.Id,
                Project = project?.ShortPath ?? Loc.T("未知项目"),
                Branch = string.IsNullOrEmpty(pipeline.Ref) ? pipeline.Name ?? "" : pipeline.Ref,
                StatusText = pipeline.StatusText,
                IsRunning = pipeline.Status.IsActive(),
                IsFailed = pipeline.Status == PipelineStatus.Failed,
            });
        }

        _hiddenCount = Math.Max(0, interesting.Count - MaxItems);
        _runningCount = running;
        _failedCount = failed;
        _successCount = success;

        Refresh(
            nameof(RunningCount), nameof(FailedCount), nameof(SuccessCount),
            nameof(HasRunning), nameof(HasFailed), nameof(IsIdle),
            nameof(HiddenCount), nameof(HasMore), nameof(HasItems),
            nameof(Summary), nameof(EmptyText), nameof(MoreText));
    }
}
