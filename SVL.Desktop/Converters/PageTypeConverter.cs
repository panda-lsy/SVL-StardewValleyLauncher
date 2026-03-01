using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Converters;

/// <summary>
/// 将 PageType 枚举转换为 bool，用于导航按钮的 IsChecked 绑定
/// </summary>
public class PageTypeToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PageType currentPage && parameter is string targetPage)
        {
            return targetPage switch
            {
                "Launch" => currentPage == PageType.Launch,
                "Download" => currentPage == PageType.Download,
                "DownloadFailure" => currentPage == PageType.DownloadFailure,
                "Mods" => currentPage == PageType.Mods,
                "Settings" => currentPage == PageType.Settings,
                "Instances" => currentPage == PageType.Instances,
                "Modpacks" => currentPage == PageType.Modpacks,
                _ => false
            };
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 当 RadioButton 被点击时，value 是 true
        // 根据 ConverterParameter 返回对应的 PageType
        if (value is bool isChecked && isChecked && parameter is string targetPage)
        {
            return targetPage switch
            {
                "Launch" => PageType.Launch,
                "Download" => PageType.Download,
                "DownloadFailure" => PageType.DownloadFailure,
                "Mods" => PageType.Mods,
                "Settings" => PageType.Settings,
                "Instances" => PageType.Instances,
                "Modpacks" => PageType.Modpacks,
                _ => DependencyProperty.UnsetValue
            };
        }
        return DependencyProperty.UnsetValue;
    }
}
