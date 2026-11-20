using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;
using CoreAppInfo = GitLabPipelineMonitor.Core.AppInfo;
using Drawing = System.Drawing;
// 本文件显式导入 WinForms 与 System.Drawing（NotifyIcon 和画图标要用）。
// WPF 的同名类型（Application / MenuItem / Window / Size 等）逐个起别名，避免二义性。
using WpfApplication = System.Windows.Application;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;
using WpfWindow = System.Windows.Window;
using WpfSize = System.Windows.Size;
using WpfStyle = System.Windows.Style;
using WpfRect = System.Windows.Rect;
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;

namespace GitLabPipelineMonitor;

/// <summary>
/// 托盘图标：左键开合面板，右键出菜单，图标本身随整体状态变色，
/// 可选地画状态圆点和失败数量。图标是运行时用 GDI+ 画的 —— 不依赖任何图标库。
/// 右键菜单是自绘的 WPF ContextMenu（Win11 观感，隐式样式在 Theme/TrayMenu.xaml），
/// 不再用 WinForms 的 ContextMenuStrip（经典渲染，不跟 Win11 视觉也不跟明暗主题）。
/// .NET 8 的 NotifyIcon 拿不到托盘图标的屏幕坐标，菜单靠一个 1x1 锚点窗口定位到工作区右下角。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Core.AppState _state;
    private readonly AppSettings _settings;
    private readonly UpdateManager _updater;
    private readonly PanelWindow _panel;
    private readonly WindowsHost _host;
    private readonly WpfContextMenu _menu;
    // 1x1 隐形锚点窗口：菜单拿它当 PlacementTarget，停在工作区右下角、贴着任务栏上沿
    private readonly WpfWindow _anchor;

    // 图标缓存：状态组合有限，画过的复用，避免每次刷新都新建 GDI 句柄
    private readonly Dictionary<string, Drawing.Icon> _icons = new(StringComparer.Ordinal);

    public TrayIcon(Core.AppState state, AppSettings settings, UpdateManager updater, PanelWindow panel, WindowsHost host)
    {
        _state = state;
        _settings = settings;
        _updater = updater;
        _panel = panel;
        _host = host;

        _anchor = new WpfWindow
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            Title = CoreAppInfo.Name,
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Focusable = false,
            IsHitTestVisible = false,
        };

        _menu = new WpfContextMenu
        {
            // 代码新建、还没进视觉树的 ContextMenu 可能查不到隐式样式，显式回填同一个样式对象
            Style = ImplicitStyle(typeof(WpfContextMenu)),
        };
        _menu.Closed += (_, _) => _anchor.Hide();
        // 打开后用实际渲染尺寸再校一次越界（首次打开前的 DesiredSize 可能不准）
        _menu.Opened += (_, _) => CorrectMenuPosition();

        _icon = new NotifyIcon
        {
            Visible = true,
            Icon = LoadDefaultIcon(),
            Text = CoreAppInfo.Name
        };
        _icon.DoubleClick += (_, _) => TogglePanel();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        // 不再给 ContextMenuStrip 赋值（那是 WinForms 经典外观）；右键自己弹 WPF 菜单
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) OpenMenu(); };

        RebuildMenu();
        _state.IconNeedsUpdate += UpdateIcon;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Core.AppState.Aggregate) or nameof(Core.AppState.FailingCount)) UpdateIcon();
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.ShowStatusBadge) or nameof(AppSettings.ShowFailureCount)) UpdateIcon();
        };
        _updater.PropertyChanged += (_, e) =>
        {
            // UpdateManager 可能从后台线程发这个事件，WPF 菜单项必须回 UI 线程重建
            if (e.PropertyName == nameof(UpdateManager.LatestVersion))
                _ = _panel.Dispatcher.InvokeAsync(RebuildMenu);
        };

        // 通知：Core 通过宿主发，这里接住并显示成气泡
        _host.NotifyRequested += ShowBalloon;

        // 任务栏深浅变了要重画图标：线条色跟着任务栏走，不重画会在浅色任务栏上"隐身"
        ThemeManager.Changed += OnThemeChanged;
    }

    private void ShowBalloon(string title, string body, string id)
    {
        // 气泡要在 UI 线程上发；Core 的刷新跑在线程池上
        _ = _panel.Dispatcher.InvokeAsync(() => _icon.ShowBalloonTip(4000, title, body, ToolTipIcon.None));
    }

    /// <summary>从嵌入资源里取 exe 图标作为托盘图标的基础形状。</summary>
    private static Drawing.Icon LoadDefaultIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception) { /* 拿不到就用下面画的 */ }
        return Drawing.SystemIcons.Application;
    }

    // MARK: - 图标绘制

    public void UpdateIcon()
    {
        var aggregate = _state.Aggregate;
        var failing = _state.FailingCount;
        var badge = _settings.ShowStatusBadge;
        var count = badge && _settings.ShowFailureCount && failing > 0;
        var key = $"{aggregate}|{badge}|{(count ? failing : 0)}";

        var tooltip = BuildTooltip(aggregate, failing);
        _ = _panel.Dispatcher.InvokeAsync(() =>
        {
            _icon.Text = tooltip;
            if (_icons.TryGetValue(key, out var cached)) { _icon.Icon = cached; return; }
            var drawn = Draw(aggregate, failing, badge, count);
            _icons[key] = drawn;
            _icon.Icon = drawn;
        });
    }

    private string BuildTooltip(AggregateStatus aggregate, int failing)
    {
        var status = aggregate switch
        {
            AggregateStatus.Ok => Loc.T("所有流水线都通过了"),
            AggregateStatus.Running => Loc.T("运行中"),
            AggregateStatus.Pending => Loc.T("排队中"),
            AggregateStatus.Failed => Loc.T("已失败"),
            AggregateStatus.Error => Loc.T("连接失败"),
            _ => Loc.T("未配置")
        };
        var text = $"{CoreAppInfo.Name} · {status}";
        if (failing > 0) text += $" · {failing} {Loc.T("失败")}";
        // 托盘提示有 127 字符上限，超了会被截断
        return text.Length > 127 ? text[..127] : text;
    }

    private static Drawing.Icon Draw(AggregateStatus aggregate, int failing, bool badge, bool count)
    {
        // 托盘图标在 16 和 20（125% 缩放）下都要能看清，按 16 画并允许系统缩放
        const int size = 16;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 三节点流水线图形：线条和节点的颜色跟着任务栏深浅走。
            // Win11 浅色任务栏接近纯白，白线会在上面"隐身"，所以浅色用深灰、深色用近白。
            var light = ThemeManager.TaskbarIsLight;
            using var line = new Pen(light ? Color.FromArgb(64, 64, 64) : Color.FromArgb(235, 235, 240), 1.4f);
            var topLeft = new PointF(4.2f, 4.0f);
            var topRight = new PointF(11.8f, 4.0f);
            var bottom = new PointF(8.0f, 11.4f);
            g.DrawLine(line, topLeft, bottom);
            g.DrawLine(line, topRight, bottom);
            foreach (var point in new[] { topLeft, topRight, bottom })
            {
                using var node = new SolidBrush(light ? Color.FromArgb(64, 64, 64) : Color.FromArgb(245, 245, 250));
                g.FillEllipse(node, point.X - 2.1f, point.Y - 2.1f, 4.2f, 4.2f);
            }

            if (!badge) return ToIcon(bitmap);

            // 状态圆点画在右下角，一眼能看出整体状态；圆点和失败数字保持醒目，不跟深浅切换
            var color = ColorTranslator.FromHtml(aggregate.ColorHex());
            var rect = new RectangleF(8.4f, 8.4f, 7.2f, 7.2f);
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, rect);
            using (var ring = new Pen(Color.FromArgb(200, 255, 255, 255), 0.8f)) g.DrawEllipse(ring, rect);

            if (!count) return ToIcon(bitmap);

            // 失败数量：圆点很小，只放 1~2 位，超过 99 用 99+
            var label = failing > 99 ? "99" : failing.ToString();
            using (var font = new Font("Segoe UI", failing > 9 ? 4.6f : 5.4f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var text = new SolidBrush(Color.White))
            {
                var measured = g.MeasureString(label, font);
                g.DrawString(label, font, text,
                    rect.X + (rect.Width - measured.Width) / 2f,
                    rect.Y + (rect.Height - measured.Height) / 2f - 0.5f);
            }
        }
        return ToIcon(bitmap);
    }

    private static Drawing.Icon ToIcon(Bitmap bitmap)
    {
        // Bitmap.GetHicon 出来的句柄要自己管；这里交给 Icon 持有，程序退出时随进程释放
        var handle = bitmap.GetHicon();
        return Drawing.Icon.FromHandle(handle);
    }

    private void OnThemeChanged()
    {
        // 缓存键里没主题这一维：先清缓存再重画，否则会复用旧配色
        _ = _panel.Dispatcher.InvokeAsync(() =>
        {
            foreach (var icon in _icons.Values) icon.Dispose();
            _icons.Clear();
            UpdateIcon();
        });
    }

    // MARK: - 菜单

    /// <summary>取 App 资源里的隐式样式（Theme/TrayMenu.xaml）。查不到返回 null，菜单退回默认外观。</summary>
    private static WpfStyle? ImplicitStyle(Type type) => WpfApplication.Current.Resources[type] as WpfStyle;

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        if (_updater.LatestVersion is { } latest)
        {
            AddItem(Loc.T("更新到 {0}", latest), () => _updater.PresentUpdateNotice());
            AddSeparator();
        }

        AddItem(Loc.T("刷新"), () => _state.Refresh(force: true));
        AddItem(_settings.AutoRefreshPaused ? Loc.T("恢复自动刷新") : Loc.T("暂停自动刷新"),
            () => { _settings.AutoRefreshPaused = !_settings.AutoRefreshPaused; RebuildMenu(); });
        AddSeparator();
        AddItem(Loc.T("设置"), () => ((App)WpfApplication.Current).ShowSettings());
        AddItem(Loc.T("检查更新"), () => { _ = _updater.CheckAsync(force: true); });
        AddSeparator();
        AddItem(Loc.T("退出"), () => WpfApplication.Current.Shutdown());
    }

    private void AddItem(string header, Action onClick)
    {
        var item = new WpfMenuItem { Header = header, Style = ImplicitStyle(typeof(WpfMenuItem)) };
        item.Click += (_, _) => onClick();
        _menu.Items.Add(item);
    }

    private void AddSeparator() =>
        _menu.Items.Add(new WpfSeparator { Style = ImplicitStyle(typeof(WpfSeparator)) });

    /// <summary>右键托盘图标：按当前状态重建菜单，弹在工作区右下角（贴着任务栏上沿）。</summary>
    private void OpenMenu()
    {
        // 托盘事件可能从非 UI 线程回调，WPF 菜单要切回 Dispatcher 再开
        _ = _panel.Dispatcher.InvokeAsync(() =>
        {
            RebuildMenu();

            var work = System.Windows.SystemParameters.WorkArea;
            // .NET 8 的 NotifyIcon 没有 Location，拿不到图标真实坐标（和 ShowPanel 同样的取舍）：
            // 锚点窗口靠右、上沿比任务栏高 1px，菜单用 Placement=Top 从锚点向上长出来。
            _anchor.Left = work.Right - 1;
            _anchor.Top = work.Bottom - 1;
            _anchor.Show();

            _menu.PlacementTarget = _anchor;
            _menu.Placement = PlacementMode.Top;
            _menu.HorizontalOffset = 0;
            _menu.VerticalOffset = 0;

            // 先量尺寸，开之前就把越界校好；Opened 里再用实际渲染尺寸兜一次
            _menu.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
            ApplyMenuOffsets(work, _menu.DesiredSize.Width, _menu.DesiredSize.Height);
            _menu.IsOpen = true;
        });
    }

    private void CorrectMenuPosition()
    {
        if (_menu.ActualWidth <= 0) return;
        ApplyMenuOffsets(System.Windows.SystemParameters.WorkArea, _menu.ActualWidth, _menu.ActualHeight);
    }

    /// <summary>菜单右缘对齐工作区右缘（太宽则靠左摆）；太高则把顶边夹回工作区内。</summary>
    private void ApplyMenuOffsets(WpfRect work, double width, double height)
    {
        var left = work.Right - width - 2;
        if (left < work.Left + 2) left = work.Left + 2;
        _menu.HorizontalOffset = left - _anchor.Left;

        var top = _anchor.Top - height;
        if (top < work.Top + 2) _menu.VerticalOffset = work.Top + 2 + height - _anchor.Top;
    }

    // MARK: - 面板开合

    /// <summary>在托盘图标位置弹出面板：贴着屏幕右下角，不能超出工作区。</summary>
    public void TogglePanel()
    {
        if (_panel.IsVisible && _panel.IsActive) { _panel.HidePanel(); return; }
        ShowPanel();
    }

    public void ShowPanel()
    {
        var width = _settings.PanelWidth;
        var height = _settings.PanelHeight;

        // .NET 8 的 NotifyIcon 拿不到图标在屏幕上的坐标（只有 Win11 通知区才有，且不可编程获取），
        // 所以贴着工作区右下角展开 —— 这也是托盘面板最常见的落点。
        var workArea = System.Windows.SystemParameters.WorkArea;
        var left = workArea.Right - width;
        var top = workArea.Bottom - height;

        // 显示器缩放或工作区变化时兜一下，别让面板跑到屏幕外
        left = Math.Clamp(left, workArea.Left + 4, Math.Max(workArea.Left + 4, workArea.Right - width - 4));
        top = Math.Clamp(top, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - height - 4));

        _panel.ShowAt(left, top, width, height);
    }

    public void HidePanel() => _panel.Dispatcher.Invoke(HidePanelCore);

    private void HidePanelCore() => _panel.HidePanel();

    public void Dispose()
    {
        _state.IconNeedsUpdate -= UpdateIcon;
        ThemeManager.Changed -= OnThemeChanged;
        _menu.IsOpen = false;
        _icon.Visible = false;
        _icon.Dispose();
        _anchor.Close();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
