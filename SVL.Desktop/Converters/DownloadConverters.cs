using System;
using System.Globalization;
using System.Windows.Data;
using SVL.Core.Download;

namespace SVL.Desktop.Converters;

/// <summary>
/// 下载状态转图标
/// </summary>
public class StatusToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DownloadTaskStatus status)
        {
            return status switch
            {
                DownloadTaskStatus.Completed => "✓",
                DownloadTaskStatus.Failed => "✕",
                DownloadTaskStatus.Cancelled => "⊘",
                _ => "📥"
            };
        }
        return "📥";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
