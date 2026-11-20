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

namespace GitLabPipelineMonitor;

/// <summary>
/// 托盘图标：左键开合面板，右键出菜单，图标本身随整体状态变色，
/// 可选地画状态圆点和失败数量。图标是运行时用 GDI+ 画的 —— 不依赖任何图标库。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Core.AppState _state;
    private readonly AppSettings _settings;
    private readonly UpdateManager _updater;
    private readonly PanelWindow _panel;
    private readonly WindowsHost _host;
    private readonly ContextMenuStrip _menu;

    // 图标缓存：状态组合有限，画过的复用，避免每次刷新都新建 GDI 句柄
    private readonly Dictionary<string, Drawing.Icon> _icons = new(StringComparer.Ordinal);

    public TrayIcon(Core.AppState state, AppSettings settings, UpdateManager updater, PanelWindow panel, WindowsHost host)
    {
        _state = state;
        _settings = settings;
        _updater = updater;
        _panel = panel;
        _host = host;

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _icon = new NotifyIcon
        {
            Visible = true,
            Icon = LoadDefaultIcon(),
            Text = CoreAppInfo.Name
        };
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => TogglePanel();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };

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
            if (e.PropertyName == nameof(UpdateManager.LatestVersion)) RebuildMenu();
        };

        // 通知：Core 通过宿主发，这里接住并显示成气泡
        _host.NotifyRequested += ShowBalloon;
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

            // 三节点流水线图形：白色描线 + 三个实心点，深色/浅色托盘上都能看清
            using var line = new Pen(Color.FromArgb(235, 235, 240), 1.4f);
            var topLeft = new PointF(4.2f, 4.0f);
            var topRight = new PointF(11.8f, 4.0f);
            var bottom = new PointF(8.0f, 11.4f);
            g.DrawLine(line, topLeft, bottom);
            g.DrawLine(line, topRight, bottom);
            foreach (var point in new[] { topLeft, topRight, bottom })
            {
                using var node = new SolidBrush(Color.FromArgb(245, 245, 250));
                g.FillEllipse(node, point.X - 2.1f, point.Y - 2.1f, 4.2f, 4.2f);
            }

            if (!badge) return ToIcon(bitmap);

            // 状态圆点画在右下角，一眼能看出整体状态
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

    // MARK: - 菜单

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        if (_updater.LatestVersion is { } latest)
        {
            _menu.Items.Add(Loc.T("更新到 {0}", latest), null, (_, _) => _updater.PresentUpdateNotice());
            _menu.Items.Add(new ToolStripSeparator());
        }

        _menu.Items.Add(Loc.T("刷新"), null, (_, _) => _state.Refresh(force: true));
        _menu.Items.Add(_settings.AutoRefreshPaused ? Loc.T("恢复自动刷新") : Loc.T("暂停自动刷新"), null,
            (_, _) => { _settings.AutoRefreshPaused = !_settings.AutoRefreshPaused; RebuildMenu(); });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Loc.T("设置"), null, (_, _) => ((App)System.Windows.Application.Current).ShowSettings());
        _menu.Items.Add(Loc.T("检查更新"), null, (_, _) => _ = _updater.CheckAsync(force: true));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Loc.T("退出"), null, (_, _) => System.Windows.Application.Current.Shutdown());
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
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
