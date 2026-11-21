using System.Threading.Tasks;
using GitLabPipelineMonitor.Core.Platform;

namespace GitLabPipelineMonitor.Tests;

/// <summary>
/// 屏幕边缘浮标的贴边与命中判定。
///
/// 这块逻辑是纯计算（Win32 那部分在 WPF 侧），所以能直接断言：
/// 浮标要贴死在工作区右边缘、不能跑到屏幕外、
/// 鼠标离细条还有一段距离时也要算「碰到了」（否则那根 5 像素的条根本点不中）。
/// </summary>
public static class ScreenTests
{
    public static Task Run()
    {
        SnapsToRightEdge();
        ClampsInsideWorkArea();
        DetectsNearStrip();
        return Task.CompletedTask;
    }

    /// <summary>贴边：右沿对齐工作区右沿。</summary>
    private static void SnapsToRightEdge()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        var snapped = ScreenSnap.SnapToRightEdge(work, 272, 200, 300);

        Check.Equal(1920.0, snapped.Right, "贴边后右沿应当正好等于工作区右沿");
        Check.Equal(1648.0, snapped.Left, "贴边后左沿应当是右沿减去窗口宽度");
        Check.Equal(300.0, snapped.Top, "纵向位置在工作区内时应当保持不变");
        Program.Ok("贴边：右沿对齐工作区右沿，纵向位置保持");
    }

    /// <summary>夹取：位置超出工作区要被拉回来。</summary>
    private static void ClampsInsideWorkArea()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);

        var tooHigh = ScreenSnap.SnapToRightEdge(work, 100, 200, -80);
        Check.Equal(0.0, tooHigh.Top, "顶到工作区上沿以下时应当夹回上沿");

        var tooLow = ScreenSnap.SnapToRightEdge(work, 100, 200, 2000);
        Check.Equal(840.0, tooLow.Top, "超出下沿时应当夹到工作区底部（1040 - 200）");

        // 窗口比工作区还高：宁可顶到上沿，也不能整块跑到屏幕外
        var huge = ScreenSnap.SnapToRightEdge(work, 100, 2000, 500);
        Check.Equal(0.0, huge.Top, "窗口比工作区高时应当顶在上沿");

        Check.Equal(5.0, ScreenSnap.Clamp(9, 0, 5), "夹取应当落在区间内");
        Check.Equal(0.0, ScreenSnap.Clamp(9, 0, -1), "区间本身反了时应当取下界");
        Program.Ok("夹取：超出工作区的位置会被拉回屏幕内");
    }

    /// <summary>命中判定：细条附近也算碰到，远处不算。</summary>
    private static void DetectsNearStrip()
    {
        var strip = new ScreenRect(1900, 500, 1910, 564);

        Check.True(ScreenSnap.NearStrip(strip, 1905, 530, 1.0, 56, 30, 30, 24),
            "鼠标在细条上应当算碰到");
        Check.True(ScreenSnap.NearStrip(strip, 1850, 530, 1.0, 56, 30, 30, 24),
            "鼠标在细条左侧扩展范围内应当算碰到");
        Check.True(ScreenSnap.NearStrip(strip, 1905, 470, 1.0, 56, 30, 30, 24),
            "鼠标在细条上下扩展范围内应当算碰到");
        Check.False(ScreenSnap.NearStrip(strip, 1700, 530, 1.0, 56, 30, 30, 24),
            "离得远时不该算碰到");
        Check.False(ScreenSnap.NearStrip(strip, 1905, 200, 1.0, 56, 30, 30, 24),
            "纵向离得远时不该算碰到");

        // 200% 缩放：扩展范围按缩放放大，物理像素下的判定范围更宽
        Check.True(ScreenSnap.NearStrip(strip, 1790, 530, 2.0, 56, 30, 30, 24),
            "高缩放下扩展范围应当按比例放大");
        Check.False(ScreenSnap.NearStrip(strip, 1790, 530, 1.0, 56, 30, 30, 24),
            "同样位置在 100% 缩放下不该算碰到");
        Program.Ok("命中判定：细条附近能触发，远了不触发，且随缩放换算");
    }
}
