using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor;

/// <summary>
/// 运行新流水线覆盖层：搜索框过滤分支 / 标签，默认选中项目默认分支，回车或双击直接运行。
/// 对应 macOS 版 RunPipelineView。
/// </summary>
public partial class RunPipelineView : UserControl
{
    private readonly GitLabPipelineMonitor.Core.AppState _state;
    private readonly GitLabProject _project;
    private readonly Action _close;
    private List<BranchItem> _all = new();
    private string _selected = "";
    private bool _loaded;
    private bool _loading;
    private string? _error;

    public RunPipelineView(GitLabPipelineMonitor.Core.AppState state, GitLabProject project, Action close)
    {
        _state = state;
        _project = project;
        _close = close;
        InitializeComponent();
        ApplyTexts();
        UpdateRunState();
        _state.PropertyChanged += State_PropertyChanged;
        Unloaded += (_, _) => _state.PropertyChanged -= State_PropertyChanged;
        Loaded += RunPipelineView_Loaded;
    }

    private async void RunPipelineView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    private void ApplyTexts()
    {
        BackBtn.ToolTip = Loc.T("关闭");
        TitleText.Text = Loc.T("运行新流水线");
        PathText.Text = _project.PathWithNamespace;
        Placeholder.Text = Loc.T("选择分支或标签");
        LoadingText.Text = Loc.T("加载中…");
        ErrorRetryBtn.Content = Loc.T("重试");
        EmptyText.Text = Loc.T("没有可用的分支");
        CancelBtn.Content = Loc.T("取消");
        RunBtnText.Text = Loc.T("运行");
        // 占位文字：搜索框为空时显示（与面板搜索框同款做法）
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - 加载

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        BranchList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;

        var (branches, error) = await _state.FetchBranchesAsync(_project.Id);
        _loading = false;
        LoadingPanel.Visibility = Visibility.Collapsed;

        if (error is not null)
        {
            _error = error;
            ErrorText.Text = error;
            ErrorPanel.Visibility = Visibility.Visible;
            UpdateRunState();
            return;
        }

        _all = (branches ?? Array.Empty<Branch>())
            .Select(b => new BranchItem
            {
                Name = b.Name,
                ShowDefaultBadge = b.Name == _project.DefaultBranch || b.Default == true
            })
            .ToList();

        // 默认选中项目的默认分支；它不在列表里（数据过期之类）就退回第一条
        if (_selected.Length == 0)
        {
            var fallback = _project.DefaultBranch;
            _selected = _all.Any(x => x.Name == fallback) ? fallback! : _all.FirstOrDefault()?.Name ?? "";
        }
        ApplyFilter();
        UpdateRunState();
    }

    // MARK: - 过滤

    private void ApplyFilter()
    {
        if (_loading || _error is not null)
        {
            BranchList.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            return;
        }
        var needle = SearchBox.Text.Trim().ToLowerInvariant();
        var list = needle.Length == 0
            ? _all
            : _all.Where(x => x.Name.ToLowerInvariant().Contains(needle)).ToList();
        // 分支可能上千条：显示上限与 macOS 版一致
        if (list.Count > 200) list = list.GetRange(0, 200);

        BranchList.ItemsSource = list;
        BranchList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 过滤把选中项筛掉了也不丢选择：_selected 保持不变，能显示就重新勾上
        if (_selected.Length > 0)
        {
            var match = list.FirstOrDefault(x => x.Name == _selected);
            if (match is not null) BranchList.SelectedItem = match;
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    // MARK: - 选择与运行

    private void BranchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BranchList.SelectedItem is BranchItem item)
        {
            _selected = item.Name;
            UpdateRunState();
        }
    }

    private void BranchList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Run();

    private void Run()
    {
        if (_selected.Length == 0) return;
        _state.RunPipeline(_project, _selected);
        _close();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => Run();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _close();

    private void Back_Click(object sender, RoutedEventArgs e) => _close();

    private void RetryLoad_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitLabPipelineMonitor.Core.AppState.BusyAction))
            // BusyAction 是在后台线程上改的，按钮状态只能在 UI 线程上更新
            UiThread.Run(UpdateRunState);
    }

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = _selected.Length > 0 && _state.BusyAction != $"project-{_project.Id}-run";
    }
}

/// <summary>分支列表条目：名称 + 是否给默认分支挂标记。</summary>
internal sealed class BranchItem
{
    public required string Name { get; init; }
    public bool ShowDefaultBadge { get; init; }
    public string DefaultLabel => Loc.T("默认分支");
}
