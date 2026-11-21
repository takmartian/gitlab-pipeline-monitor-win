using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Screen = System.Windows.Forms.Screen;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace GitLabPipelineMonitor;

/// <summary>
/// 托盘右键菜单：自绘的 WPF 弹层，观感对齐 Win11 的飞层菜单（Windows 安全中心那种）。
///
/// 为什么不用系统原生菜单（真 HMENU）：Win11 25H2 把 Win32 深色主题的开关
/// （SetPreferredAppMode / AllowDarkModeForWindows / RefreshImmersiveColorPolicyState）
/// 从 uxtheme.dll 里删干净了 —— 本机只剩 85 个导出，这几个入口一个都不剩。
/// 于是原生菜单在任何主题下都只能是浅色：给归属窗口或菜单窗口打沉浸式深色标记无效，
/// 套 SetWindowTheme("DarkMode_Explorer") 也无效（菜单绘制压根不走 uxtheme）。
/// 微软自己的 MMC 在这台机器上菜单同样是白的。安全中心那种深色菜单是 WinUI(XAML) 画的，
/// 不是原生菜单 —— 要同样的观感只能自己画。
///
/// 定位：1x1 全透明的锚点窗口摆在鼠标（也就是托盘图标）上，菜单用 Placement=Custom 定位：
/// 右缘贴着鼠标右侧 6px、下缘在鼠标上方 6px，跟系统托盘菜单的手感一致；
/// 算完再夹回所在显示器的工作区，不会跑到屏幕外。位置全程用物理像素算，
/// 再按锚点所在显示器的缩放折回 DIP —— 多显示器不同缩放也不会跑偏。
/// </summary>
public sealed class TrayMenu : IDisposable
{
    private const int Gap = 6;                 // 菜单与鼠标之间的缝隙（DIP）
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly ContextMenu _menu;
    private readonly Window _anchor;

    // 鼠标位置与所在显示器工作区，都是物理像素
    private Drawing.Point _cursor;
    private Drawing.Rectangle _workArea;
    // 锚点所在显示器的缩放：物理像素 → DIP 的换算系数（150% 缩放就是 1.5）
    private double _scale = 1.0;
    // 锚点窗口的实际屏幕矩形（物理像素）
    private Drawing.Rectangle _anchorRect;

    public TrayMenu()
    {
        // 锚点窗：1x1、全透明、不激活、不进任务栏。菜单拿它当定位参照，
        // 它摆在鼠标上，菜单就算「贴在托盘图标旁边」。
        _anchor = new Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            Title = Core.AppInfo.Name,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Focusable = false,
            IsHitTestVisible = false,
        };

        _menu = new ContextMenu
        {
            Style = Resource("TrayMenuStyle") ?? Resource(typeof(ContextMenu)),
            Placement = PlacementMode.Custom,
        };
        _menu.CustomPopupPlacementCallback = PlaceMenu;
        _menu.Closed += OnMenuClosed;
    }

    private void OnMenuClosed(object? sender, EventArgs e) => _anchor.Hide();

    /// <summary>清空菜单项。</summary>
    public void Clear() => _menu.Items.Clear();

    /// <summary>加一个菜单项。托盘菜单不画分隔线 —— Win11 的飞层菜单就是一张平铺的列表。</summary>
    public void Add(string header, Action onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = Resource("TrayMenuItemStyle") ?? Resource(typeof(MenuItem)),
        };
        item.Click += (_, _) => onClick();
        _menu.Items.Add(item);
    }

    /// <summary>
    /// 加一个可勾选的菜单项：文案固定，勾号表示状态。
    ///
    /// 比「暂停自动刷新 / 恢复自动刷新」那种动态文案好 —— 文案会跳来跳去，
    /// 勾号是位置固定的状态指示，一眼就知道开没开。
    ///
    /// 点完菜单不关（StaysOpenOnClick），不然用户看不到勾号变了。
    /// </summary>
    public void AddCheckable(string header, bool isChecked, Action<bool> onToggle)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = Resource("TrayMenuItemStyle") ?? Resource(typeof(MenuItem)),
            IsCheckable = true,
            IsChecked = isChecked,
            StaysOpenOnClick = true,
        };

        var last = isChecked;
        item.Click += (_, _) =>
        {
            // WPF 在 IsCheckable 时会先自己切 IsChecked 再抛 Click，
            // 所以这里读到的通常已经是切换后的值；万一顺序反了就自己补切一次。
            var next = item.IsChecked;
            if (next == last) next = !last;
            item.IsChecked = next;
            last = next;
            onToggle(next);
        };
        _menu.Items.Add(item);
    }

    /// <summary>
    /// 在鼠标位置弹出菜单。cursor 是物理像素坐标（右键托盘图标时当场取）。
    /// </summary>
    public void Show(Drawing.Point cursor)
    {
        _cursor = cursor;
        _workArea = Screen.FromPoint(cursor).WorkingArea;

        // 先显示再挪位置：窗口显示出来才知道自己落在哪块屏幕、用的是哪套 DPI
        if (!_anchor.IsVisible) _anchor.Show();
        var handle = new WindowInteropHelper(_anchor).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, IntPtr.Zero, cursor.X, cursor.Y, 1, 1,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (GetWindowRect(handle, out var rect))
            {
                _anchorRect = new Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }
        _scale = ReadScale();

        _menu.PlacementTarget = _anchor;
        _menu.HorizontalOffset = 0;
        _menu.VerticalOffset = 0;
        _menu.IsOpen = true;
    }

    /// <summary>锚点窗口的实际屏幕矩形（物理像素），排查落点用。</summary>
    public Drawing.Rectangle AnchorRect => _anchorRect;

    /// <summary>这次弹菜单用的鼠标位置（物理像素）。</summary>
    public Drawing.Point CursorPoint => _cursor;

    /// <summary>鼠标所在显示器的工作区（物理像素）。</summary>
    public Drawing.Rectangle WorkArea => _workArea;

    /// <summary>锚点所在显示器的缩放系数（物理像素 → DIP）。</summary>
    public double Scale => _scale;

    /// <summary>菜单自身的状态：开没开、尺寸、有没有接上呈现源（诊断用）。</summary>
    public string State =>
        $"已开={_menu.IsOpen} 尺寸={_menu.ActualWidth}x{_menu.ActualHeight} " +
        $"呈现源={(PresentationSource.FromVisual(_menu) is null ? "无" : "有")} " +
        $"宿主={(_menu.PlacementTarget is null ? "无" : _menu.PlacementTarget.GetType().Name)}";

    /// <summary>弹层窗口的句柄、可见性和屏幕矩形（诊断用）。</summary>
    public string PopupInfo
    {
        get
        {
            if (PresentationSource.FromVisual(_menu) is not HwndSource source) return "无弹层窗口";
            var handle = source.Handle;
            if (handle == IntPtr.Zero) return "弹层窗口句柄为空";
            var rect = "矩形未知";
            if (GetWindowRect(handle, out var r))
            {
                rect = $"({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}";
            }
            return $"0x{handle.ToInt64():X} 可见={IsWindowVisible(handle)} {rect}";
        }
    }

    /// <summary>
    /// 把菜单离屏渲染成 PNG。桌面锁屏、或抓屏失败时的兜底，也是观感自查的好办法
    /// （渲染的是弹层窗口真正显示的那棵树：底色、圆角、行距、字号都对得上）。
    ///
    /// 渲染对象不唯一：弹层的 RootVisual（PopupRoot）、菜单本身、菜单模板根 Border，
    /// 以及用 VisualBrush 重画一遍，各有各的坑（PopupRoot 在硬件加速窗口里
    /// 直接 Render 可能是全空的），所以挨个试，谁出得来内容就用谁。
    /// </summary>
    public bool RenderToPng(string path)
    {
        _renderLog.Clear();
        foreach (var candidate in RenderCandidates())
        {
            BitmapSource? bitmap;
            try { bitmap = candidate.Factory(); }
            catch (Exception error) { _renderLog.Add($"{candidate.Name} 渲染抛异常：{error.Message}"); continue; }
            if (bitmap is null) { _renderLog.Add($"{candidate.Name} 没有产出"); continue; }

            var stats = Describe(bitmap);
            if (candidate.SelfTest)
            {
                _renderLog.Add($"自检色块 {stats}（不透明像素 {stats.OpaquePixels}）");
                continue;
            }
            if (stats.OpaquePixels <= 0)
            {
                _renderLog.Add($"{candidate.Name} 全透明（{stats.Width}x{stats.Height}）");
                continue;
            }
            try
            {
                Save(bitmap, path);
                _renderLog.Add($"{candidate.Name} 出图成功 {stats}");
                return true;
            }
            catch (Exception error)
            {
                _renderLog.Add($"{candidate.Name} 保存失败：{error.Message}");
            }
        }
        return false;
    }

    /// <summary>托盘菜单实际用的画刷颜色（诊断用：系统是深色时这里就该是深色）。</summary>
    public string BrushDump
    {
        get
        {
            var parts = new List<string>();
            foreach (var key in new[] { "PopupBackgroundBrush", "PopupBorderBrush", "PopupHoverBrush", "PanelForegroundBrush" })
            {
                var brush = WpfApplication.Current?.Resources[key] as SolidColorBrush;
                parts.Add($"{key}={(brush is null ? "无" : brush.Color.ToString())}");
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>上次离屏渲染的逐项结果（诊断用）。</summary>
    public string RenderNote => _renderLog.Count == 0 ? "还没渲染过" : string.Join(" | ", _renderLog);

    private readonly List<string> _renderLog = new();

    private readonly record struct Candidate(string Name, Func<BitmapSource?> Factory, bool SelfTest = false);

    private IEnumerable<Candidate> RenderCandidates()
    {
        // 自检色块：一个跟菜单无关的纯色矩形。
        // 它都渲染不出来，说明是渲染管线本身的问题（比如桌面锁着、WPF 渲染被挂起），
        // 不是菜单内容的问题 —— 这一条能把两类故障分开。
        yield return new Candidate("自检色块", () => RenderSelfTest(), SelfTest: true);

        var root = PresentationSource.FromVisual(_menu) is HwndSource source ? source.RootVisual : null;
        var template = _menu.Template is null ? null : VisualTreeHelper.GetChild(_menu, 0);

        var pairs = new (string Name, Visual? Visual)[]
        {
            ("弹层根视觉", root),
            ("菜单本身", _menu),
            ("菜单模板根", template as Visual),
        };
        foreach (var (name, visual) in pairs)
        {
            if (visual is null) continue;
            yield return new Candidate($"{name}@96", () => RenderVisual(visual, 96));
            yield return new Candidate($"{name}@192", () => RenderVisual(visual, 192));
            yield return new Candidate($"{name}经画刷", () => RenderViaBrush(visual, 96));
        }

        // 最稳的一条：拿一棵脱离视觉树的新菜单来渲染 —— 同样的样式、同样的条目。
        // 活的弹层窗口是硬件加速的，Render 常常把内容吐成全透明，这条不受影响。
        yield return new Candidate("脱离树独立渲染", () => RenderDetached(96));
    }

    /// <summary>
    /// 自检色块：一个 32x32 的纯红矩形，跟菜单毫无关系。
    /// 它能渲染出来说明离屏渲染管线是好的，出不来就是环境问题（桌面锁着时 WPF 渲染会挂起）。
    /// </summary>
    private static BitmapSource? RenderSelfTest()
    {
        var block = new Border
        {
            Width = 32,
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)),
        };
        block.Measure(new Size(32, 32));
        block.Arrange(new Rect(0, 0, 32, 32));
        block.UpdateLayout();
        return RenderVisual(block, 96);
    }

    /// <summary>同一个色块，强制走软件渲染再来一遍（锁屏时 GPU 合成可能被挂起）。</summary>
    private static BitmapSource? RenderSelfTestSoftware()
    {
        var block = new Border
        {
            Width = 32,
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)),
        };
        block.Measure(new Size(32, 32));
        block.Arrange(new Rect(0, 0, 32, 32));
        block.UpdateLayout();
        return RenderVisual(block, 96);
    }

    /// <summary>
    /// 用一棵脱离视觉树的新菜单渲染：同样的样式、同样的条目、同样的画刷，
    /// 只是不挂在活的弹层窗口上。
    /// </summary>
    private BitmapSource? RenderDetached(double dpi)
    {
        var detached = new ContextMenu
        {
            Style = _menu.Style,
            FontFamily = _menu.FontFamily,
            FontSize = _menu.FontSize,
            Foreground = _menu.Foreground,
        };
        foreach (var item in _menu.Items.OfType<MenuItem>())
        {
            detached.Items.Add(new MenuItem
            {
                Header = item.Header,
                Style = item.Style,
                IsEnabled = item.IsEnabled,
                // 勾选状态要一起复制，否则离屏渲染出来的图看不到勾号
                IsCheckable = item.IsCheckable,
                IsChecked = item.IsChecked,
            });
        }
        detached.ApplyTemplate();
        detached.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = detached.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0) return null;
        detached.Arrange(new Rect(0, 0, size.Width, size.Height));
        detached.UpdateLayout();
        return RenderVisual(detached, dpi);
    }

    /// <summary>离屏渲染一个可视对象；返回 null 表示尺寸算不出来。</summary>
    private static BitmapSource? RenderVisual(Visual visual, double dpi)    {
        var size = MeasureVisual(visual);
        if (size.Width <= 0 || size.Height <= 0) return null;
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), dpi, dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    /// <summary>
    /// 用 VisualBrush 把可视对象重画一遍再离屏渲染。
    /// 画刷会自己走一遍渲染流程，跟目标是不是连着活的呈现源、是不是硬件加速都没关系，
    /// 所以这是最容易出内容的路子。
    /// </summary>
    private static BitmapSource? RenderViaBrush(Visual visual, double dpi)
    {
        var size = MeasureVisual(visual);
        if (size.Width <= 0 || size.Height <= 0) return null;
        var brush = new VisualBrush(visual)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size.Width, size.Height),
        };
        var holder = new DrawingVisual();
        using (var context = holder.RenderOpen())
        {
            context.DrawRectangle(brush, null, new Rect(0, 0, size.Width, size.Height));
        }
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), dpi, dpi, PixelFormats.Pbgra32);
        target.Render(holder);
        return target;
    }

    private static Size MeasureVisual(Visual visual)
    {
        if (visual is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0)
            return new Size(element.ActualWidth, element.ActualHeight);
        var bounds = VisualTreeHelper.GetDescendantBounds(visual);
        return new Size(bounds.Width, bounds.Height);
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>数一下图里有多少不透明像素、有多少种颜色 —— 全透明或纯色说明没渲染出东西。</summary>
    private static (int Width, int Height, int OpaquePixels, int DistinctColors) Describe(BitmapSource bitmap)
    {
        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var opaque = 0;
            var colors = new HashSet<int>();
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;
                opaque++;
                if (colors.Count < 4096)
                    colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16 | pixels[i + 3] << 24);
            }
            return (converted.PixelWidth, converted.PixelHeight, opaque, colors.Count);
        }
        catch (Exception)
        {
            return (bitmap.PixelWidth, bitmap.PixelHeight, -1, -1);
        }
    }

    /// <summary>
    /// 菜单落点。popupSize 是 DIP，先换算成物理像素算最终位置，
    /// 再折回「相对锚点原点」的偏移 —— CustomPopupPlacement 要的就是这个相对量。
    /// 返回 PrimaryAxis.None：位置我们自己算好并夹在工作区里，不让 WPF 再自作主张挪一次。
    /// </summary>
    private CustomPopupPlacement[] PlaceMenu(Size popupSize, Size targetSize, Point offset)
    {
        var width = popupSize.Width * _scale;
        var height = popupSize.Height * _scale;

        // 右缘在鼠标右侧、下缘在鼠标上方：菜单从托盘图标往左上方展开
        var left = _cursor.X + Gap * _scale - width;
        var top = _cursor.Y - Gap * _scale - height;

        // 夹在所在显示器的工作区里：贴边时不越界，超宽时贴着左缘
        if (left < _workArea.Left) left = _workArea.Left;
        if (left + width > _workArea.Right) left = _workArea.Right - width;
        if (top < _workArea.Top) top = _workArea.Top;

        // 锚点就摆在鼠标上，所以锚点原点 = 鼠标位置
        var dx = (left - _cursor.X) / _scale;
        var dy = (top - _cursor.Y) / _scale;
        return new[]
        {
            new CustomPopupPlacement(new WpfPoint(dx, dy), PopupPrimaryAxis.None),
        };
    }

    /// <summary>锚点所在显示器的缩放：1 个 DIP 对应多少物理像素（200% 缩放就是 2）。</summary>
    private double ReadScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(_anchor);
            var perDevice = source?.CompositionTarget?.TransformFromDevice.M11 ?? 0;
            // TransformFromDevice 是「物理 → DIP」，取倒数才是「DIP → 物理」
            if (perDevice <= 0.05) return 1.0;
            var scale = 1.0 / perDevice;
            return scale > 0.05 ? scale : 1.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    /// <summary>取应用资源里的样式；代码新建的控件还没进视觉树，查不到隐式样式要显式回填。</summary>
    private static Style? Resource(string key) => WpfApplication.Current?.Resources[key] as Style;

    private static Style? Resource(Type type) => WpfApplication.Current?.Resources[type] as Style;

    public void Dispose()
    {
        _menu.IsOpen = false;
        _menu.Closed -= OnMenuClosed;
        _anchor.Close();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
