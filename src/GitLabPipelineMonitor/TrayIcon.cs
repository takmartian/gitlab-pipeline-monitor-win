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
// WinForms 里也有 Application / MenuItem / ContextMenu 这些和 WPF 同名的类型，
// 所以 WPF 侧只保留实际引用到的别名，避免二义性。
using WpfApplication = System.Windows.Application;

namespace GitLabPipelineMonitor;

/// <summary>
/// 托盘图标：左键开合面板，右键出菜单，图标本身随整体状态变色，
/// 可选地画状态圆点和失败数量。图标是运行时用 GDI+ 画的 —— 不依赖任何图标库。
/// 右键菜单是自绘的 WPF 弹层（TrayMenu），观感对齐 Win11 的飞层菜单（Windows 安全中心那种），
/// 深色系统下就是深色 —— 系统原生菜单做不到这点，原因见 TrayMenu.cs 的注释。
/// 窗口内部的右键菜单（面板的流水线行、详情页的作业行）仍是 WPF ContextMenu，
/// 样式在 Theme/TrayMenu.xaml。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Core.AppState _state;
    private readonly AppSettings _settings;
    private readonly UpdateManager _updater;
    private readonly PanelWindow _panel;
    private readonly WindowsHost _host;
    // 托盘右键菜单：自绘的 Win11 飞层菜单
    private readonly TrayMenu _menu;

    // 图标缓存：状态组合有限，画过的复用，避免每次刷新都新建 GDI 句柄
    private readonly Dictionary<string, Drawing.Icon> _icons = new(StringComparer.Ordinal);

    public TrayIcon(Core.AppState state, AppSettings settings, UpdateManager updater, PanelWindow panel, WindowsHost host)
    {
        _state = state;
        _settings = settings;
        _updater = updater;
        _panel = panel;
        _host = host;

        // 托盘右键菜单：自绘的 Win11 飞层菜单（见 TrayMenu）。
        // 菜单是 WPF 弹层，不是模态的，弹完就还回 UI 线程。
        _menu = new TrayMenu();

        _icon = new NotifyIcon
        {
            Visible = true,
            Icon = LoadDefaultIcon(),
            Text = CoreAppInfo.Name
        };
        _icon.DoubleClick += (_, _) => TogglePanel();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        // 右键出菜单
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTrayMenu(); };

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

    /// <summary>托盘图标当前用的是哪一份，写进启动日志便于排查「图标看不见」这类问题。</summary>
    public string IconDescription { get; private set; } = "未创建";

    /// <summary>
    /// 托盘图标的基础形状。按可靠程度依次尝试：
    /// exe 里嵌入的图标 → 程序集里的 ico 资源 → 运行时画的图形 → 系统通用图标。
    /// 任何一步失败都要有下一档兜底，托盘图标没了整个应用就等于没了。
    /// </summary>
    private Drawing.Icon LoadDefaultIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is not null && extracted.Width > 0)
                {
                    IconDescription = $"exe 关联图标 {extracted.Width}x{extracted.Height}";
                    return extracted;
                }
            }
        }
        catch (Exception) { /* 换下一档 */ }

        try
        {
            // 程序集里打包的 Assets/app.ico，比 exe 关联图标更可靠（单文件发布时也一定在）
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var stream = WpfApplication.GetResourceStream(uri);
            if (stream is not null)
            {
                var icon = new Drawing.Icon(stream.Stream);
                IconDescription = $"内嵌 ico {icon.Width}x{icon.Height}";
                return icon;
            }
        }
        catch (Exception) { /* 换下一档 */ }

        IconDescription = "系统通用图标";
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

    private void RebuildMenu()
    {
        _menu.Clear();

        // 托盘菜单是一张平铺的列表，不画分隔线（Win11 飞层菜单就是这样）
        if (_updater.LatestVersion is { } latest)
        {
            _menu.Add(Loc.T("更新到 {0}", latest), () => _updater.PresentUpdateNotice());
        }

        _menu.Add(Loc.T("刷新"), () => _state.Refresh(force: true));

        // 自动刷新：文案固定，勾号表示开没开。
        // 以前是「暂停自动刷新 / 恢复自动刷新」两套文案来回切，文字会跳，
        // 还得点开才知道状态 —— 现在一个勾号就够。
        _menu.AddCheckable(Loc.T("自动刷新"), !_settings.AutoRefreshPaused,
            enabled => _settings.AutoRefreshPaused = !enabled);

        _menu.Add(Loc.T("设置"), () => ((App)WpfApplication.Current).ShowSettings());
        _menu.Add(Loc.T("检查更新"), () => { _ = _updater.CheckAsync(force: true); });
        _menu.Add(Loc.T("退出"), () => WpfApplication.Current.Shutdown());
    }

    /// <summary>右键托盘图标：在鼠标（托盘图标）位置弹菜单。</summary>
    private void ShowTrayMenu()
    {
        // 光标位置当场取：延到 Dispatcher 里再取，指针可能已经挪走了
        var location = Cursor.Position;
        // 托盘事件可能在非 UI 线程回调，菜单要在 UI 线程上弹
        _ = _panel.Dispatcher.InvokeAsync(() => ShowMenuAt(location));
    }

    /// <summary>
    /// 在指定屏幕位置弹托盘菜单。右键和诊断模式（--capture traymenu）走的是同一条路径。
    /// 菜单不是模态的：开完就返回，用户选完（或点外面）才关。
    /// </summary>
    internal void ShowMenuAt(Drawing.Point location)
    {
        RebuildMenu();
        _menu.Show(location);
    }

    /// <summary>菜单落点的自查信息：光标、工作区、锚点实际矩形、缩放系数（诊断用）。</summary>
    internal string DescribeMenuPlacement() =>
        $"光标 {_menu.CursorPoint} · 工作区 {_menu.WorkArea} · 锚点 {_menu.AnchorRect} · 缩放 {_menu.Scale:0.###} · {_menu.State} · 弹层 {_menu.PopupInfo}";

    /// <summary>把托盘菜单离屏渲染成 PNG（桌面锁屏抓不到屏时的兜底，也是观感自查）。</summary>
    internal bool RenderMenuToPng(string path) => _menu.RenderToPng(path);

    /// <summary>离屏渲染的详细说明：用了哪种渲染对象、出图的像素统计（诊断用）。</summary>
    internal string MenuRenderNote => _menu.RenderNote;

    /// <summary>托盘菜单用的画刷颜色（诊断用）。</summary>
    internal string MenuBrushDump => _menu.BrushDump;

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
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
