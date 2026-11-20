using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GitLabPipelineMonitor.Core.GitLab;

namespace GitLabPipelineMonitor.Converters;

/// <summary>#RRGGBB → 画刷。Core 里存的是 hex（这样它能在非 Windows 上编译和测试），
/// 到 UI 层才转成画刷；转换结果缓存，避免每行每帧都新建对象。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    public static Brush BrushFor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) hex = "#8E8E93";
        if (Cache.TryGetValue(hex, out var cached)) return cached;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Cache[hex] = brush;
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BrushFor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>PipelineStatus → 状态色画刷。</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => HexToBrushConverter.BrushFor(value is PipelineStatus status ? status.ColorHex() : "#8E8E93");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>PipelineStatus → 图标 Geometry。与 macOS 版的 symbol 一一对应。</summary>
public sealed class StatusToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is PipelineStatus status ? status.IconKey() : "CircleDashed";
        return Application.Current.TryFindResource("Icon." + key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public static class StatusIconKeys
{
    /// <summary>状态 → 图标资源名，对应 macOS 版的 SF Symbol 选择。</summary>
    public static string IconKey(this PipelineStatus status) => status switch
    {
        PipelineStatus.Success => "CheckCircle",
        PipelineStatus.Failed => "CloseCircle",
        PipelineStatus.Running => "Refresh",
        PipelineStatus.Pending or PipelineStatus.Scheduled => "Clock",
        PipelineStatus.WaitingForResource or PipelineStatus.Preparation => "Hourglass",
        PipelineStatus.Canceled => "MinusCircle",
        PipelineStatus.Manual => "Hand",
        PipelineStatus.Blocked => "Lock",
        PipelineStatus.Skipped => "SkipForward",
        _ => "CircleDashed"
    };
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>非空字符串才显示（覆盖率、触发人这类可选信息）。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>可空值非 null 才显示（耗时、流水线名称）。</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>计数为 0 时隐藏（筛选胶囊上的数字）。</summary>
public sealed class PositiveCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把状态色画刷按给定不透明度变淡，用于胶囊选中底色。</summary>
public sealed class BrushOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SolidColorBrush brush) return Brushes.Transparent;
        var opacity = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0.18;
        var color = brush.Color;
        color.A = (byte)(255 * Math.Clamp(opacity, 0, 1));
        var result = new SolidColorBrush(color);
        result.Freeze();
        return result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
