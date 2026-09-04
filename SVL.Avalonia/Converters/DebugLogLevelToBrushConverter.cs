using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace SVL.Avalonia.ViewModels;

/// <summary>日志级别到画刷的转换器，用于 DebugConsole 逐行着色。</summary>
/// <remarks>
/// INFO/DEBUG 使用主题资源画刷（TextPrimaryBrush/TextSecondaryBrush），保证日间/夜间模式下均有足够对比度；
/// WARN/ERROR 使用固定警示色。主题资源在转换时实时查找，配合 DebugConsoleWindow 的 RefreshThemeResources 同步机制响应主题切换。
/// </remarks>
public sealed class DebugLogLevelToBrushConverter : IValueConverter
{
    public static readonly DebugLogLevelToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = (value as string ?? "INFO").ToUpperInvariant();
        return level switch
        {
            "ERROR" => Brushes.OrangeRed,
            "WARN" => Brushes.Orange,
            "DEBUG" => TryFindResourceBrush("TextSecondaryBrush") ?? Brushes.Gray,
            _ => TryFindResourceBrush("TextPrimaryBrush") ?? Brushes.DimGray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static IBrush? TryFindResourceBrush(string key)
    {
        var resources = Application.Current?.Resources;
        return resources is not null && resources.TryGetValue(key, out var resource)
            ? resource as IBrush
            : null;
    }
}
