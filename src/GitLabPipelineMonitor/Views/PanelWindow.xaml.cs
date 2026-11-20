using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;
using GitLabPipelineMonitor.ViewModels;

namespace GitLabPipelineMonitor;

/// <summary>
/// 从托盘图标弹出的主面板。无边框、不进任务栏、失去焦点自动收起，
/// 行为对齐 macOS 版那个从菜单栏弹出的面板。
/// </summary>
public partial class PanelWindow : Window
{
    private readonly Core.AppState _state;
    private readonly AppSettings _settings;
    private readonly UpdateManager _updater;
    private readonly PanelViewModel _vm;

    /// <summary>正在拖底边调高度。</summary>
    private bool _resizing;

    private int _resizeStartY;
    private int _resizeStartHeight;

    public PanelWindow(Core.AppState state, AppSettings settings, UpdateManager updater)
    {
        _state = state;
        _settings = settings;
        _updater = updater;
        _vm = new PanelViewModel(state, settings);

        InitializeComponent();
        DataContext = _vm;

        SearchBox.PreviewKeyDown += Search_PreviewKeyDown;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.Toast)) UpdateToast();
        };
        MouseDown += (_, _) => { if (_resizing) return; };
    }

    /// <summary>在指定屏幕位置按指定尺寸显示（托盘图标右下角对齐）。</summary>
    public void ShowAt(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _vm.Rebuild();
        Show();
        Activate();
        // 面板宽度变了要同步一次高度上限（宽度不影响高度，这里只是确保尺寸落地）
        Dispatcher.InvokeAsync(() => { Left = left; Top = top; Width = width; Height = height; Activate(); });
    }

    public void HidePanel()
    {
        CloseOverlay();
        Hide();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // 点到面板外面就收起，这是 Windows 上弹出式面板的常规行为。
        // 正在拖高度时不收起，否则一拖就关。
        if (_resizing) return;
        if (OverlayHost.Visibility == Visibility.Visible) return;
        Hide();
    }

    // MARK: - 顶栏与筛选

    private void Refresh_Click(object sender, RoutedEventArgs e) => _state.Refresh(force: true);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        ((App)Application.Current).ShowSettings();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _vm.Query = "";
        SearchBox.Focus();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: FilterChipViewModel chip }) chip.Select();
    }

    private void Search_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc：先关详情/运行覆盖层，再关搜索，最后关面板（与 macOS 版一致的层级）
        if (e.Key != Key.Escape) return;
        if (OverlayHost.Visibility == Visibility.Visible) { CloseOverlay(); e.Handled = true; return; }
        if (SearchBox.Text.Length > 0) { ClearSearch_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        Hide();
        e.Handled = true;
    }

    private void EmptyAction_Click(object sender, RoutedEventArgs e) => _vm.RunEmptyAction();

    private void OpenGitLab_Click(object sender, RoutedEventArgs e)
    {
        var host = _state.DemoHost is null ? _settings.NormalizedBaseUrl : "https://" + _state.DemoHost;
        if (!string.IsNullOrEmpty(host)) ((App)Application.Current).GetHost().OpenUrl(host!);
    }

    // MARK: - 项目分组

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int projectId }) return;
        // 折叠状态记在 code-behind：它纯粹是这个窗口的显示细节，没必要进 Core
        if (_collapsed.Contains(projectId)) _collapsed.Remove(projectId);
        else _collapsed.Add(projectId);
        ApplyCollapse();
    }

    private readonly HashSet<int> _collapsed = new();

    private void ApplyCollapse()
    {
        // 折叠只是把该组的流水线行藏起来，项目头保留
        var host = FindVisualChild<ItemsControl>(this);
        if (host is null) return;
        foreach (var group in _vm.Groups)
        {
            if (host.ItemContainerGenerator.ContainerFromItem(group) is not FrameworkElement container) continue;
            var items = FindVisualChild<ItemsControl>(container);
            if (items is null) continue;
            items.Visibility = _collapsed.Contains(group.Project.Id) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void StarProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int projectId }) return;
        var project = _vm.Groups.FirstOrDefault(g => g.Project.Id == projectId)?.Project
                      ?? _state.StarredProjects.FirstOrDefault(p => p.Id == projectId)
                      ?? _state.MemberProjects.FirstOrDefault(p => p.Id == projectId);
        if (project is null) return;
        _state.SetStarred(project, !project.Starred);
    }

    private void RunPipeline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int projectId }) return;
        var group = _vm.Groups.FirstOrDefault(g => g.Project.Id == projectId);
        if (group is not null) ShowOverlay(new RunPipelineView(_state, group.Project, CloseOverlay));
    }

    // MARK: - 流水线行

    private void OpenPipeline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int pipelineId }) return;
        var pipeline = FindPipeline(pipelineId);
        if (pipeline is null) return;
        var project = _state.ProjectFor(pipeline);
        if (project is null) return;
        ShowOverlay(new PipelineDetailView(_state, project, pipeline, CloseOverlay));
    }

    private Pipeline? FindPipeline(int pipelineId)
        => _state.Pipelines.Values.SelectMany(v => v).FirstOrDefault(p => p.Id == pipelineId);

    private void RetryRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id } && FindPipeline(id) is { } pipeline) _state.Retry(pipeline);
    }

    private void CancelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id } && FindPipeline(id) is { } pipeline) _state.Cancel(pipeline);
    }

    // MARK: - 右键菜单

    private void MenuDetail_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemPipeline(sender) is not { } pipeline) return;
        OpenPipeline_Click(new Button { Tag = pipeline.Id }, e);
    }

    private void MenuOpenPipeline_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemPipeline(sender) is not { } pipeline) return;
        var project = _state.ProjectFor(pipeline);
        if (project is null) return;
        ((App)Application.Current).GetHost().OpenUrl(project.PipelineUrl(pipeline, _settings.NormalizedBaseUrl));
    }

    private void MenuRetry_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemPipeline(sender) is { } pipeline) _state.Retry(pipeline);
    }

    private void MenuCancel_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemPipeline(sender) is { } pipeline) _state.Cancel(pipeline);
    }

    private void MenuCopyRef_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemPipeline(sender) is { } pipeline) ((App)Application.Current).GetHost().CopyToClipboard(pipeline.Ref);
    }

    private void MenuCopySha_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemPipeline(sender) is { } pipeline) ((App)Application.Current).GetHost().CopyToClipboard(pipeline.Sha);
    }

    /// <summary>ContextMenu 的 DataContext 挂在 PlacementTarget 上，取法和平时的按钮不同。</summary>
    private Pipeline? MenuItemPipeline(object sender)
    {
        if (sender is MenuItem { DataContext: Pipeline pipeline }) return pipeline;
        // ContextMenu 里的元素继承的是 PlacementTarget 的 DataContext
        return (sender as FrameworkElement)?.DataContext as Pipeline;
    }

    // MARK: - 覆盖层

    private void ShowOverlay(UserControl view)
    {
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(view);
        OverlayHost.Visibility = Visibility.Visible;
    }

    private void CloseOverlay()
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        OverlayHost.Children.Clear();
    }

    // MARK: - Toast

    private void UpdateToast()
    {
        if (string.IsNullOrEmpty(_vm.Toast)) { ToastHost.Visibility = Visibility.Collapsed; return; }
        ToastText.Text = _vm.Toast;
        ToastIcon.Fill = _vm.ToastIsError
            ? (Brush)FindResource("FailedBrush")
            : (Brush)FindResource("PanelForegroundBrush");
        ToastHost.Visibility = Visibility.Visible;
    }

    // MARK: - 拖底边调高度

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击恢复默认高度
            _settings.PanelHeight = AppSettings.DefaultPanelHeight;
            Height = _settings.PanelHeight;
            return;
        }
        _resizing = true;
        _resizeStartY = (int)PointToScreen(e.GetPosition(this)).Y;
        _resizeStartHeight = (int)Height;
        _settings.BeginPanelHeightAdjustment();
        GripMark.Opacity = 1;
        CaptureMouse();
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            GripMark.Opacity = 1;
            return;
        }
        var y = (int)PointToScreen(e.GetPosition(this)).Y;
        var next = AppSettings.ClampHeight(_resizeStartHeight + (y - _resizeStartY), WorkAreaHeight());
        _settings.PanelHeight = next;
        Height = next;
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        _settings.EndPanelHeightAdjustment();
        GripMark.Opacity = 0;
        ReleaseMouseCapture();
    }

    private void Grip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_resizing) GripMark.Opacity = 0;
    }

    /// <summary>屏幕工作区高度：面板不能拖到屏幕外面去。</summary>
    private static int WorkAreaHeight()
    {
        var area = SystemParameters.WorkArea;
        return area.Height > 0 ? (int)area.Height : 1000;
    }

    // MARK: - 工具

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var deeper = FindVisualChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }
}
