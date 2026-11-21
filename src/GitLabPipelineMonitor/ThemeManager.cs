using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace GitLabPipelineMonitor;

/// <summary>
/// 明暗主题。WPF 的 SystemColors 在 Win11 上不会读「应用使用浅色主题」那个开关，
/// 永远是浅色那套值 —— 所以深浅要自己判定、自己上色。
/// 
/// 关键实现细节：这些画刷全部由本类用代码创建。XAML 里声明的 SolidColorBrush 会被
/// WPF 自动冻结，冻结后改 Color 会抛 InvalidOperationException（曾导致启动即崩、
/// 托盘图标建不起来）。画刷是全局唯一且不冻结的实例，改一次 Color，所有引用它的
/// 界面立刻跟着变，不需要重新加载窗口。
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
        "AccentBrush", "AccentTextBrush",
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

    /// <summary>读一次系统状态并把配色打到应用资源上；同时开始盯着主题切换。</summary>
    public static void Install()
    {
        EnsureBrushes();
        RefreshFlags();
        Apply();

        // 不挑类别：换主题、换强调色、改明暗开关都从这里过一遍，宁可多刷一次
        _watcher = (_, _) =>
        {
            RefreshFlags();
            Apply();
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += _watcher;
    }

    /// <summary>
    /// 在应用资源里放上自己创建的画刷（不冻结）。放在应用级字典里，优先级高于合并进来的
    /// Theme/Colors.xaml，所以 XAML 里引用同名键拿到的就是我们这份、可以随时改色的实例。
    /// </summary>
    private static void EnsureBrushes()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        foreach (var key in BrushKeys)
        {
            // 已经是自己建的未冻结实例（重复 Install）就复用
            if (app.Resources[key] is SolidColorBrush existing && !existing.IsFrozen) continue;

            // Colors.xaml 里那份可能是冻结的（XAML 声明的 Freezable 会被 WPF 自动冻结），
            // 拿它的颜色起个新的未冻结画刷覆盖上去
            var template = app.Resources[key] as SolidColorBrush;
            var brush = new SolidColorBrush(template?.Color ?? Colors.Black);
            app.Resources[key] = brush;
        }
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
    /// 把当前主题的配色打到画刷上。画刷是全局唯一实例，改 Color 等于全局生效 ——
    /// 界面各处引用的是同一个对象，不需要重新加载窗口。
    /// </summary>
    public static void Apply()
    {
        var dark = IsDark;

        // 取值照着 Win11 的深/浅两套来：主底、条栏、正文、次级、三级、悬停、字段底、分隔线
        Paint("PanelBackgroundBrush", dark ? "#1F1F1F" : "#FFFFFF", 1);
        Paint("BarBackgroundBrush", dark ? "#272727" : "#F3F3F3", 1);
        Paint("PanelForegroundBrush", dark ? "#FFFFFF" : "#1A1A1A", 1);
        Paint("SecondaryTextBrush", dark ? "#FFFFFF" : "#1A1A1A", dark ? 0.72 : 0.62);
        Paint("TertiaryTextBrush", dark ? "#FFFFFF" : "#1A1A1A", dark ? 0.55 : 0.45);
        Paint("RowHoverBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.07 : 0.05);
        Paint("FieldBackgroundBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.06 : 0.05);
        Paint("HairlineBrush", dark ? "#FFFFFF" : "#000000", 0.10);

        // 弹出层（托盘菜单）：Win11 的 flyout 比窗口底再亮一档 / 再白一档
        Paint("PopupBackgroundBrush", dark ? "#2C2C2C" : "#FDFDFD", 1);
        Paint("PopupBorderBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.14 : 0.12);
        Paint("PopupHoverBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.08 : 0.055);
        Paint("AccentBrush", ToHex(Accent), 1);
        Paint("AccentTextBrush", "#FFFFFF", 1);

        // 控件外壳：WPF 自带主题在深色下仍是浅色，这几档由我们自己给
        Paint("ControlBackgroundBrush", dark ? "#2D2D2D" : "#FDFDFD", 1);
        Paint("ControlBorderBrush", dark ? "#FFFFFF" : "#D9D9D9", dark ? 0.16 : 1);
        Paint("ControlHoverBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.06 : 0.04);
        Paint("ControlPressedBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.04 : 0.08);
        Paint("DisabledTextBrush", dark ? "#FFFFFF" : "#000000", 0.36);
        Paint("TabSelectedBrush", dark ? "#FFFFFF" : "#000000", dark ? 0.08 : 0.06);
        Paint("ScrollThumbBrush", dark ? "#FFFFFF" : "#000000", 0.28);

        // 状态色：浅色沿用与 macOS 版一致的取值，深色底上要提亮才跳得出来
        Paint("SuccessBrush", dark ? "#3DDC7A" : "#33B85C", 1);
        Paint("FailedBrush", dark ? "#FF6B6B" : "#F04545", 1);
        Paint("RunningBrush", dark ? "#4DA3FF" : "#388CFA", 1);
        Paint("PendingBrush", dark ? "#FFB84D" : "#F5A624", 1);
        Paint("CanceledBrush", dark ? "#A5ADB6" : "#8C949E", 1);
        Paint("ManualBrush", dark ? "#B18CF0" : "#9E73E6", 1);
        Paint("BlockedBrush", dark ? "#E8884A" : "#D97333", 1);
        Paint("MutedBrush", dark ? "#A5ADB6" : "#8E8E93", 1);
        Paint("StarBrush", dark ? "#FFC94D" : "#F5B833", 1);

        // 通知订阅者配色变了（托盘图标要按新的任务栏深浅重画）
        Changed?.Invoke();
    }

    /// <summary>改一个画刷的颜色（把不透明度折算进 alpha，免得和 brush.Opacity 叠乘）。</summary>
    private static void Paint(string key, string hex, double opacity)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        if (app.Resources[key] is not SolidColorBrush brush) return;
        if (brush.IsFrozen) return;   // 冻结的改不动；正常情况下这里不会命中
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = (byte)(255 * Math.Clamp(opacity, 0, 1));
        if (brush.Color == color) return;
        brush.Color = color;
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

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static string ToHex(Color color) => $"#FF{color.R:X2}{color.G:X2}{color.B:X2}";
}