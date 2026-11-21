using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GitLabPipelineMonitor;

/// <summary>
/// 明暗主题。WPF 的 SystemColors 在 Win11 上不会读「应用使用浅色主题」那个开关，
/// 永远是浅色那套值 —— 所以深浅要自己判定、自己上色。
///
/// 关键实现细节（本机实测踩出来的坑）：**任何放进 Application.Resources 的画刷都会被 WPF
/// 立刻冻结**（合并字典里的也一样），冻结后改 Color 会抛 InvalidOperationException；
/// 就算跳过不抛，主题也永远切不动 —— 应用会一直停在 XAML 里的浅色默认值上，
/// 表现为「深色系统下菜单一直是白的」。
///
/// 所以这里不往应用资源里塞「一份长期不变的画刷」，而是每次换主题就整份重建：
/// 新建画刷 → 上色 → 把这份字典换进 Application.Resources.MergedDictionaries。
/// 换字典会触发 DynamicResource 重新解析，界面上所有引用同一时间跟着变。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>所有由代码托管的主题画刷。键名与 Theme/Colors.xaml 保持一致。</summary>
    private static readonly string[] BrushKeys =
    {
        "PanelBackgroundBrush", "PanelForegroundBrush", "BarBackgroundBrush",
        "SecondaryTextBrush", "TertiaryTextBrush", "RowHoverBrush",
        "FieldBackgroundBrush", "HairlineBrush",
        "PopupBackgroundBrush", "PopupBorderBrush", "PopupHoverBrush",
        "AccentBrush", "AccentTextBrush", "AccentForegroundBrush",
        "ControlBackgroundBrush", "ControlBorderBrush", "ControlHoverBrush",
        "ControlPressedBrush", "DisabledTextBrush", "TabSelectedBrush", "ScrollThumbBrush",
        "SuccessBrush", "FailedBrush", "RunningBrush", "PendingBrush",
        "CanceledBrush", "ManualBrush", "BlockedBrush", "MutedBrush", "StarBrush",
    };

    /// <summary>当前是不是深色主题（应用自己的深浅，看 AppsUseLightTheme）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>任务栏/托盘是不是浅色（SystemUsesLightTheme）—— 决定托盘图标画深线还是白线。</summary>
    public static bool TaskbarIsLight { get; private set; } = true;

    /// <summary>系统强调色，取不到就用 Windows 默认的蓝。</summary>
    public static Color Accent { get; private set; } = Color.FromRgb(0, 120, 212);

    /// <summary>主题（明暗或强调色）变了。托盘图标的线条色取决于任务栏深浅，订阅这个重画。</summary>
    public static event Action? Changed;

    // SystemEvents 是静态事件，必须拿住委托引用，否则被 GC 后再也收不到通知
    private static UserPreferenceChangedEventHandler? _watcher;

    /// <summary>当前挂进应用资源的那份主题字典。换主题时整份换掉。</summary>
    private static ResourceDictionary? _theme;

    /// <summary>读一次系统状态并把配色打到应用资源上；同时开始盯着主题切换。</summary>
    public static void Install()
    {
        RefreshFlags();
        Rebuild();

        // 不挑类别：换主题、换强调色、改明暗开关都从这里过一遍，宁可多刷一次
        _watcher = (_, _) =>
        {
            RefreshFlags();
            Rebuild();
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += _watcher;
    }

    /// <summary>
    /// 重建一份主题字典（新建画刷 → 上色）并换进应用资源。
    /// 画刷是先上色后入字典的：入字典就会被冻结，那时再改就来不及了。
    /// </summary>
    private static void Rebuild()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary();
        foreach (var key in BrushKeys)
        {
            dictionary[key] = new SolidColorBrush(Colors.Black) { Opacity = 1 };
        }
        Paint(dictionary);

        // 先摘旧的再挂新的：合并字典里后挂的优先级更高
        if (_theme is not null)
        {
            try { app.Resources.MergedDictionaries.Remove(_theme); }
            catch (Exception) { /* 字典已经被动过就算了 */ }
        }
        app.Resources.MergedDictionaries.Add(dictionary);
        _theme = dictionary;

        // 通知订阅者配色变了（托盘图标要按新的任务栏深浅重画）
        Changed?.Invoke();
    }

    private static void RefreshFlags()
    {
        IsDark = ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
        TaskbarIsLight = ReadDword(PersonalizeKey, "SystemUsesLightTheme", 1) == 1;
        var accent = ReadDword(DwmKey, "AccentColor", 0);
        if (accent != 0)
        {
            // 注册表里是 0x00BBGGRR（低 24 位），不是 ARGB
            Accent = Color.FromRgb((byte)((accent >> 16) & 0xFF), (byte)((accent >> 8) & 0xFF), (byte)(accent & 0xFF));
        }
        // 深色底上系统给的那个强调色往往太暗，往白提一点才看得清（Win11 自己也是这么干的）
        if (IsDark) Accent = Blend(Accent, Colors.White, 0.28);
    }

    /// <summary>
    /// 把当前主题的配色打到字典里的画刷上。
    /// 取值照着 Win11 的深/浅两套来：主底、条栏、正文、次级、三级、悬停、字段底、分隔线。
    /// </summary>
    private static void Paint(ResourceDictionary dictionary)
    {
        var dark = IsDark;

        Paint(dictionary, "PanelBackgroundBrush", dark ? "#1F1F1F" : "#FFFFFF", 1);
        Paint(dictionary, "BarBackgroundBrush", dark ? "#272727" : "#F3F3F3", 1);
        Paint(dictionary, "PanelForegroundBrush", dark ? "#FFFFFF" : "#1A1A1A", 1);
        Paint(dictionary, "SecondaryTextBrush", dark ? "#FFFFFF" : "#1A1A1A", dark ? 0.72 : 0.62);
        Paint(dictionary, "TertiaryTextBrush", dark ? "#FFFFFF" : "#1A1A1A", dark ? 0.55 : 0.45);
        Paint(dictionary, "RowHoverBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.07 : 0.05);
        Paint(dictionary, "FieldBackgroundBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.06 : 0.05);
        Paint(dictionary, "HairlineBrush", dark ? "#FFFFFF" : "#000000", 0.10);

        // 弹出层（托盘菜单）：Win11 的 flyout 比窗口底再亮一档 / 再白一档
        Paint(dictionary, "PopupBackgroundBrush", dark ? "#2C2C2C" : "#FDFDFD", 1);
        Paint(dictionary, "PopupBorderBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.14 : 0.12);
        Paint(dictionary, "PopupHoverBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.08 : 0.055);
        Paint(dictionary, "AccentBrush", ToHex(Accent), 1);
        Paint(dictionary, "AccentTextBrush", "#FFFFFF", 1);
        // 强调色上的前景色（勾、按钮文字）：强调色浅时用深色字，深色时用白字。
        // 本机强调色是那种很亮的黄，白勾白字压在上面对比度根本不够。
        Paint(dictionary, "AccentForegroundBrush", AccentIsLight() ? "#1A1A1A" : "#FFFFFF", 1);

        // 控件外壳：WPF 自带主题在深色下仍是浅色，这几档由我们自己给
        Paint(dictionary, "ControlBackgroundBrush", dark ? "#2D2D2D" : "#FDFDFD", 1);
        Paint(dictionary, "ControlBorderBrush", dark ? "#FFFFFF" : "#D9D9D9", dark ? 0.16 : 1);
        Paint(dictionary, "ControlHoverBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.06 : 0.04);
        Paint(dictionary, "ControlPressedBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.04 : 0.08);
        Paint(dictionary, "DisabledTextBrush", dark ? "#FFFFFF" : "#000000", 0.36);
        Paint(dictionary, "TabSelectedBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.08 : 0.06);
        Paint(dictionary, "ScrollThumbBrush", dark ? "#FFFFFF" : "#000000", 0.28);

        // 状态色：浅色沿用与 macOS 版一致的取值，深色底上要提亮才跳得出来
        Paint(dictionary, "SuccessBrush", dark ? "#3DDC7A" : "#33B85C", 1);
        Paint(dictionary, "FailedBrush", dark ? "#FF6B6B" : "#F04545", 1);
        Paint(dictionary, "RunningBrush", dark ? "#4DA3FF" : "#388CFA", 1);
        Paint(dictionary, "PendingBrush", dark ? "#FFB84D" : "#F5A624", 1);
        Paint(dictionary, "CanceledBrush", dark ? "#A5ADB6" : "#8C949E", 1);
        Paint(dictionary, "ManualBrush", dark ? "#B18CF0" : "#9E73E6", 1);
        Paint(dictionary, "BlockedBrush", dark ? "#E8884A" : "#D97333", 1);
        Paint(dictionary, "MutedBrush", dark ? "#A5ADB6" : "#8E8E93", 1);
        Paint(dictionary, "StarBrush", dark ? "#FFC94D" : "#F5B833", 1);
    }

    /// <summary>改一个画刷的颜色（把不透明度折算进 alpha，免得和 brush.Opacity 叠乘）。</summary>
    private static void Paint(ResourceDictionary dictionary, string key, string hex, double opacity)
    {
        if (dictionary[key] is not SolidColorBrush brush) return;
        if (brush.IsFrozen) return;   // 入字典前就冻结了，这版配色上不去（不该发生）
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = (byte)(255 * Math.Clamp(opacity, 0, 1));
        brush.Color = color;
    }

    /// <summary>
    /// 给窗口套上深色标题栏。
    ///
    /// Win11 的应用标题栏不跟系统深浅走 —— 默认永远是浅色那套，深色系统下
    /// 浅标题栏配深色内容非常扎眼（设置窗口、更新弹窗都是这样）。
    /// 得显式告诉 DWM「走沉浸式深色」，标题栏才会跟着变深色，
    /// 顺带把 Win11 的窗口圆角也带出来。
    ///
    /// 属性值 19 是 Win10 20H1 之前的那个、20 是之后的，两个都设一遍兼容性最好。
    /// 弹出层（托盘菜单那种）没有标题栏，用不着这个。
    /// </summary>
    public static void AttachDarkTitleBar(Window window)
    {
        void Apply()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var on = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
        }

        window.SourceInitialized += (_, _) => Apply();

        // 主题切换时重刷：属性值变了要再告诉 DWM 一次
        void OnChanged() => Apply();
        Changed += OnChanged;
        window.Closed += (_, _) => Changed -= OnChanged;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 主题诊断：深浅判定 + 每个画刷的实际颜色（找「深色系统下菜单还是白的」这类问题用）。
    /// </summary>
    public static string Describe()
    {
        var app = System.Windows.Application.Current;
        var parts = new List<string> { $"IsDark={IsDark}", $"任务栏浅色={TaskbarIsLight}" };
        foreach (var key in new[] { "PanelBackgroundBrush", "PanelForegroundBrush", "PopupBackgroundBrush", "PopupBorderBrush", "PopupHoverBrush" })
        {
            var brush = app?.Resources[key] as SolidColorBrush;
            parts.Add(brush is null ? $"{key}=缺失" : $"{key}={brush.Color}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 探针：把「画刷放进 Application.Resources 之后还能不能改色」这件事测明白。
    /// 这是本机实测出来的坑，探针留着，以后换 .NET / 换系统时能第一时间看出行为变没变。
    /// </summary>
    public static string Probe()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return "没有 Application";
        var lines = new List<string>();

        var merged = app.Resources.MergedDictionaries.Count > 0 ? app.Resources.MergedDictionaries[0] : null;
        var fromMerged = merged?["PanelBackgroundBrush"] as SolidColorBrush;
        lines.Add($"合并字典里的画刷={(fromMerged is null ? "无" : (fromMerged.IsFrozen ? "冻结" : "未冻结"))}");

        var probe = new SolidColorBrush(Colors.Red);
        try { app.Resources["__probe_brush__"] = probe; lines.Add("写进应用资源=成功"); }
        catch (Exception error) { lines.Add($"写进应用资源=失败 {error.GetType().Name}"); }
        lines.Add($"写进去之后={(probe.IsFrozen ? "冻结" : "未冻结")}");

        try { probe.Color = Colors.Blue; lines.Add("改颜色=成功"); }
        catch (Exception error) { lines.Add($"改颜色=失败 {error.Message}"); }

        try
        {
            var holder = new System.Windows.Controls.Border();
            holder.Resources["__probe_element__"] = new SolidColorBrush(Colors.Yellow);
            var elementBrush = holder.Resources["__probe_element__"] as SolidColorBrush;
            lines.Add($"元素资源里画刷={(elementBrush is null ? "无" : (elementBrush.IsFrozen ? "冻结" : "未冻结"))}");
        }
        catch (Exception error) { lines.Add($"元素资源=失败 {error.GetType().Name}"); }

        // 换掉整个字典后，已经加载好的 DynamicResource 会不会跟着更新 —— 主题切换就靠这一条
        try
        {
            var first = new ResourceDictionary();
            first["__probe3__"] = new SolidColorBrush(Colors.Red);
            app.Resources.MergedDictionaries.Add(first);

            var element = new System.Windows.Controls.Border { Width = 20, Height = 20 };
            element.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "__probe3__");
            var probeWindow = new System.Windows.Window { Content = element, Width = 60, Height = 60, ShowActivated = false };
            probeWindow.Show();
            probeWindow.UpdateLayout();
            var before = (element.Background as SolidColorBrush)?.Color.ToString() ?? "未解析";

            var second = new ResourceDictionary();
            second["__probe3__"] = new SolidColorBrush(Colors.Blue);
            app.Resources.MergedDictionaries.Remove(first);
            app.Resources.MergedDictionaries.Add(second);
            probeWindow.UpdateLayout();
            var after = (element.Background as SolidColorBrush)?.Color.ToString() ?? "未解析";

            lines.Add($"换字典：前={before} 后={after}");
            probeWindow.Close();
            app.Resources.MergedDictionaries.Remove(second);
        }
        catch (Exception error) { lines.Add($"换字典=失败 {error.GetType().Name} {error.Message}"); }

        return string.Join(" · ", lines);
    }

    /// <summary>读 HKCU 下的一个 DWORD；读不到就返回兜底值（注册表可能被策略挡掉）。</summary>
    private static int ReadDword(string path, string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>强调色够不够亮（够亮就得用深色字压上去，白字看不清）。</summary>
    private static bool AccentIsLight()
    {
        // 相对亮度（Rec. 709 的简化算法）
        var luma = (0.2126 * Accent.R + 0.7152 * Accent.G + 0.0722 * Accent.B) / 255.0;
        return luma > 0.6;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static string ToHex(Color color) => $"#FF{color.R:X2}{color.G:X2}{color.B:X2}";
}
