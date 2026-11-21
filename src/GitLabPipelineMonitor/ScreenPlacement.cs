using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GitLabPipelineMonitor.Core.Platform;

namespace GitLabPipelineMonitor;

/// <summary>
/// 屏幕与窗口的 Win32 辅助层。
///
/// 为什么要单独一层：多显示器 + 不同缩放的机器上，「窗口在屏幕哪儿」这件事
/// WPF 的 Left/Top（DIP）说不清楚 —— DIP 在不同屏上是不同的尺子，
/// 跨屏拖动时换算来换去必然跑偏。这里一律用**物理像素**说话：
/// 显示器信息、光标位置、窗口矩形都取物理像素，摆放窗口也直接用 SetWindowPos，
/// 绕开 DIP 换算这套糊涂账。
///
/// 纯计算部分（贴边、夹取）在 Core/ScreenGeometry.cs，那边能写单元测试。
/// </summary>
public static class ScreenPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorPrimary = 1;

    private const int MdtEffectiveDpi = 0;

    private const int SwpNoSize = 0x0001;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoOwnerZOrder = 0x0200;

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    // MARK: - 对外接口

    /// <summary>当前鼠标位置（物理像素）。取不到就返回 (0,0)。</summary>
    public static (int X, int Y) GetCursorPosition()
    {
        try
        {
            return GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>所有显示器。枚举失败就返回空表。</summary>
    public static IReadOnlyList<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = Describe(monitor);
                if (info is not null) result.Add(info);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 枚举失败就当只有主屏
        }
        return result;
    }

    /// <summary>主屏。</summary>
    public static MonitorInfo Primary()
    {
        try
        {
            var handle = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MonitorDefaultToNearest);
            var info = Describe(handle);
            if (info is not null) return info;
        }
        catch (Exception)
        {
            // 落到下面去
        }
        return Fallback();
    }

    /// <summary>按设备名找显示器（用户把浮标拖到副屏后记下的就是这个名字）。</summary>
    public static MonitorInfo? ByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;
        foreach (var monitor in All())
        {
            if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return monitor;
        }
        return null;
    }

    /// <summary>某个物理坐标落在哪块屏幕上（松手时判定「用户把浮标放到了哪块屏」）。</summary>
    public static MonitorInfo At(int x, int y)
    {
        try
        {
            var handle = MonitorFromPoint(new POINT { X = x, Y = y }, MonitorDefaultToNearest);
            var info = Describe(handle);
            if (info is not null) return info;
        }
        catch (Exception)
        {
            // 落到主屏
        }
        return Primary();
    }

    /// <summary>窗口矩形（物理像素）。</summary>
    public static ScreenRect GetWindowRect(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            return new ScreenRect(0, 0, 0, 0);
        return new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>
    /// 把窗口挪到物理坐标 (x, y)。只挪位置，不改大小 ——
    /// 大小由 WPF 按 DIP 管（WPF 换算 DPI 时会自己跟上），这里插手容易打架。
    /// </summary>
    public static void MoveTo(IntPtr handle, int x, int y)
    {
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>窗口所在屏幕的缩放（物理像素 / DIP）。取不到按 1.0 算。</summary>
    public static double WindowScale(IntPtr handle)
    {
        try
        {
            var dpi = GetDpiForWindow(handle);
            if (dpi > 0) return dpi / 96.0;
        }
        catch (Exception)
        {
            // 老系统没有这个 API，往下走
        }
        return 1.0;
    }

    /// <summary>
    /// 加上 WS_EX_NOACTIVATE：浮标是贴在屏幕边上的常驻小东西，
    /// 鼠标扫过去不该把用户正在用的窗口抢掉焦点。
    /// </summary>
    public static void MakeNoActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate);
        }
        catch (Exception)
        {
            // 加不上就算了，最多是被抢焦点
        }
    }

    // MARK: - 内部实现

    private static MonitorInfo? Describe(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        return new MonitorInfo(
            info.szDevice,
            new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            new ScreenRect(info.rcWorkArea.Left, info.rcWorkArea.Top, info.rcWorkArea.Right, info.rcWorkArea.Bottom),
            EffectiveScale(monitor),
            (info.dwFlags & MonitorPrimary) != 0);
    }

    /// <summary>显示器的有效缩放。取不到就退回 1.0（96 DPI）。</summary>
    private static double EffectiveScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (Exception)
        {
            // 落到下面
        }
        return 1.0;
    }

    /// <summary>连显示器都枚举不出来时的兜底：按 1920x1080 猜一个。</summary>
    private static MonitorInfo Fallback()
        => new("", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 1.0, true);

    // MARK: - Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWorkArea;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    private static IntPtr GetWindowLong(IntPtr handle, int index) => GetWindowLongPtr(handle, index);

    private static void SetWindowLong(IntPtr handle, int index, IntPtr value)
        => SetWindowLongPtr(handle, index, value);
}

/// <summary>一块显示器的信息。矩形都是物理像素。</summary>
public sealed record MonitorInfo(
    string DeviceName,
    ScreenRect Bounds,
    ScreenRect WorkArea,
    double Scale,
    bool IsPrimary)
{
    /// <summary>设备名空着（枚举失败）时按主屏处理。</summary>
    public string Key => string.IsNullOrEmpty(DeviceName) ? "" : DeviceName;
}
