using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return true;
    }
}

public class BooleanToStatusString : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? "Enabled" : "Disabled";
        }
        return "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue)
        {
            return Visibility.Collapsed;
        }

        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        // 对于非布尔值（如对象类型），null 时隐藏，非 null 时显示
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue)
        {
            return Visibility.Visible;
        }

        if (value is bool boolValue)
        {
            return !boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        // 对于非布尔值（如对象类型），null 时显示，非 null 时隐藏
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility != Visibility.Visible;
        }
        return true;
    }
}

public class PageToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PageType pageType && parameter is string pageStr)
        {
            // 支持多个页面参数（用分号分隔）
            var pages = pageStr.Split(';').Select(p => p.Trim()).ToList();
            foreach (var page in pages)
            {
                if (Enum.TryParse<PageType>(page, out var targetPage) && pageType == targetPage)
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InversePageToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PageType pageType && parameter is string pageStr)
        {
            // 支持多个页面参数（用分号分隔）
            var pages = pageStr.Split(';').Select(p => p.Trim()).ToList();
            foreach (var page in pages)
            {
                if (Enum.TryParse<PageType>(page, out var targetPage) && pageType == targetPage)
                {
                    return Visibility.Collapsed;
                }
            }
            return Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 根据实例信息返回图标路径，优先使用自定义图标
/// </summary>
public class InstanceToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SVL.Core.Stardew.Instance.GamePathInfo instance)
        {
            return instance.GetIconPath();
        }
        // 默认返回 Vanilla 图标
        return "/Images/Vanilla.png";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 枚举值到可见性的转换器（用于切换不同页面）
/// </summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Visibility.Collapsed;

        try
        {
            var enumValue = Enum.Parse(value.GetType(), parameter.ToString());
            return value.Equals(enumValue) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            return Visibility.Collapsed;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 进度条宽度转换器 - 根据进度百分比和容器宽度计算进度条宽度
/// </summary>
public class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double progress &&
            values[1] is double containerWidth)
        {
            // 进度值为 0-100，计算实际宽度
            return (containerWidth * progress) / 100.0;
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 展开/折叠按钮标签转换器
/// </summary>
public class ExpandButtonLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isExpanded)
        {
            return isExpanded ? "收起" : "展开版本";
        }
        return "展开版本";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 根据失败状态返回图标
/// </summary>
public class StatusIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isFailed)
        {
            return isFailed ? "⚠" : "⏳";
        }
        return "⏳";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 根据失败状态返回标题
/// </summary>
public class StatusTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isFailed)
        {
            return isFailed ? "安装失败" : "安装中";
        }
        return "安装中";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 根据失败状态返回颜色
/// </summary>
public class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isFailed)
        {
            return isFailed ? "#FF6B6B" : "#4CAF50";
        }
        return "#4CAF50";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 枚举值到布尔值的转换器（用于RadioButton绑定）
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        try
        {
            var enumValue = Enum.Parse(value.GetType(), parameter.ToString());
            return value.Equals(enumValue);
        }
        catch
        {
            return false;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter != null)
        {
            return Enum.Parse(targetType, parameter.ToString());
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// 字符串非空检查转换器
/// </summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string strValue && !string.IsNullOrWhiteSpace(strValue))
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// URL 检测转换器 - 判断字符串是否为 HTTP(S) URL、WPF Pack URI 或本地文件路径
/// </summary>
public class UrlToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string strValue && !string.IsNullOrWhiteSpace(strValue))
        {
            // 检查 HTTP(S) URL
            if (strValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                strValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }

            // 检查 Pack URI
            if (strValue.StartsWith("pack://application:,,,", StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }

            // 检查本地文件路径（绝对路径，如 C:\ 或 D:\ 等）
            if (System.IO.Path.IsPathRooted(strValue))
            {
                // 检查是否是图片文件扩展名
                var extension = System.IO.Path.GetExtension(strValue).ToLower();
                if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                    extension == ".gif" || extension == ".bmp" || extension == ".ico")
                {
                    return Visibility.Visible;
                }
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 非 URL 检测转换器 - 判断字符串是否为非 URL（用于显示 emoji）
/// </summary>
public class NonUrlToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string strValue && !string.IsNullOrWhiteSpace(strValue))
        {
            // 检查 HTTP(S) URL
            if (strValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                strValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Collapsed;
            }

            // 检查 Pack URI
            if (strValue.StartsWith("pack://application:,,,", StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Collapsed;
            }

            // 检查本地文件路径（绝对路径，如 C:\ 或 D:\ 等）
            if (System.IO.Path.IsPathRooted(strValue))
            {
                // 检查是否是图片文件扩展名
                var extension = System.IO.Path.GetExtension(strValue).ToLower();
                if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                    extension == ".gif" || extension == ".bmp" || extension == ".ico")
                {
                    return Visibility.Collapsed;
                }
            }

            // 其他情况显示为文本（emoji）
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// int > 1 显示，否则隐藏
/// </summary>
public class IntGreaterThanOneToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i)
            return i > 1 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}


/// <summary>
/// 零值检查转换器（用于集合Count为0时显示）
/// </summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 布尔值转换为启用/禁用图标
/// </summary>
public class BoolToEnableDisableIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isEnabled)
        {
            return isEnabled ? "🔛" : "🔴";
        }
        return "🔴";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 布尔值转换为启用/禁用提示文本
/// </summary>
public class BoolToEnableDisableTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isEnabled)
        {
            return isEnabled ? "禁用" : "启用";
        }
        return "启用";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 检查对象是否为指定类型，并转换为可见性
/// 用于只在特定页面显示浮动卡片
/// </summary>
public class TypeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Visibility.Collapsed;

        string? targetTypeString = parameter as string;
        if (string.IsNullOrEmpty(targetTypeString))
            return Visibility.Collapsed;

        Type? targetTypeToCheck = Type.GetType(targetTypeString);
        if (targetTypeToCheck == null)
            return Visibility.Collapsed;

        return targetTypeToCheck.IsInstanceOfType(value) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 正整数显示（页码按钮），-1 隐藏（省略号）
/// </summary>
public class PositiveIntToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// -1 显示（省略号），正整数隐藏
/// </summary>
public class NegativeIntToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue == -1 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 检查页码是否为当前页（用于高亮当前页按钮）
/// </summary>
public class PageNumberToBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is int pageNumber && values[1] is int currentPage)
        {
            return pageNumber == currentPage;
        }
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 字符串颜色值转 Brush 转换器
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorString && !string.IsNullOrWhiteSpace(colorString))
        {
            try
            {
                return new System.Windows.Media.BrushConverter().ConvertFrom(colorString);
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }
        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 根据来源返回对应的颜色（NexusMods 为橙色，Curseforge 为蓝色）
/// </summary>
public class SourceToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string source)
        {
            return source switch
            {
                "NexusMods" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 132, 0)),
                "Curseforge" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 90, 37)),
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
            };
        }
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将长整型数字转换为可读格式（如 1.5M、250K）
/// </summary>
public class LongToNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long number)
        {
            return number switch
            {
                >= 1_000_000 => $"{number / 1_000_000.0:F1}M",
                >= 1_000 => $"{number / 1_000.0:F0}K",
                _ => number.ToString()
            };
        }
        return "0";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将起始角度与扫过角度转换为圆弧 Geometry（用于圆环图）。
/// 约定：角度单位为度，顺时针为正；-90 表示从正上方开始。
/// 参数："radius,center"（可选），例如 "32,40"；默认 radius=32, center=40。
/// </summary>
public class ArcGeometryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (values.Length < 2)
                return System.Windows.Media.Geometry.Empty;

            if (values[0] == null || values[1] == null)
                return System.Windows.Media.Geometry.Empty;

            var startAngle = System.Convert.ToDouble(values[0], culture);
            var sweepAngle = System.Convert.ToDouble(values[1], culture);

            if (double.IsNaN(startAngle) || double.IsNaN(sweepAngle) || sweepAngle <= 0.01)
                return System.Windows.Media.Geometry.Empty;

            double radius = 32;
            double center = 40;
            if (parameter is string p && !string.IsNullOrWhiteSpace(p))
            {
                var parts = p.Split(',');
                if (parts.Length >= 1 && double.TryParse(parts[0].Trim(), out var r))
                    radius = r;
                if (parts.Length >= 2 && double.TryParse(parts[1].Trim(), out var c))
                    center = c;
            }

            var endAngle = startAngle + sweepAngle;
            var start = PointOnCircle(center, center, radius, startAngle);
            var end = PointOnCircle(center, center, radius, endAngle);

            var isLargeArc = sweepAngle > 180;

            var figure = new System.Windows.Media.PathFigure
            {
                StartPoint = start,
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new System.Windows.Media.ArcSegment
            {
                Point = end,
                Size = new System.Windows.Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = System.Windows.Media.SweepDirection.Clockwise,
                RotationAngle = 0,
                IsStroked = true
            });

            var geometry = new System.Windows.Media.PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
        catch
        {
            return System.Windows.Media.Geometry.Empty;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static System.Windows.Point PointOnCircle(double cx, double cy, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var x = cx + radius * Math.Cos(radians);
        var y = cy + radius * Math.Sin(radians);
        return new System.Windows.Point(x, y);
    }
}

/// <summary>
/// 将文件大小（字节）转换为可读格式（KB、MB、GB）
/// </summary>
public class LongToFileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return bytes switch
            {
                >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
                >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
                >= 1_024 => $"{bytes / 1_024.0:F1} KB",
                _ => $"{bytes} B"
            };
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 图片路径转换器 - 空字符串或null时返回默认图片
/// </summary>
public class ImagePathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 获取默认图片路径（可通过参数传递）
        string defaultImagePath = "/Images/Modded.png";
        if (parameter is string paramPath && !string.IsNullOrWhiteSpace(paramPath))
        {
            defaultImagePath = paramPath;
        }

        // 如果值为null或空字符串，返回默认图片
        if (value is string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return defaultImagePath;
            }
            return path;
        }

        return defaultImagePath;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 相等比较转换器 - 用于在 DataTrigger 中比较两个值是否相等
/// </summary>
public class EqualityConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 比较值和参数是否相等
        return object.Equals(value, parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // MultiBinding：比较第一个值和第二个值是否相等
        if (values.Length >= 2)
        {
            return object.Equals(values[0], values[1]);
        }
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


/// <summary>
/// 版本发布类型到颜色的转换器
/// Alpha - 橙色 (#FF9500)
/// Beta - 绿色 (#4CD964)
/// Release - 青色 (#5AC8FA)
/// </summary>
public class ReleaseTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string releaseType)
        {
            return releaseType.ToUpper() switch
            {
                "ALPHA" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 149, 0)),  // 橙色
                "BETA" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 217, 100)),   // 绿色
                "RELEASE" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 200, 250)),  // 青色
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
            };
        }
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 版本发布类型到字母的转换器
/// Alpha -> A
/// Beta -> B
/// Release -> R
/// </summary>
public class ReleaseTypeToLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string releaseType)
        {
            return releaseType.ToUpper() switch
            {
                "ALPHA" => "A",
                "BETA" => "B",
                "RELEASE" => "R",
                _ => "?"
            };
        }
        return "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 布尔值转换为展开/折叠按钮文本
/// true -> "折叠"
/// false -> "展开"
/// </summary>

/// <summary>
/// 版本发布类型到图标的转换器
/// 返回对应的ImageSource
/// </summary>
public class ReleaseTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string releaseType)
        {
            // 根据发布类型创建对应的VisualBrush
            return releaseType.ToUpper() switch
            {
                "ALPHA" => CreateIconBrush("#FF9500", "A"),
                "BETA" => CreateIconBrush("#4CD964", "B"),
                "RELEASE" => CreateIconBrush("#5AC8FA", "R"),
                _ => CreateIconBrush("#5AC8FA", "R")
            };
        }
        return CreateIconBrush("#5AC8FA", "R");
    }

    private System.Windows.Media.VisualBrush CreateIconBrush(string color, string letter)
    {
        var border = new System.Windows.Controls.Border
        {
            Width = 32,
            Height = 32,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)),
            CornerRadius = new System.Windows.CornerRadius(4)
        };

        var textBlock = new System.Windows.Controls.TextBlock
        {
            Text = letter,
            FontSize = 16,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        border.Child = textBlock;

        return new System.Windows.Media.VisualBrush
        {
            Visual = border,
            Stretch = System.Windows.Media.Stretch.None
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
