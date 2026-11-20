using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Converters;

namespace GitLabPipelineMonitor;

/// <summary>
/// 流水线详情覆盖层：概要、提交信息、按 stage 分组的作业列表，以及流水线 / 作业级操作。
/// 对应 macOS 版 PipelineDetailView。数据在进入页面时异步拉取，不阻塞 UI。
/// </summary>
public partial class PipelineDetailView : UserControl
{
    private readonly GitLabPipelineMonitor.Core.AppState _state;
    private readonly GitLabProject _project;
    private readonly Pipeline _pipeline;
    private readonly Action _close;
    private Commit? _commit;
    private bool _loaded;
    private List<JobItem> _jobItems = new();

    public PipelineDetailView(GitLabPipelineMonitor.Core.AppState state, GitLabProject project, Pipeline pipeline, Action close)
    {
        _state = state;
        _project = project;
        _pipeline = pipeline;
        _close = close;
        InitializeComponent();
        ApplyTexts();
        RenderPipeline();
        UpdateBusyState();
        _state.PropertyChanged += State_PropertyChanged;
        Unloaded += (_, _) => _state.PropertyChanged -= State_PropertyChanged;
        Loaded += PipelineDetailView_Loaded;
    }

    private async void PipelineDetailView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    // MARK: - 文案（中文原文经 Loc.T 取译文）

    private void ApplyTexts()
    {
        BackBtn.ToolTip = Loc.T("关闭");
        TitleText.Text = Loc.T("流水线详情");
        PathText.Text = _project.PathWithNamespace;
        SummaryTitle.Text = Loc.T("流水线") + " #" + _pipeline.Iid;
        KeyBranch.Text = Loc.T("分支");
        CopyRefBtn.ToolTip = Loc.T("复制分支名");
        KeyCommit.Text = Loc.T("提交");
        KeyName.Text = Loc.T("名称");
        KeySource.Text = Loc.T("触发方式");
        KeyUser.Text = Loc.T("作者");
        KeyDuration.Text = Loc.T("耗时");
        KeyStarted.Text = Loc.T("开始时间");
        KeyFinished.Text = Loc.T("结束时间");
        KeyCreated.Text = Loc.T("创建时间");
        KeyUpdated.Text = Loc.T("更新时间");
        KeyCoverage.Text = Loc.T("覆盖率");
        CommitTitle.Text = Loc.T("提交信息");
        JobsTitle.Text = Loc.T("作业");
        JobsLoadingText.Text = Loc.T("加载中…");
        JobsRetryBtn.Content = Loc.T("重试");
        JobsEmpty.Text = Loc.T("无作业");
        RetryBtnText.Text = Loc.T("重跑流水线");
        CancelBtnText.Text = Loc.T("停止流水线");
        OpenBtnText.Text = Loc.T("打开流水线页面");
    }

    // MARK: - 概要

    private void RenderPipeline()
    {
        var brush = HexToBrushConverter.BrushFor(_pipeline.Status.ColorHex());
        HeaderStatusIcon.Fill = brush;
        if (Application.Current.TryFindResource("Icon." + _pipeline.Status.IconKey()) is Geometry geo)
            HeaderStatusIcon.Data = geo;
        HeaderText.Text = _pipeline.Status.Label();
        HeaderText.Foreground = brush;

        // 运行中：与面板行一致，图标转起来
        HeaderStatusIcon.RenderTransform = new RotateTransform();
        if (_pipeline.Status == PipelineStatus.Running)
        {
            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1)));
            Storyboard.SetTarget(spin, HeaderStatusIcon);
            Storyboard.SetTargetProperty(spin, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            storyboard.Children.Add(spin);
            HeaderStatusIcon.BeginStoryboard(storyboard);
        }

        ValRef.Text = _pipeline.Ref;
        ValSha.Text = _pipeline.ShortSha;
        if (!string.IsNullOrWhiteSpace(_pipeline.Name))
        {
            RowName.Visibility = Visibility.Visible;
            ValName.Text = _pipeline.Name;
        }
        if (!string.IsNullOrWhiteSpace(_pipeline.Source))
        {
            RowSource.Visibility = Visibility.Visible;
            ValSource.Text = _pipeline.Source;
        }
        if (_pipeline.User is { } user)
        {
            RowUser.Visibility = Visibility.Visible;
            ValUser.Text = $"{user.DisplayName} · @{user.Username}";
        }
        if (_pipeline.HasDuration)
        {
            RowDuration.Visibility = Visibility.Visible;
            ValDuration.Text = _pipeline.DurationText;
        }
        var started = _pipeline.StartedAt ?? _pipeline.CreatedAt;
        if (started is { })
        {
            RowStarted.Visibility = Visibility.Visible;
            ValStarted.Text = Fmt.Relative(started);
        }
        if (_pipeline.FinishedAt is { } finished)
        {
            RowFinished.Visibility = Visibility.Visible;
            ValFinished.Text = Fmt.Relative(finished);
        }
        if (_pipeline.CreatedAt is { } created)
        {
            RowCreated.Visibility = Visibility.Visible;
            ValCreated.Text = Fmt.Relative(created);
        }
        if (_pipeline.UpdatedAt is { } updated)
        {
            RowUpdated.Visibility = Visibility.Visible;
            ValUpdated.Text = Fmt.Relative(updated);
        }
        if (_pipeline.HasCoverage)
        {
            RowCoverage.Visibility = Visibility.Visible;
            ValCoverage.Text = _pipeline.Coverage;
        }
    }

    // MARK: - 加载

    private async Task LoadAsync()
    {
        JobsLoading.Visibility = Visibility.Visible;
        JobsErrorPanel.Visibility = Visibility.Collapsed;
        JobsEmpty.Visibility = Visibility.Collapsed;
        JobsList.Visibility = Visibility.Collapsed;

        var (jobs, error) = await _state.FetchJobsAsync(_project.Id, _pipeline.Id);
        JobsLoading.Visibility = Visibility.Collapsed;
        if (error is not null)
        {
            JobsErrorText.Text = error;
            JobsErrorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            var list = jobs ?? Array.Empty<Job>();
            if (list.Count == 0)
            {
                JobsEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                BuildStageGroups(list);
                JobsList.Visibility = Visibility.Visible;
            }
            UpdateBusyState();
        }

        if (_commit is null)
            _commit = await _state.FetchCommitAsync(_project.Id, _pipeline.Sha);
        if (!string.IsNullOrWhiteSpace(_commit?.Title))
        {
            CommitCard.Visibility = Visibility.Visible;
            CommitTitleText.Text = _commit!.Title;
            if (!string.IsNullOrWhiteSpace(_commit.AuthorName))
            {
                CommitAuthor.Visibility = Visibility.Visible;
                CommitAuthor.Text = _commit.AuthorName;
            }
            if (_commit.CreatedAt is { } commitAt)
            {
                CommitTime.Visibility = Visibility.Visible;
                CommitTime.Text = Fmt.Relative(commitAt);
            }
        }
    }

    /// <summary>按 stage 首次出现的顺序分组，组名大写（与 macOS 版的 textCase(.uppercase) 一致）。</summary>
    private void BuildStageGroups(IReadOnlyList<Job> jobs)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<JobItem>>();
        var baseUrl = _state.Settings.NormalizedBaseUrl;
        foreach (var job in jobs)
        {
            if (!buckets.TryGetValue(job.Stage, out var bucket))
            {
                bucket = new List<JobItem>();
                buckets[job.Stage] = bucket;
                order.Add(job.Stage);
            }
            bucket.Add(new JobItem
            {
                Job = job,
                Url = job.ResolvedWebUrl(baseUrl, _project)
            });
        }
        _jobItems = buckets.Values.SelectMany(v => v).ToList();
        JobsList.ItemsSource = order.Select(name => new StageGroup
        {
            Name = name.ToUpperInvariant(),
            Jobs = buckets[name]
        }).ToList();
    }

    // MARK: - 忙碌状态（只让对应按钮转圈）

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitLabPipelineMonitor.Core.AppState.BusyAction))
            UpdateBusyState();
    }

    private void UpdateBusyState()
    {
        var busy = _state.BusyAction;
        var pipelineBusy = busy == $"pipeline-{_pipeline.Id}-retry" || busy == $"pipeline-{_pipeline.Id}-cancel";
        RetryBtn.IsEnabled = _pipeline.Status.CanRetry() && !pipelineBusy;
        CancelBtn.IsEnabled = _pipeline.Status.CanCancel() && !pipelineBusy;
        PipelineBusyIndicator.Visibility = pipelineBusy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _jobItems)
            item.Busy = busy == $"job-{item.Job.Id}-retry" || busy == $"job-{item.Job.Id}-cancel";
    }

    // MARK: - 操作

    private void Back_Click(object sender, RoutedEventArgs e) => _close();

    private void CopyRef_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).GetHost().CopyToClipboard(_pipeline.Ref);

    private void RetryPipeline_Click(object sender, RoutedEventArgs e) => _state.Retry(_pipeline);

    private void CancelPipeline_Click(object sender, RoutedEventArgs e) => _state.Cancel(_pipeline);

    private void OpenPipelinePage_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).GetHost().OpenUrl(_project.PipelineUrl(_pipeline, _state.Settings.NormalizedBaseUrl));

    private void RetryJobs_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>作业操作按钮（Tag）和右键菜单项（DataContext）都指向同一个 JobItem。</summary>
    private static JobItem? ItemFrom(object sender) => sender switch
    {
        Button { Tag: JobItem button } => button,
        MenuItem { DataContext: JobItem menu } => menu,
        _ => (sender as FrameworkElement)?.DataContext as JobItem
    };

    private void RetryJob_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) _state.RetryJob(item.Job, _project.Id);
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) _state.CancelJob(item.Job, _project.Id);
    }

    private void OpenJob_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item && item.HasUrl)
            ((App)Application.Current).GetHost().OpenUrl(item.Url);
    }
}

/// <summary>作业列表的 stage 分组。</summary>
internal sealed class StageGroup
{
    public required string Name { get; init; }
    public required List<JobItem> Jobs { get; init; }
}

/// <summary>作业行的绑定包装：派生显示值 + 忙碌标记（BusyAction 变了只刷新这一行）。</summary>
internal sealed class JobItem : INotifyPropertyChanged
{
    private bool _busy;

    public required Job Job { get; init; }
    public required string Url { get; init; }

    public string Name => Job.Name;
    public PipelineStatus Status => Job.Status;
    public bool HasDuration => Job.Duration.HasValue;
    public string DurationText => Fmt.Duration(Job.Duration);
    public bool ShowAllowFailure => Job.AllowFailure == true && Job.Status == PipelineStatus.Failed;
    public bool HasFailure => !string.IsNullOrWhiteSpace(Job.FailureReason);
    public string FailureReasonText => Job.FailureReason ?? "";
    public bool CanRetry => Job.Status.CanRetry();
    public bool CanCancel => Job.Status.CanCancel();
    public bool HasUrl => Url.Length > 0;
    public string Tooltip => string.IsNullOrWhiteSpace(Job.FailureReason) ? Job.Name : Job.FailureReason!;

    // 模板里的文案：DataContext 是 JobItem，取不到视图上的资源，所以译文挂在条目上。
    public string AllowFailureLabel => Loc.T("允许失败");
    public string MenuViewLog => Loc.T("查看作业日志");
    public string MenuRetryJob => Loc.T("重跑作业");
    public string MenuCancelJob => Loc.T("停止作业");
    public string TipRetry => Loc.T("重跑作业");
    public string TipCancel => Loc.T("停止作业");
    public string TipOpen => Loc.T("查看作业日志");

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Busy)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
