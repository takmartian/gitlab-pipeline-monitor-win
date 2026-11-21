using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.Platform;
using GitLabPipelineMonitor.Core.Storage;
using GitLabPipelineMonitor.ViewModels;

namespace GitLabPipelineMonitor.Views;

/// <summary>
/// 屏幕边缘浮标。
///
/// Windows 的托盘图标一多就被收进「隐藏的图标」里，这时候就看不见有没有流水线在跑。
/// 这个浮标就是补这一块：平时缩在屏幕右边缘，只留一根细条，
/// 有流水线在跑就变成蓝色并脉动，有失败就变红，什么都没有就几乎透明。
/// 鼠标靠近就展开成完整浮标（状态摘要 + 在跑/失败的流水线），点一下打开完整面板。
///
/// 三件事都靠同一份数据：<see cref="Core.AppState"/> 里托盘图标本来就在用的那份状态，
/// 浮标不额外发请求，「实时」的延迟就是刷新间隔。
///
/// 位置用**物理像素**算（见 <see cref="ScreenPlacement"/>）：多显示器、不同缩放的机器上
/// DIP 说不清「在屏幕哪儿」。用户可以把它拖到任意一块屏幕，松手就吸附到那块屏的右边缘。
/// </summary>
public partial class FloatingBadgeWindow : Window
{
    /// <summary>折叠时的窗口宽度（DIP）：细条加上一点余量。</summary>
    private const double CollapsedWidth = 7;

    /// <summary>展开时的窗口宽度（DIP）：面板 + 细条。</summary>
    private const double ExpandedWidth = 272;

    /// <summary>判定「鼠标靠近了」的扩展范围（DIP）：细条太细，得往外放一圈才点得中。</summary>
    private const double HoverPadLeft = 56;
    private const double HoverPadTop = 30;
    private const double HoverPadBottom = 30;
    private const double HoverPadRight = 24;

    /// <summary>鼠标移开后过多久收起（毫秒）。</summary>
    private const int CollapseDelayMs = 500;

    /// <summary>拖动超过这个距离（物理像素）就算「在拖」，不算点击。</summary>
    private const int DragThreshold = 3;

    private readonly AppSettings _settings;
    private readonly PanelWindow _panel;
    private readonly TrayIcon _tray;
    private readonly FloatingBadgeViewModel _vm;
    private readonly DispatcherTimer _poll;

    private bool _expanded;
    private bool _pinned;
    private bool _panelOpen;
    private bool _dragging;
    private bool _dragMoved;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private int _dragStartX;
    private int _dragStartY;
    private DateTime _collapseAt = DateTime.MinValue;
    private DoubleAnimation? _pulse;

    public FloatingBadgeWindow(Core.AppState state, AppSettings settings, PanelWindow panel, TrayIcon tray)
    {
        _settings = settings;
        _panel = panel;
        _tray = tray;
        _vm = new FloatingBadgeViewModel(state);

        InitializeComponent();
        DataContext = _vm;
        // 高度交给 SizeToContent（折叠时就是细条那么高，展开时按内容长），
        // 宽度由代码在折叠/展开之间切
        Width = CollapsedWidth;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FloatingBadgeViewModel.HasRunning) or nameof(FloatingBadgeViewModel.HasFailed))
                UpdateStripVisual();
        };
        ThemeManager.Changed += OnThemeChanged;

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>当前是不是展开的（诊断用）。</summary>
    public bool IsExpanded => _expanded;

    /// <summary>本窗口的原生句柄（还没创建时是零）。</summary>
    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>浮标所在的显示器（诊断用）。</summary>
    public string CurrentScreen => _settings.FloatingBadgeScreen;

    /// <summary>重新贴到该在的位置（显示出来之后调用一次）。</summary>
    public void Reposition()
    {
        if (!IsVisible) return;
        PlaceInitial();
    }

    /// <summary>位置自查（诊断用）：实际矩形跟工作区差多少、右沿贴没贴齐。</summary>
    public string DescribePlacement()
    {
        var monitor = ResolveMonitor();
        var work = monitor.WorkArea;
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        return $"屏幕 {monitor.Key}（缩放 {monitor.Scale:0.##}）· 工作区 {work}"
             + $" · 浮标 {rect} · 右沿差 {work.Right - rect.Right:0.#} 像素"
             + $" · 纵向 {rect.Top - work.Top:0.#}/{work.Height - rect.Height:0.#}"
             + $" · 展开={_expanded}";
    }

    /// <summary>把浮标挪到指定物理坐标（诊断用，模拟拖到别处）。</summary>
    public void MoveForProbe(int x, int y) => ScreenPlacement.MoveTo(Hwnd, x, y);

    /// <summary>本窗口的原生句柄（诊断用）。</summary>
    public IntPtr NativeHandle => Hwnd;

    /// <summary>
    /// 诊断用：强制展开，并摆到屏幕左上角一小块地方，方便抓图看观感。
    /// 平时不会走到这里 —— 展开是靠鼠标靠近触发的。
    /// </summary>
    public void ExpandForCapture()
    {
        _pinned = true;
        SetExpanded(true);
        var work = ScreenPlacement.Primary().WorkArea;
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        if (rect.Width <= 0) return;
        ScreenPlacement.MoveTo(Hwnd, (int)(work.Left + 60), (int)(work.Top + 60));
    }

    /// <summary>诊断用：折叠态（就一根细条），贴在工作区右边缘。</summary>
    public void ShowStripForCapture()
    {
        _pinned = true;
        SetExpanded(false);
        var work = ScreenPlacement.Primary().WorkArea;
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        if (rect.Width <= 0) return;
        ScreenPlacement.MoveTo(Hwnd, (int)(work.Right - rect.Width), (int)(work.Top + 60));
    }

    // MARK: - 生命周期

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 常驻在屏幕边上的小东西，鼠标扫过去不该把用户正在用的窗口抢掉焦点
        ScreenPlacement.MakeNoActivate(Hwnd);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PlaceInitial();
        _vm.Rebuild();
        UpdateStripVisual();
        _poll.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        ThemeManager.Changed -= OnThemeChanged;
    }

    /// <summary>数据变了就重建列表。可能在后台线程被调到，切回 UI 线程。</summary>
    public void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        _vm.Rebuild();
    }

    // MARK: - 摆放

    /// <summary>浮标该待在哪块屏：用户拖到哪儿就记哪儿，屏没了就回主屏。</summary>
    private MonitorInfo ResolveMonitor()
    {
        var name = _settings.FloatingBadgeScreen;
        if (!string.IsNullOrEmpty(name))
        {
            var found = ScreenPlacement.ByName(name);
            if (found is not null) return found;
        }
        return ScreenPlacement.Primary();
    }

    /// <summary>初次摆放：贴到目标屏的右边缘，纵向按上次记的比例。</summary>
    private void PlaceInitial()
    {
        var monitor = ResolveMonitor();
        var work = monitor.WorkArea;
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var desiredTop = work.Top + (work.Height - rect.Height) * _settings.FloatingBadgeTopRatio;
        var snapped = ScreenSnap.SnapToRightEdge(work, rect.Width, rect.Height, desiredTop);
        ScreenPlacement.MoveTo(Hwnd, (int)Math.Round(snapped.Left), (int)Math.Round(snapped.Top));
    }

    /// <summary>
    /// 把窗口重新贴回目标屏的右边缘，纵向夹在工作区里。
    /// 分辨率变了、缩放变了、显示器拔了，浮标都得跟着回到该在的位置。
    /// </summary>
    private void EnsureSnapped(MonitorInfo monitor)
    {
        var work = monitor.WorkArea;
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var left = work.Right - rect.Width;
        var maxTop = Math.Max(work.Top, work.Bottom - rect.Height);
        var top = ScreenSnap.Clamp(rect.Top, work.Top, maxTop);
        if (Math.Abs(left - rect.Left) < 0.5 && Math.Abs(top - rect.Top) < 0.5) return;
        ScreenPlacement.MoveTo(Hwnd, (int)Math.Round(left), (int)Math.Round(top));
    }

    // MARK: - 展开与收起

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        Panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Width = expanded ? ExpandedWidth : CollapsedWidth;
        // 宽度一变物理尺寸就变了，右边缘得重新贴一次
        EnsureSnapped(ResolveMonitor());
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EnsureSnapped(ResolveMonitor()));
    }

    /// <summary>每 90ms 看一次鼠标位置：靠近就展开，移开一会儿就收起。</summary>
    private void Poll()
    {
        if (_dragging) return;

        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        if (rect.Width <= 0) return;

        var (cx, cy) = ScreenPlacement.GetCursorPosition();
        var monitor = ResolveMonitor();

        // _pinned 只在诊断抓图时用：那时不让悬停逻辑把浮标收起来，
        // 但位置自保照常跑 —— 否则探针挪走浮标后就贴不回右边缘了。
        if (!_pinned)
        {
            // 完整面板开着的时候浮标让位：面板也是贴着右下角的，
            // 两个叠在一起会互相盖住。这时浮标退回细条、也不再置顶，
            // 鼠标判定也先不做（免得面板刚关浮标就自己弹开）。
            var panelOpen = _panel.IsVisible;
            if (panelOpen != _panelOpen)
            {
                _panelOpen = panelOpen;
                Topmost = !panelOpen;
            }
            if (panelOpen)
            {
                if (_expanded) SetExpanded(false);
                EnsureSnapped(monitor);
                return;
            }

            bool hovering;
            if (_expanded)
            {
                hovering = rect.Contains(cx, cy);
            }
            else
            {
                hovering = ScreenSnap.NearStrip(rect, cx, cy, monitor.Scale,
                    HoverPadLeft, HoverPadTop, HoverPadBottom, HoverPadRight);
            }

            if (hovering)
            {
                _collapseAt = DateTime.MinValue;
                if (!_expanded) SetExpanded(true);
            }
            else if (_expanded)
            {
                if (_collapseAt == DateTime.MinValue) _collapseAt = DateTime.Now.AddMilliseconds(CollapseDelayMs);
                else if (DateTime.Now >= _collapseAt) SetExpanded(false);
            }
        }

        EnsureSnapped(monitor);
    }

    // MARK: - 拖动

    private void Strip_MouseDown(object sender, MouseButtonEventArgs e) => BeginDrag(sender, e);

    private void Strip_MouseMove(object sender, MouseEventArgs e) => ContinueDrag(e);

    private void Strip_MouseUp(object sender, MouseButtonEventArgs e) => EndDrag(sender, e);

    private void BeginDrag(object? sender, MouseButtonEventArgs e)
    {
        var (cx, cy) = ScreenPlacement.GetCursorPosition();
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        _dragOffsetX = cx - (int)rect.Left;
        _dragOffsetY = cy - (int)rect.Top;
        _dragStartX = cx;
        _dragStartY = cy;
        _dragging = true;
        _dragMoved = false;

        // 捕获到按下鼠标的那个元素上：拖动时指针会离开它，
        // 捕获到窗口的话挂在元素上的 MouseMove / MouseUp 就收不到了
        if (sender is UIElement element) element.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(MouseEventArgs e)
    {
        if (!_dragging) return;
        var (cx, cy) = ScreenPlacement.GetCursorPosition();
        if (!_dragMoved && (Math.Abs(cx - _dragStartX) > DragThreshold || Math.Abs(cy - _dragStartY) > DragThreshold))
            _dragMoved = true;
        if (!_dragMoved) return;
        ScreenPlacement.MoveTo(new WindowInteropHelper(this).Handle, cx - _dragOffsetX, cy - _dragOffsetY);
    }

    private void EndDrag(object? sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (sender is UIElement element) element.ReleaseMouseCapture();

        if (!_dragMoved)
        {
            // 没怎么动 = 点击：打开完整面板
            OpenFullPanel();
            return;
        }

        // 松手在哪块屏，就吸附到那块屏的右边缘
        var (cx, cy) = ScreenPlacement.GetCursorPosition();
        var monitor = ScreenPlacement.At(cx, cy);
        _settings.FloatingBadgeScreen = monitor.Key;
        EnsureSnapped(monitor);
        SaveVerticalPosition(monitor);
    }

    /// <summary>记下纵向位置（工作区高度的比例），下次启动还在这儿。</summary>
    private void SaveVerticalPosition(MonitorInfo monitor)
    {
        var work = monitor.WorkArea;
        var rect = ScreenPlacement.GetWindowRect(Hwnd);
        var span = work.Height - rect.Height;
        var ratio = span > 1 ? (rect.Top - work.Top) / span : 0.5;
        _settings.FloatingBadgeTopRatio = Math.Clamp(ratio, 0, 1);
    }

    // MARK: - 交互

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int pipelineId }) return;
        OpenFullPanel();
        _panel.ShowWithPipeline(pipelineId);
    }

    /// <summary>打开完整面板（跟点托盘图标一样）。</summary>
    private void OpenFullPanel() => _tray.ShowPanel();

    // MARK: - 外观

    /// <summary>
    /// 细条的颜色：蓝=有在跑的（还带脉动），红=有失败但没在跑的，其余几乎透明。
    /// 只让这两种状态冒出来，免得跟托盘图标显示的信息重复。
    /// </summary>
    private void UpdateStripVisual()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateStripVisual);
            return;
        }

        var running = _vm.HasRunning;
        var failed = _vm.HasFailed;
        var key = running ? "RunningBrush" : failed ? "FailedBrush" : "MutedBrush";
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            Strip.Background = brush;
            HeaderDot.Fill = brush;
        }

        if (running)
        {
            _pulse ??= new DoubleAnimation
            {
                From = 0.45,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.85)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Strip.BeginAnimation(OpacityProperty, _pulse);
        }
        else
        {
            Strip.BeginAnimation(OpacityProperty, null);
            Strip.Opacity = failed ? 0.9 : 0.22;
        }
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateStripVisual);
            return;
        }
        UpdateStripVisual();
    }
}
