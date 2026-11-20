using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GitLabPipelineMonitor.Converters;

/// <summary>资源名（如 "Alert"）→ 图标 Geometry（"Icon.Alert"）。
/// 空状态的图标种类由视图模型决定，用这个转换器在绑定时查资源。</summary>
public sealed class IconResourceConverter : IValueConverter
{
    public static readonly IconResourceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrEmpty(key)) return null;
        return Application.Current.TryFindResource("Icon." + key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
