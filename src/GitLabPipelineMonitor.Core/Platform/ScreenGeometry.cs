namespace GitLabPipelineMonitor.Core.Platform;

/// <summary>
/// 屏幕矩形。单位统一用**物理像素**（不是 DIP）——
/// 多显示器、不同缩放的机器上，DIP 和物理像素不是一回事，
/// 跨屏拖动时一律按物理像素算才不会跑偏。
/// </summary>
public readonly record struct ScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    /// <summary>点是否落在这个矩形里（右/下边界算在内，贴边也算命中）。</summary>
    public bool Contains(double x, double y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// 浮标贴边与夹取。纯计算，不碰 Win32 —— 这样能直接写单元测试。
/// </summary>
public static class ScreenSnap
{
    /// <summary>把值夹进 [min, max]。区间本身反了（max &lt; min）时取 min。</summary>
    public static double Clamp(double value, double min, double max)
        => max < min ? min : Math.Clamp(value, min, max);

    /// <summary>
    /// 贴到工作区的**右边缘**：右沿对齐工作区右沿，纵向夹在工作区里。
    /// 窗口比工作区还高时（极端情况）就顶到上沿，别让标题跑出屏幕。
    /// </summary>
    public static ScreenRect SnapToRightEdge(ScreenRect workArea, double windowWidth, double windowHeight, double desiredTop)
    {
        var left = workArea.Right - windowWidth;
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - windowHeight);
        var top = Clamp(desiredTop, workArea.Top, maxTop);
        return new ScreenRect(left, top, left + windowWidth, top + windowHeight);
    }

    /// <summary>
    /// 命中区域：浮标折叠时只是一根细条，鼠标要正好压在那根条上才算命中。
    /// 这里把它往外扩一圈（按显示缩放换算），不然细条太难点中。
    /// </summary>
    public static bool NearStrip(ScreenRect strip, double x, double y, double scale,
        double padLeft, double padTop, double padBottom, double padRight)
    {
        return x >= strip.Left - padLeft * scale
            && x <= strip.Right + padRight * scale
            && y >= strip.Top - padTop * scale
            && y <= strip.Bottom + padBottom * scale;
    }
}
