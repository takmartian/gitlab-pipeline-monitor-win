using System.Collections.ObjectModel;
using System.Windows.Threading;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;

namespace GitLabPipelineMonitor.ViewModels;

/// <summary>面板里一个项目分组（含按筛选与搜索过滤后的流水线）。</summary>
public sealed class PanelGroup
{
    public required GitLabProject Project { get; init; }
    public required IReadOnlyList<Pipeline> Pipelines { get; init; }
    public bool HasActive => Pipelines.Any(p => p.Status.IsActive());
    public bool HasFailed => Pipelines.Any(p => p.Status == PipelineStatus.Failed);
    public Pipeline? Latest => Pipelines.Count == 0 ? null : Pipelines.OrderByDescending(p => p.SortDate).First();
    public string Title => Project.ShortPath;
    public string Subtitle => Project.Namespace;
    public bool ShowSubtitle => Project.Namespace.Length > 0;
    public bool ShowArchived => Project.Archived;
    public bool Starred => Project.Starred;
    public string LatestTime => Fmt.Relative(Latest?.SortDate);
    public bool ShowLatest => Latest is not null;
    public string? LatestColor => Latest?.Status.ColorHex();
    public bool ShowError => Error is { Length: > 0 };
    public string? Error { get; set; }
}

/// <summary>面板主体要显示什么。空状态有好几种，每种文案和按钮都不同。</summary>
public enum PanelContent
{
    List,
    NotConfigured,
    NoProjects,
    ConnectionFailed,
    NoPipelines,
    NoMatches
}

/// <summary>
/// 面板的视图模型：负责筛选、搜索、分组排序和空状态判定。
/// 排序规则与 macOS 版一致：有在跑的排前面，其次有失败的，最后按最新流水线时间。
/// </summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly Core.AppState _state;
    private readonly AppSettings _settings;

    public ObservableCollection<PanelGroup> Groups { get; } = new();
    public ObservableCollection<FilterChipViewModel> Filters { get; } = new();

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            Rebuild();
        }
    }

    private StatusFilter _selected = StatusFilter.All;
    public StatusFilter Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var chip in Filters) chip.IsSelected = chip.Filter == value;
            Rebuild();
        }
    }

    private PanelContent _content = PanelContent.NotConfigured;
    public PanelContent Content { get => _content; private set => Set(ref _content, value); }

    public bool ShowList => Content == PanelContent.List;
    public bool ShowEmpty => Content != PanelContent.List;

    private string _emptyTitle = "";
    public string EmptyTitle { get => _emptyTitle; private set => Set(ref _emptyTitle, value); }

    private string _emptyMessage = "";
    public string EmptyMessage { get => _emptyMessage; private set => Set(ref _emptyMessage, value); }

    private string? _emptyAction;
    public string? EmptyAction { get => _emptyAction; private set => Set(ref _emptyAction, value); }

    private string _emptyIconKey = "Link";
    public string EmptyIconKey { get => _emptyIconKey; private set => Set(ref _emptyIconKey, value); }

    private string _footerSummary = "";
    public string FooterSummary { get => _footerSummary; private set => Set(ref _footerSummary, value); }

    private string _footerUpdated = "";
    public string FooterUpdated { get => _footerUpdated; private set => Set(ref _footerUpdated, value); }

    private bool _showRefreshIndicator;
    public bool ShowRefreshIndicator { get => _showRefreshIndicator; private set => Set(ref _showRefreshIndicator, value); }

    private string _headerSubtitle = "";
    public string HeaderSubtitle { get => _headerSubtitle; private set => Set(ref _headerSubtitle, value); }

    private bool _headerSubtitleIsError;
    public bool HeaderSubtitleIsError { get => _headerSubtitleIsError; private set => Set(ref _headerSubtitleIsError, value); }

    private string _headerHost = "";
    public string HeaderHost { get => _headerHost; private set => Set(ref _headerHost, value); }

    private string _timerLabel = "";
    public string TimerLabel { get => _timerLabel; private set => Set(ref _timerLabel, value); }

    private string? _toast;
    public string? Toast { get => _toast; private set => Set(ref _toast, value); }

    private bool _toastIsError;
    public bool ToastIsError { get => _toastIsError; private set => Set(ref _toastIsError, value); }

    public PanelViewModel(Core.AppState state, AppSettings settings)
    {
        _state = state;
        _settings = settings;

        foreach (var filter in StatusFilterExtensions.AllCases)
            Filters.Add(new FilterChipViewModel(filter, this));

        _state.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Core.AppState.Pipelines):
                case nameof(Core.AppState.StarredProjects):
                case nameof(Core.AppState.MemberProjects):
                case nameof(Core.AppState.ProjectErrors):
                case nameof(Core.AppState.LastUpdated):
                case nameof(Core.AppState.FatalErrorMessage):
                case nameof(Core.AppState.IsLoadingPipelines):
                case nameof(Core.AppState.IsLoadingProjects):
                case nameof(Core.AppState.User):
                    Rebuild();
                    break;
                case nameof(Core.AppState.ActionMessage):
                    ShowToast(_state.ActionMessage);
                    _state.ActionMessage = null;
                    break;
            }
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.OnlyStarred) or nameof(AppSettings.MonitoredProjectIds)
                or nameof(AppSettings.ServerUrl) or nameof(AppSettings.Token) or nameof(AppSettings.RefreshInterval))
                Rebuild();
        };

        Rebuild();
    }

    private void ShowToast(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Toast = message;
        ToastIsError = message.Contains("失败", StringComparison.Ordinal)
                       || message.Contains("失败", StringComparison.Ordinal);
        // 3 秒后自动收起来
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { timer.Stop(); Toast = null; };
        timer.Start();
    }

    public IReadOnlyDictionary<StatusFilter, int> LastCounts { get; private set; } =
        StatusFilterExtensions.AllCases.ToDictionary(f => f, _ => 0);

    public void Rebuild()
    {
        var monitored = _state.MonitoredProjects;
        var allPipelines = _state.AllVisiblePipelines;
        LastCounts = StatusFilterExtensions.Counts(allPipelines);
        foreach (var chip in Filters) chip.RefreshCount(LastCounts);

        HeaderHost = _state.DemoHost ?? ServerAddress.HostOf(_settings.ServerUrl) ?? Loc.T("未配置");

        // 错误优先于账号：令牌失效时不该继续显示上一个账号名，免得看着像连上了
        if (_state.User is { } user)
        {
            HeaderSubtitle = $"{user.DisplayName} · @{user.Username}";
            HeaderSubtitleIsError = false;
        }
        else if (_state.FatalErrorMessage is { Length: > 0 } error)
        {
            HeaderSubtitle = error;
            HeaderSubtitleIsError = true;
        }
        else
        {
            HeaderSubtitle = Loc.T("未配置");
            HeaderSubtitleIsError = false;
        }

        TimerLabel = _settings.RefreshInterval > 0 ? AppSettings.RefreshIntervalLabel(_settings.RefreshInterval) : "";
        FooterUpdated = Loc.T("更新于 {0}", Fmt.Clock(_state.LastUpdated));
        FooterSummary = Loc.T("共 {0} 个项目 · {1} 条流水线", monitored.Count, allPipelines.Count);
        ShowRefreshIndicator = _state.IsLoadingPipelines || _state.IsLoadingProjects;

        // 空状态判定：顺序有讲究，未配置 > 没有项目 > 连接失败 > 没有流水线 > 筛不到
        if (!_settings.IsConfigured)
        {
            SetEmpty(PanelContent.NotConfigured, "Link", Loc.T("未配置 GitLab 连接"),
                Loc.T("填写服务器地址和访问令牌后，这里会显示你打星项目的流水线。"), Loc.T("开始配置"));
            return;
        }
        if (monitored.Count == 0)
        {
            SetEmpty(PanelContent.NoProjects, "StarOutline", Loc.T("没有可监控的项目"),
                Loc.T("在设置里勾选要监控的项目，或开启「仅监控星标项目」。"), Loc.T("设置"));
            return;
        }

        var needle = Query.Trim().ToLowerInvariant();
        var groups = new List<PanelGroup>();
        var anyPipelineAtAll = false;

        foreach (var project in monitored)
        {
            var all = (_state.Pipelines.TryGetValue(project.Id, out var list) ? list : Array.Empty<Pipeline>())
                .OrderByDescending(p => p.SortDate)
                .ToList();
            if (all.Count > 0) anyPipelineAtAll = true;

            var matched = all.Where(pipeline => Selected.Matches(pipeline.Status)
                && (needle.Length == 0
                    || project.PathWithNamespace.ToLowerInvariant().Contains(needle)
                    || project.Name.ToLowerInvariant().Contains(needle)
                    || pipeline.Ref.ToLowerInvariant().Contains(needle)
                    || pipeline.ShortSha.ToLowerInvariant().Contains(needle)
                    || (pipeline.Name?.ToLowerInvariant().Contains(needle) ?? false))).ToList();

            // 没有任何筛选条件时，即使一条流水线都没有也要把项目本身列出来（显示「暂无流水线记录」）
            if (matched.Count == 0 && Selected == StatusFilter.All && needle.Length == 0)
                matched = all;
            if (matched.Count == 0) continue;

            groups.Add(new PanelGroup
            {
                Project = project,
                Pipelines = matched,
                Error = _state.ProjectErrors.TryGetValue(project.Id, out var message) ? message : null
            });
        }

        var visible = groups
            .OrderByDescending(g => g.HasActive)
            .ThenByDescending(g => g.HasFailed)
            .ThenByDescending(g => g.Latest?.SortDate ?? DateTime.MinValue)
            .ToList();

        if (visible.Count == 0)
        {
            if (_state.FatalErrorMessage is { Length: > 0 } failure && !anyPipelineAtAll)
            {
                SetEmpty(PanelContent.ConnectionFailed, "CloudOff", Loc.T("连接失败"), failure, Loc.T("重新加载"));
                return;
            }
            if (!anyPipelineAtAll)
            {
                SetEmpty(PanelContent.NoPipelines, "Timer", Loc.T("暂无流水线记录"), null, null);
                return;
            }
            SetEmpty(PanelContent.NoMatches, "Filter", Loc.T("没有符合筛选条件的流水线"),
                Loc.T("试试切换筛选或清空搜索。"), null);
            return;
        }

        Content = PanelContent.List;
        Groups.Clear();
        foreach (var group in visible) Groups.Add(group);
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void SetEmpty(PanelContent kind, string icon, string title, string? message, string? action)
    {
        Content = kind;
        EmptyIconKey = icon;
        EmptyTitle = title;
        EmptyMessage = message ?? "";
        EmptyAction = action;
        Groups.Clear();
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>空状态上的按钮：未配置去连接页，没有项目去项目页，连接失败就重载。</summary>
    public void RunEmptyAction()
    {
        switch (Content)
        {
            case PanelContent.NotConfigured:
                ((App)System.Windows.Application.Current).ShowSettings(Views.SettingsTab.Connection);
                break;
            case PanelContent.NoProjects:
                ((App)System.Windows.Application.Current).ShowSettings(Views.SettingsTab.Projects);
                break;
            case PanelContent.ConnectionFailed:
                _state.Refresh(force: true);
                break;
        }
    }
}

/// <summary>筛选胶囊：标题 + 计数 + 是否选中。</summary>
public sealed class FilterChipViewModel : ObservableObject
{
    private readonly PanelViewModel _owner;
    public StatusFilter Filter { get; }
    public string Title => Filter.Title();
    public string? TintHex => Filter.TintHex();
    public bool HasTint => TintHex is not null;
    public int Count { get; private set; }
    public bool ShowCount => Count > 0;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public FilterChipViewModel(StatusFilter filter, PanelViewModel owner)
    {
        Filter = filter;
        _owner = owner;
        _isSelected = filter == StatusFilter.All;
    }

    public void Select() => _owner.Selected = Filter;

    public void RefreshCount(IReadOnlyDictionary<StatusFilter, int> counts)
    {
        var next = counts.TryGetValue(Filter, out var value) ? value : 0;
        if (next == Count) return;
        Count = next;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ShowCount));
    }
}
